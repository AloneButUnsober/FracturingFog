// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Engine/Imaging/Qr/QrExport.cs
//
// #1305 - the one entry point the poster / batch / Control Center paths call:
// validate the options, encode, composite into the final display buffer, and
// (after the file is written) verify the SAVED image scans.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace FracturingFog.Imaging.Qr
{
    /// <summary>What a QR export produced.</summary>
    public sealed record QrExportReport(QrCode Code, QrLayout Layout, QrVerifyResult? Verify)
    {
        /// <summary>#1402 — what shaping achieved; null when the code was not shaped.</summary>
        public QrShapeStats? Shape { get; init; }
    }

    /// <summary>The code composited into the image, where, and (when shaped) how
    /// much of it follows the image.</summary>
    public sealed record QrApplyResult(QrCode Code, QrLayout Layout, QrShapeStats? Shape)
    {
        public void Deconstruct(out QrCode code, out QrLayout layout) { code = Code; layout = Layout; }
    }

    public static class QrExport
    {
        /// <summary>Null when the options are usable, else the reason.</summary>
        public static string? Validate(QrCodeOptions opt)
        {
            if (opt == null) return "no QR options";
            if (string.IsNullOrEmpty(opt.Payload)) return "the QR payload is empty";
            int bytes = Encoding.UTF8.GetByteCount(opt.Payload);
            if (bytes > QrCodeOptions.MaxPayloadBytes)
                return $"the QR payload is {bytes} bytes; the largest QR code holds {QrCodeOptions.MaxPayloadBytes}";
            if (opt.ModulePixels < 0 || opt.ModulePixels > QrCodeOptions.MaxModulePixels)
                return $"QR module size must be 0 (auto) .. {QrCodeOptions.MaxModulePixels} px";
            if (opt.Mask is < -1 or > 7)
                return "QR mask must be 0..7 (or -1 = auto)";
            if (opt.MinVersion is < QrEncoder.MinVersion or > QrEncoder.MaxVersion)
                return "QR version must be 1..40";
            if (opt.DotFraction < QrCodeOptions.MinDotFraction || opt.DotFraction > QrCodeOptions.MaxDotFraction)
                return $"QR dot fraction must be {QrCodeOptions.MinDotFraction} .. {QrCodeOptions.MaxDotFraction}";
            try { Encode(opt, -1); }
            catch (ArgumentException ex) { return ex.Message; }
            return null;
        }

        /// <summary>The unshaped code for these options (honours MinVersion).</summary>
        public static QrCode Encode(QrCodeOptions opt, int mask)
            => QrEncoder.Encode(opt.Payload, opt.ErrorCorrection, minVersion: opt.MinVersion, mask: mask);

        /// <summary>Null when <paramref name="opt"/> encodes and fits a
        /// <paramref name="w"/> x <paramref name="h"/> image, else why not - so a
        /// caller can refuse before a long render.</summary>
        public static string? Preflight(QrCodeOptions opt, int w, int h)
        {
            if (Validate(opt) is { } err) return err;
            var code = Encode(opt, -1);
            try { QrCompositor.Layout(w, h, code.Size, opt.ModulePixels); }
            catch (ArgumentException ex) { return ex.Message; }
            return null;
        }

        /// <summary>Encode and composite in place. Throws ArgumentException when the
        /// payload does not fit or the image is too small for the code.
        ///
        /// The mask is chosen by DECODING, not only by the spec penalty: a real
        /// detector can lock onto a finder lookalike that the data plus the
        /// fractal happen to draw (QrEncoder.PseudoFinderPenalty), and which mask
        /// avoids that depends on the image. Candidates are tried in penalty
        /// order (a preferred <see cref="QrCodeOptions.Mask"/> first) and the
        /// first composite that passes every QrVerifier pass wins; if none
        /// does, the one passing the most passes is kept (and the post-save
        /// verify reports it).</summary>
        public static QrApplyResult Apply(uint[] buf, int w, int h, QrCodeOptions opt)
        {
            string? err = Validate(opt);
            if (err != null) throw new ArgumentException(err, nameof(opt));

            // #1402 — shaped candidates follow the image under every mask.
            Func<int, int, (bool, double)>? target = opt.Shape ? ShapeTarget(buf, w, h, opt) : null;
            var stats = new Dictionary<QrCode, QrShapeStats>();
            var candidates = new List<(QrCode Code, int Penalty)>(8);
            for (int m = 0; m < 8; m++)
            {
                QrCode c;
                if (target != null)
                {
                    c = QrQArt.ShapeText(opt.Payload, opt.ErrorCorrection, opt.MinVersion, m, target, out var st);
                    stats[c] = st;
                }
                else c = Encode(opt, m);
                candidates.Add((c, QrEncoder.MaskPenalty(c)));
            }
            candidates.Sort((a, b) => a.Penalty.CompareTo(b.Penalty));
            // #1401 — a preferred mask (the view search's best match) goes first.
            if (opt.Mask >= 0)
            {
                int at = candidates.FindIndex(c => c.Code.Mask == opt.Mask);
                var preferred = candidates[at];
                candidates.RemoveAt(at);
                candidates.Insert(0, preferred);
            }

            uint[]? best = null;
            QrCode? bestCode = null;
            QrLayout bestLayout = default;
            int bestPassed = -1;
            foreach (var (code, _) in candidates)
            {
                var trial = (uint[])buf.Clone();
                var layout = QrCompositor.Composite(trial, w, h, code, opt);
                var v = QrVerifier.Verify(trial, w, h, opt.Payload, layout.ModulePixels);
                int passed = v.Passes.Count(p => p.Ok);
                if (passed > bestPassed) { best = trial; bestCode = code; bestLayout = layout; bestPassed = passed; }
                if (v.Ok) break;
            }
            Array.Copy(best!, buf, best!.Length);
            return new QrApplyResult(bestCode!, bestLayout, stats.TryGetValue(bestCode!, out var s) ? s : null);
        }

        /// <summary>#1402 — what the image wants per module: its luma at the module
        /// centre (mean of the centre third) against the code area's mean, weighted
        /// by the distance from that mean (strong light / dark areas are claimed
        /// first). Sampled from the unmodified render.</summary>
        public static Func<int, int, (bool Dark, double Weight)> ShapeTarget(uint[] buf, int w, int h, QrCodeOptions opt)
        {
            var size = Encode(opt, 0).Size;
            var L = QrCompositor.Layout(w, h, size, opt.ModulePixels);
            int m = L.ModulePixels, lo = m / 3, hi = Math.Max(lo + 1, m - m / 3);
            var luma = new double[size, size];
            double sum = 0;
            for (int my = 0; my < size; my++)
                for (int mx = 0; mx < size; mx++)
                {
                    long s = 0; int n = 0;
                    for (int dy = lo; dy < hi; dy++)
                        for (int dx = lo; dx < hi; dx++)
                        {
                            s += QrVerifier.LumaOf(buf[(L.OriginY + my * m + dy) * w + L.OriginX + mx * m + dx]);
                            n++;
                        }
                    luma[my, mx] = (double)s / n;
                    sum += luma[my, mx];
                }
            double mean = sum / (size * size);
            bool invert = opt.Invert;
            return (x, y) => ((luma[y, x] < mean) ^ invert, Math.Abs(luma[y, x] - mean));
        }

        /// <summary>Verify the written file (what the user actually gets, watermark
        /// and encoder included). Falls back to <paramref name="fallback"/> when the
        /// format cannot be read back (e.g. EXR).</summary>
        public static QrVerifyResult VerifySaved(string path, QrCodeOptions opt, QrLayout layout,
            uint[] fallback, int fw, int fh)
        {
            uint[]? px = null;
            int w = fw, h = fh;
            try { px = QrVerifier.LoadBgra(path, out w, out h); }
            catch (Exception) { px = null; }
            if (px == null) { px = fallback; w = fw; h = fh; }
            return QrVerifier.Verify(px, w, h, opt.Payload, layout.ModulePixels * (double)w / fw);
        }
    }
}
