// Tools/DocSiteGen/Program.cs
//
// Static site generator for the Fracturing Fog documentation tree.
//
//   dotnet run --project Tools/DocSiteGen
//
// Walks Docs/**/*.md (skipping Docs/site/ and Docs/Images/_placeholders/),
// converts each file via Markdig with the AdvancedExtensions pipeline, and
// emits Docs/site/<relative-path>.html wrapped in a shared shell that
// loads KaTeX for inline + block LaTeX (`$...$`, `$$...$$`) and Prism for
// syntax-highlighted code blocks. The shell also synthesises a sidebar
// navigation grouped by top-level folder (User / Technical / Images).
//
// Output is fully static — drop Docs/site/ on GitHub Pages, an S3 bucket,
// or open `index.html` directly in a browser with no server required.
//
// Image references (`![](../Images/foo.png)`) resolve naturally because
// the on-disk Docs/Images/ tree is mirrored under Docs/site/Images/ by a
// recursive copy pass before page emission.

using System.Text;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Renderers.Html;
using Markdig.Syntax;

namespace FracturingFog.Tools.DocSiteGen;

internal static class Program
{
    private static int Main(string[] args)
    {
        string root = LocateRepoRoot();
        string docsDir = Path.Combine(root, "Docs");
        string outDir  = Path.Combine(docsDir, "site");

        if (!Directory.Exists(docsDir))
        {
            Console.Error.WriteLine($"Docs/ not found at {docsDir}");
            return 1;
        }

        Directory.CreateDirectory(outDir);

        var pipeline = new MarkdownPipelineBuilder()
            .UseAdvancedExtensions()       // tables, footnotes, task lists, emphasis extras, autolinks, etc.
            .UseEmojiAndSmiley()
            .UseGenericAttributes()
            .UseAutoIdentifiers()           // turn headings into id anchors
            .UseSoftlineBreakAsHardlineBreak()
            .Build();

        var pages = new List<PageInfo>();
        foreach (var mdPath in Directory.EnumerateFiles(docsDir, "*.md", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(docsDir, mdPath).Replace('\\', '/');
            if (rel.StartsWith("site/", StringComparison.OrdinalIgnoreCase)) continue;
            if (rel.StartsWith("Images/", StringComparison.OrdinalIgnoreCase)) continue;
            pages.Add(new PageInfo(mdPath, rel));
        }

        // Mirror Docs/Images/ into Docs/site/Images/ so relative `![](../Images/foo.png)`
        // refs Just Work in the rendered HTML.
        string imagesSrc = Path.Combine(docsDir, "Images");
        string imagesDst = Path.Combine(outDir, "Images");
        if (Directory.Exists(imagesSrc))
            MirrorDirectory(imagesSrc, imagesDst,
                            skipPredicate: p => p.Contains("/_placeholders/", StringComparison.OrdinalIgnoreCase));

        string sidebar = BuildSidebar(pages);

        foreach (var page in pages)
        {
            string md = File.ReadAllText(page.SourcePath);
            string title = ExtractTitle(md, page.RelativePath);
            string body = Markdown.ToHtml(md, pipeline);
            body = RewriteMdLinksToHtml(body, page.RelativePath, pages);
            body = RewriteTocAnchors(body);
            string html = WrapShell(title, body, sidebar, page.RelativePath, pages);

            string outPath = Path.Combine(outDir,
                Path.ChangeExtension(page.RelativePath.Replace('/', Path.DirectorySeparatorChar), ".html"));
            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
            File.WriteAllText(outPath, html, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        File.WriteAllText(Path.Combine(outDir, "index.html"),
            BuildLandingPage(pages, sidebar),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        File.WriteAllText(Path.Combine(outDir, "site.css"), SiteCss);

        Console.WriteLine($"Wrote {pages.Count} page(s) → {outDir}");
        return 0;
    }

    private static string LocateRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "FracturingFogCLD.sln"))) return dir.FullName;
            dir = dir.Parent;
        }
        // Fall back to the current working directory when the binary is
        // invoked from an unexpected location (CI artifact directory, etc.)
        return Directory.GetCurrentDirectory();
    }

    private static void MirrorDirectory(string src, string dst, Func<string, bool>? skipPredicate = null)
    {
        Directory.CreateDirectory(dst);
        foreach (var srcFile in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            string normalised = srcFile.Replace('\\', '/');
            if (skipPredicate != null && skipPredicate(normalised)) continue;
            string rel = Path.GetRelativePath(src, srcFile);
            string outPath = Path.Combine(dst, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
            File.Copy(srcFile, outPath, overwrite: true);
        }
    }

    private static string ExtractTitle(string md, string fallback)
    {
        foreach (var line in md.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.StartsWith("# ")) return line[2..].Trim();
        }
        return Path.GetFileNameWithoutExtension(fallback);
    }

    private static string BuildSidebar(List<PageInfo> pages)
    {
        // Group by the first path segment.
        var groups = pages
            .GroupBy(p => p.RelativePath.Contains('/') ? p.RelativePath[..p.RelativePath.IndexOf('/')] : "Root")
            .OrderBy(g => g.Key switch { "User" => 0, "Technical" => 1, _ => 2 });

        var sb = new StringBuilder();
        sb.AppendLine("<nav class='sidebar'><h2><a href='{ROOT}/index.html'>Fracturing Fog Docs</a></h2>");
        foreach (var group in groups)
        {
            sb.AppendLine($"<h3>{group.Key}</h3><ul>");
            foreach (var page in group.OrderBy(p => p.RelativePath))
            {
                string href = "{ROOT}/" + Path.ChangeExtension(page.RelativePath, ".html");
                string label = Path.GetFileNameWithoutExtension(page.RelativePath);
                sb.AppendLine($"  <li><a href='{href}' data-href='{Path.ChangeExtension(page.RelativePath, ".html")}'>{label}</a></li>");
            }
            sb.AppendLine("</ul>");
        }
        sb.AppendLine("</nav>");
        return sb.ToString();
    }

    private static string WrapShell(string title, string body, string sidebar, string relativePath, List<PageInfo> pages)
    {
        // Compute relative path from this page back to the site root.
        int depth = relativePath.Count(c => c == '/');
        string rootPrefix = depth == 0 ? "." : string.Join('/', Enumerable.Repeat("..", depth));
        string sidebarResolved = sidebar.Replace("{ROOT}", rootPrefix);

        return $@"<!DOCTYPE html>
<html lang='en'>
<head>
<meta charset='utf-8'>
<meta name='viewport' content='width=device-width, initial-scale=1'>
<title>{HtmlEscape(title)} — Fracturing Fog</title>
<link rel='stylesheet' href='{rootPrefix}/site.css'>
<link rel='stylesheet' href='https://cdn.jsdelivr.net/npm/katex@0.16.10/dist/katex.min.css'>
<link rel='stylesheet' href='https://cdn.jsdelivr.net/npm/prismjs@1.29.0/themes/prism-tomorrow.min.css'>
</head>
<body>
{sidebarResolved}
<main>
<article>
{body}
</article>
<footer>Source: <code>Docs/{HtmlEscape(relativePath)}</code></footer>
</main>
<script src='https://cdn.jsdelivr.net/npm/prismjs@1.29.0/components/prism-core.min.js'></script>
<script src='https://cdn.jsdelivr.net/npm/prismjs@1.29.0/plugins/autoloader/prism-autoloader.min.js'></script>
<script defer src='https://cdn.jsdelivr.net/npm/katex@0.16.10/dist/katex.min.js'></script>
<script defer src='https://cdn.jsdelivr.net/npm/katex@0.16.10/dist/contrib/auto-render.min.js'
        onload='renderMathInElement(document.body, {{
            delimiters: [
              {{left: ""$$"", right: ""$$"", display: true}},
              {{left: ""\\["", right: ""\\]"", display: true}},
              {{left: ""$"",  right: ""$"",  display: false}},
              {{left: ""\\("", right: ""\\)"", display: false}}
            ],
            throwOnError: false
        }});'></script>
