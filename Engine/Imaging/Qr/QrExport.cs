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
    public sealed record QrExportReport(QrCode Code, QrLayout Layout, QrVerifyResult? Verify);

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
            if (opt.DotFraction < QrCodeOptions.MinDotFraction || opt.DotFraction > QrCodeOptions.MaxDotFraction)
                return $"QR dot fraction must be {QrCodeOptions.MinDotFraction} .. {QrCodeOptions.MaxDotFraction}";
            try { QrEncoder.Encode(opt.Payload, opt.ErrorCorrection); }
            catch (ArgumentException ex) { return ex.Message; }
            return null;
        }

        /// <summary>Null when <paramref name="opt"/> encodes and fits a
        /// <paramref name="w"/> x <paramref name="h"/> image, else why not - so a
        /// caller can refuse before a long render.</summary>
        public static string? Preflight(QrCodeOptions opt, int w, int h)
        {
            if (Validate(opt) is { } err) return err;
            var code = QrEncoder.Encode(opt.Payload, opt.ErrorCorrection);
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
        public static (QrCode Code, QrLayout Layout) Apply(uint[] buf, int w, int h, QrCodeOptions opt)
        {
            string? err = Validate(opt);
            if (err != null) throw new ArgumentException(err, nameof(opt));

            var candidates = new List<(QrCode Code, int Penalty)>(8);
            for (int m = 0; m < 8; m++)
            {
                var c = QrEncoder.Encode(opt.Payload, opt.ErrorCorrection, mask: m);
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
            return (bestCode!, bestLayout);
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
