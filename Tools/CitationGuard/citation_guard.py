#!/usr/bin/env python3
# SPDX-License-Identifier: AGPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 Bradley Brown
"""
Citation guard - enforces the project's source-citation contract (CLAUDE.md,
CONTRIBUTING.md "Source citation contract").

Every author, paper or external resource the code or docs lean on must be
credited in Docs/Resources-Bibliography.md. This script fails (exit 1) when:

  1. a scholarly-style citation in tracked source/docs ("Keinert et al. 2014",
     "Tan Lei (1990)", "Daza 2016", "Ott & Tel 1993") names a surname that the
     bibliography does not mention;
  2. an external URL in tracked source/docs points at a host the bibliography
     does not mention;
  3. a link into Resources-Bibliography.md#<anchor> has no matching anchor
     (explicit <a id> or a heading slug);
  4. with --base <git-ref>: a credit present in the bibliography at <git-ref>
     (a **bold** author / source name, a <URL>, an <a id> anchor) is gone.
     Credits are corrected or moved, never removed: a confirmed correction
     keeps the old form under "Corrected attributions" in the bibliography.

A detected name / host that is NOT a citation (a word that happens to sit next
to a year, a test fixture URL) goes in allowlist.txt beside this script, one
entry per line, with a comment saying why. Never allowlist a real source: add
it to the bibliography instead.

Usage:  python Tools/CitationGuard/citation_guard.py [repo-root] [--base <git-ref>]
"""

import os
import re
import subprocess
import sys
import unicodedata

BIB = "Docs/Resources-Bibliography.md"
HERE = os.path.dirname(os.path.abspath(__file__))
EXTS = (".cs", ".md", ".hlsl", ".comp", ".glsl", ".frag", ".axaml", ".py", ".ps1", ".sh")

# Path prefixes that are not project prose (generated / vendored / logs).
SKIP_PREFIXES = ("bin/", "obj/", "out/", "BenchmarkDotNet.Artifacts/", "FracturedRefract/")

NAME = r"[A-Z][\w'’]+(?:-[A-Z][\w'’]+)?"
JOIN = r"(?:\s*[–—&/]\s*|\s+and\s+)"
YEAR = r"(?:1[6-9]|20)\d{2}"
CITATION_PATTERNS = [
    # Keinert et al. 2014 / Chu et al., SIGGRAPH Asia 2013 / Wang et al. (2004)
    re.compile(rf"\b({NAME})\s+et\s+al\.?,?\s*(?:\(|[A-Z][\w ]{{0,30}}?,?\s*)?({YEAR})"),
    # Ott & Tel 1993 / Grebogi-McDonald-Ott-Yorke 1983 / Daza 2016 / Tan Lei (1990)
    re.compile(rf"\b((?:{NAME}{JOIN})*{NAME}(?:\s+{NAME})?)\s*(?:,\s*|\(\s*|\s+)({YEAR})\)?"),
]
URL = re.compile(r"https?://([A-Za-z0-9.-]+\.[A-Za-z]{2,})")

# Words that commonly sit before a year without being a citation.
STOP = set("""
January February March April May June July August September October November
December Jan Feb Mar Apr Jun Jul Aug Sep Sept Oct Nov Dec Q1 Q2 Q3 Q4
The This That These Those A An In On At By For From To Of And Or As Is Was Since
Until Before After Through During Circa Updated Created Started Shipped Landed
Merged Done Status Fixed Resolved Confirmed Audit Re-audit Decisions Update
Report Finding Correction Deferred Superseded Bugged Re-scoped Built Wait Best
Round Join Equal Min Max Random Snapshot Uniforms Req Created Standard's Ultra
Visual Studio Windows Avalonia Copyright SPDX-FileCopyrightText Year Asia SIGGRAPH
Fit Create Read Rows Parity Rabbit Sciences
""".split())


def ascii_fold(s):
    return "".join(c for c in unicodedata.normalize("NFKD", s) if not unicodedata.combining(c)).lower()


def tracked_files(root):
    out = subprocess.run(["git", "-C", root, "ls-files"], capture_output=True, text=True, check=True).stdout
    for f in out.splitlines():
        if f.endswith(EXTS) and not f.startswith(SKIP_PREFIXES) and f != BIB:
            yield f


def load_allowlist():
    names, hosts, anchors = set(), set(), set()
    path = os.path.join(HERE, "allowlist.txt")
    if os.path.exists(path):
        for line in open(path, encoding="utf-8"):
            line = line.split("#", 1)[0].strip()
            if not line:
                continue
            if line.startswith("host:"):
                hosts.add(line[5:].strip().lower())
            elif line.startswith("anchor:"):
                anchors.add(line[7:].strip())
            else:
                names.add(ascii_fold(line))
    return names, hosts, anchors