</body>
</html>";
    }

    private static string BuildLandingPage(List<PageInfo> pages, string sidebar)
    {
        var sidebarResolved = sidebar.Replace("{ROOT}", ".");
        var groups = pages
            .GroupBy(p => p.RelativePath.Contains('/') ? p.RelativePath[..p.RelativePath.IndexOf('/')] : "Root")
            .OrderBy(g => g.Key switch { "User" => 0, "Technical" => 1, _ => 2 });

        var sb = new StringBuilder();
        sb.AppendLine("<h1>Fracturing Fog Documentation</h1>");
        sb.AppendLine("<p>Real-time high-precision Mandelbrot &amp; friends explorer. Browse the User guides for end-user features or the Technical docs for architecture and contribution notes.</p>");
        foreach (var group in groups)
        {
            sb.AppendLine($"<h2>{group.Key}</h2><ul class='landing-list'>");
            foreach (var page in group.OrderBy(p => p.RelativePath))
            {
                string href = Path.ChangeExtension(page.RelativePath, ".html");
                string title = Path.GetFileNameWithoutExtension(page.RelativePath);
                sb.AppendLine($"  <li><a href='{href}'>{HtmlEscape(title)}</a></li>");
            }
            sb.AppendLine("</ul>");
        }

        return $@"<!DOCTYPE html>
<html lang='en'>
<head>
<meta charset='utf-8'>
<meta name='viewport' content='width=device-width, initial-scale=1'>
<title>Fracturing Fog Documentation</title>
<link rel='stylesheet' href='site.css'>
</head>
<body>
{sidebarResolved}
<main><article>
{sb}
</article></main>
</body>
</html>";
    }

    private static string HtmlEscape(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    // Markdig preserves `.md` in href targets verbatim. Browsers hitting the
    // static site under Docs/site/ see a 404 because we only emit `.html`.
    // Rewrite intra-doc relative links so `[label](Foo.md)` becomes
    // `<a href="Foo.html">label</a>`, leaving absolute URLs, mailto:, and
    // pure-anchor fragments alone. Preserves any trailing `#anchor` slice.
    //
    // Cross-folder safety net: when the literal href would 404 (e.g. a User/
    // page links to `Architecture-Overview.md` without `../Technical/`), look
    // the bare filename up across every page in the tree and emit a corrected
    // relative path. Mirrors the in-app HelpViewer's User/ ↔ Technical/
    // fallback so the static site renders the same links live.
    private static readonly Regex MdHrefRegex = new(
        @"href=""(?!https?://|mailto:|#)(?<base>[^""#]+?)\.md(?<anchor>#[^""]*)?""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static string RewriteMdLinksToHtml(string html, string currentRelative, List<PageInfo> pages)
    {
        string currentDir = currentRelative.Contains('/')
            ? currentRelative[..currentRelative.LastIndexOf('/')]
            : "";

        // Bare-name index — every page keyed by filename without extension (case insensitive).
        var bareIndex = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in pages)
        {
            string bare = Path.GetFileNameWithoutExtension(p.RelativePath);
            // Last writer wins is fine: identically-named pages are not expected.
            bareIndex[bare] = p.RelativePath;
        }
        var pageSet = new HashSet<string>(pages.Select(p => p.RelativePath), StringComparer.OrdinalIgnoreCase);

        return MdHrefRegex.Replace(html, m =>
        {
            string href = m.Groups["base"].Value;
            string anchor = m.Groups["anchor"].Value;

            // Literal join — what does the link resolve to today?
            string resolved = JoinRelative(currentDir, href + ".md");
            if (pageSet.Contains(resolved))
                return $"href=\"{href}.html{anchor}\"";

            // Try bare-name lookup. Lets `[X](X.md)` survive a folder move.
            string bareName = Path.GetFileNameWithoutExtension(href);
            if (bareIndex.TryGetValue(bareName, out var targetRel))
            {
                string fixedHref = MakeRelative(currentDir, Path.ChangeExtension(targetRel, ".html"));
                return $"href=\"{fixedHref}{anchor}\"";
            }

            // Nothing matched — fall back to the literal swap and let the
            // 404 surface as a visible authoring error.
            return $"href=\"{href}.html{anchor}\"";
        });
    }

    // Hand-authored TOCs use `[label](#N-some-thing)` where N is a step
    // number. Markdig's GFM auto-identifier strips leading non-letter
    // characters AND collapses punctuation runs, so `### 8. Camera + Lighting`
    // becomes `id="camera-lighting"`, not `#8-camera--lighting`. Result: every
    // numbered TOC link 404s.
    //
    // Fix: collect every heading id on the page, then for each `<a href="#X">`
    // that doesn't resolve, try (a) stripping leading-digit-dash prefix and
    // (b) collapsing `--` to `-`. If the transformed target exists, rewrite.
    // Falls back to the literal anchor when no candidate matches so authoring
    // errors stay visible.
    private static readonly Regex HeadingIdRegex = new(
        @"<h\d+\s+id=""(?<id>[^""]+)""",
        RegexOptions.Compiled);

    private static readonly Regex AnchorHrefRegex = new(
        @"href=""#(?<anchor>[^""]+)""",
        RegexOptions.Compiled);

    private static string RewriteTocAnchors(string html)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in HeadingIdRegex.Matches(html))
            ids.Add(m.Groups["id"].Value);

        return AnchorHrefRegex.Replace(html, m =>
        {
            string anchor = m.Groups["anchor"].Value;
            if (ids.Contains(anchor)) return m.Value;

            string candidate = anchor;
            // Markdig's GFM auto-id strips ALL leading non-letter characters
            // (digits, dashes, dots), then slugifies. `### 7. 3D Lighting` →
            // `d-lighting-...` because both `7` and `3` get peeled before the
            // first letter. Mirror by stripping every leading `[\d-]` run.
            int i = 0;
            while (i < candidate.Length && (char.IsDigit(candidate[i]) || candidate[i] == '-')) i++;
            candidate = candidate[i..];
            // Collapse consecutive dashes (TOC author writes `+ ` as `--`).
            while (candidate.Contains("--")) candidate = candidate.Replace("--", "-");
            candidate = candidate.Trim('-');

            if (candidate.Length > 0 && ids.Contains(candidate))
                return $"href=\"#{candidate}\"";

            return m.Value;
        });
    }

    private static string JoinRelative(string dir, string rel)
    {
        if (string.IsNullOrEmpty(dir)) return Normalise(rel);
        string combined = dir + "/" + rel;
        return Normalise(combined);
    }

    private static string MakeRelative(string fromDir, string toPath)
    {
        if (string.IsNullOrEmpty(fromDir)) return toPath;
        var fromParts = fromDir.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var toParts   = toPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        int common = 0;
        while (common < fromParts.Length && common < toParts.Length &&
               string.Equals(fromParts[common], toParts[common], StringComparison.OrdinalIgnoreCase))
            common++;
        var sb = new StringBuilder();
        for (int i = common; i < fromParts.Length; i++) sb.Append("../");
        for (int i = common; i < toParts.Length; i++)
        {
            sb.Append(toParts[i]);
            if (i < toParts.Length - 1) sb.Append('/');
        }
        return sb.ToString();
    }

    // Collapse `a/b/../c` → `a/c`. Markdig output should already be normalised
    // but contributor markdown is not — handle both shapes.
    private static string Normalise(string path)
    {
        var parts = new List<string>();
        foreach (var seg in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (seg == ".") continue;
            if (seg == ".." && parts.Count > 0 && parts[^1] != "..") { parts.RemoveAt(parts.Count - 1); continue; }
            if (seg == "..") { parts.Add(".."); continue; }
            parts.Add(seg);
        }
        return string.Join('/', parts);
    }

    private sealed record PageInfo(string SourcePath, string RelativePath);

    // Single dark-themed stylesheet matching the in-app FloatingHelp viewer.
    private const string SiteCss = @"