def looks_like_identifier(tok):
    # TraceGrid, S-X9, HD, InlineData, Shewchuk's -> strip possessive first
    if any(ch.isdigit() for ch in tok):
        return True
    if tok.isupper():
        return True
    return re.search(r"[a-z][A-Z]", tok) is not None


def slugify(heading):
    s = heading.strip().lower()
    s = re.sub(r"[^\w\- ]", "", s, flags=re.UNICODE)
    return s.replace(" ", "-")


def bibliography_anchors(bib_text):
    anchors = set(re.findall(r'<a\s+id="([^"]+)"', bib_text))
    for m in re.finditer(r"^#{1,6}\s+(.+?)\s*$", bib_text, re.M):
        anchors.add(slugify(m.group(1)))
    return anchors


def credits(bib_text):
    return (set(re.findall(r"\*\*([^*]+?)\.?\*\*", bib_text))
            | set(re.findall(r"<(https?://[^>]+)>", bib_text))
            | set(re.findall(r'<a\s+id="([^"]+)"', bib_text)))


def dropped_credits(root, base, bib_text):
    old = subprocess.run(["git", "-C", root, "show", f"{base}:{BIB}"],
                         capture_output=True, text=True, encoding="utf-8")
    if old.returncode != 0:
        print(f"Citation guard: cannot read {BIB} at {base}: {old.stderr.strip()}")
        sys.exit(2)
    return sorted(c for c in credits(old.stdout) if c not in bib_text)


def main():
    args = sys.argv[1:]
    base = None
    if "--base" in args:
        i = args.index("--base")
        base = args[i + 1]
        del args[i:i + 2]
    root = args[0] if args else os.getcwd()
    bib_text = open(os.path.join(root, BIB), encoding="utf-8").read()
    bib_fold = ascii_fold(bib_text)
    allow_names, allow_hosts, allow_anchors = load_allowlist()
    anchors = bibliography_anchors(bib_text) | allow_anchors

    missing_names, missing_hosts, broken_anchors = {}, {}, {}
    for f in tracked_files(root):
        try:
            text = open(os.path.join(root, f), encoding="utf-8", errors="replace").read()
        except OSError:
            continue
        for lineno, line in enumerate(text.splitlines(), 1):
            for pat in CITATION_PATTERNS:
                for m in pat.finditer(line):
                    for tok in re.split(JOIN, m.group(1)):
                        tok = re.sub(r"['’]s$", "", tok.strip())
                        for part in tok.split():
                            if part in STOP or looks_like_identifier(part) or len(part) < 3:
                                continue
                            key = ascii_fold(part)
                            # Strain-Clark / Heiland-Allen: credited when every part is.
                            if key in allow_names or all(
                                    re.search(rf"\b{re.escape(k)}\b", bib_fold) for k in key.split("-")):
                                continue
                            missing_names.setdefault(part, f"{f}:{lineno}")
            for m in URL.finditer(line):
                host = m.group(1).lower().rstrip(".")
                bare = host[4:] if host.startswith("www.") else host
                if bare in allow_hosts or host in allow_hosts or bare in bib_fold:
                    continue
                missing_hosts.setdefault(bare, f"{f}:{lineno}")
            for m in re.finditer(r"Resources-Bibliography\.md#([A-Za-z0-9_\-]+)", line):
                if m.group(1) not in anchors:
                    broken_anchors.setdefault(m.group(1), f"{f}:{lineno}")

    removed = {c: base for c in dropped_credits(root, base, bib_text)} if base else {}

    ok = True
    for title, items in (("Credits REMOVED from the bibliography (never allowed)", removed),
                         ("Cited names missing from the bibliography", missing_names),
                         ("External hosts missing from the bibliography", missing_hosts),
                         ("Broken Resources-Bibliography.md anchors", broken_anchors)):
        if items:
            ok = False
            print(f"\n{title} ({len(items)}):")
            for k in sorted(items, key=str.lower):
                print(f"  {k:<40} {'present at' if items is removed else 'first seen'} {items[k]}")
    if removed:
        print("\nA correction keeps the old form under \"Corrected attributions\" in " + BIB +
              " (date, PR, reason); a renamed anchor keeps its old <a id>.")
    if not ok:
        print("\nSource citation contract: credit every source in " + BIB +
              " (see CONTRIBUTING.md). Only non-citations may go in Tools/CitationGuard/allowlist.txt.")
        return 1
    print("Citation guard: all cited names, external hosts and bibliography anchors are accounted for.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