:root {
  --bg: #161616;
  --bg-elev: #1c1c1c;
  --fg: #DCDCDC;
  --fg-dim: #9C9C9C;
  --accent: #9CBDFF;
  --accent2: #FFCC00;
  --code: #9CDCFE;
  --border: #2A2A2A;
  --max-width: 880px;
}
* { box-sizing: border-box; }
body {
  margin: 0;
  background: var(--bg);
  color: var(--fg);
  font-family: 'Segoe UI', system-ui, -apple-system, sans-serif;
  font-size: 15px;
  line-height: 1.55;
  display: grid;
  grid-template-columns: minmax(220px, 280px) 1fr;
}
.sidebar {
  background: var(--bg-elev);
  border-right: 1px solid var(--border);
  padding: 18px 16px;
  position: sticky; top: 0;
  height: 100vh;
  overflow-y: auto;
}
.sidebar h2 { font-size: 16px; margin: 0 0 8px 0; }
.sidebar h2 a { color: var(--accent2); text-decoration: none; }
.sidebar h3 { font-size: 12px; color: var(--fg-dim); text-transform: uppercase; letter-spacing: 0.08em; margin: 14px 0 4px; }
.sidebar ul { list-style: none; padding-left: 0; margin: 0; }
.sidebar li { margin: 2px 0; }
.sidebar a { color: var(--accent); text-decoration: none; font-size: 13px; display: block; padding: 3px 6px; border-radius: 4px; }
.sidebar a:hover { background: rgba(156,189,255,0.08); }
main {
  padding: 24px 36px;
  max-width: var(--max-width);
  margin: 0;
}
article h1 { font-size: 28px; border-bottom: 1px solid var(--border); padding-bottom: 8px; color: #FFFFFF; }
article h2 { font-size: 22px; margin-top: 1.6em; color: #FFFFFF; }
article h3 { font-size: 18px; margin-top: 1.4em; color: #D4D4D4; }
article h4 { font-size: 15px; margin-top: 1.2em; color: #B4B4B4; }
article p, article li { color: var(--fg); }
article a { color: var(--accent); }
article code {
  font-family: Consolas, 'Cascadia Code', monospace;
  background: rgba(156,220,254,0.08);
  color: var(--code);
  padding: 1px 4px;
  border-radius: 3px;
  font-size: 0.92em;
}
article pre {
  background: #101010;
  border: 1px solid var(--border);
  padding: 12px 14px;
  border-radius: 4px;
  overflow-x: auto;
}
article pre code { background: none; padding: 0; }
article blockquote {
  border-left: 3px solid var(--accent);
  background: rgba(156,189,255,0.04);
  padding: 10px 14px;
  margin: 12px 0;
  color: var(--fg);
}
article img { max-width: 100%; height: auto; display: block; margin: 12px 0; }
article table {
  border-collapse: collapse;
  margin: 14px 0;
  overflow-x: auto;
  display: block;
  max-width: 100%;
}
article th, article td {
  border: 1px solid var(--border);
  padding: 6px 10px;
  text-align: left;
  vertical-align: top;
}
article th { background: #222; color: #fff; }
article tr:nth-child(even) td { background: rgba(255,255,255,0.02); }
.landing-list { columns: 2; gap: 30px; }
footer {
  margin-top: 40px;
  padding-top: 14px;
  border-top: 1px solid var(--border);
  color: var(--fg-dim);
  font-size: 12px;
}
.katex-display { overflow-x: auto; overflow-y: hidden; }
@media (max-width: 720px) {
  body { grid-template-columns: 1fr; }
  .sidebar { position: relative; height: auto; }
}
";
}
