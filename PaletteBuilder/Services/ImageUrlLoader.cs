// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 Bradley Brown

// Services/ImageUrlLoader.cs
//
// Secure HTTPS image fetcher for the "From URL…" load path. The
// confirm-before-load UX (UrlFetchDialog) sits on top of this; the loader
// itself is purely network + validation logic and is safe to call from
// background tasks.
//
// Defense in depth (in order of execution):
//   1. Scheme allow-list — only https. file/data/http/etc rejected.
//   2. DNS resolution → reject if any returned address is loopback,
//      RFC1918 private, IPv4 link-local (169.254/16 — kills the AWS/Azure
//      metadata endpoint), multicast, broadcast, IPv6 ULA (fc00::/7),
//      IPv6 link-local (fe80::/10), or IPv6 multicast (ff00::/8).
//   3. DNS-rebind defence: SocketsHttpHandler.ConnectCallback pins each
//      hop to the first validated address so the OS resolver cannot be
//      re-queried between validate and connect.
//   4. Manual redirect loop (AllowAutoRedirect off) — each Location is
//      re-run through the full pipeline before the next request fires.
//   5. Content-Length cap (25 MB) checked from headers before body read;
//      streaming counter aborts if a server omits Content-Length and the
//      payload exceeds the cap mid-stream.
//   6. Content-Type must start "image/".
//   7. Final SKCodec.Create sanity decode confirms the bytes are an
//      actual image, not, e.g., an HTML error page that slipped past the
//      content-type check.

using System;
using System.Buffers;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SkiaSharp;

namespace PaletteBuilder.Services
{
    public readonly record struct UrlFetchResult(
        byte[]? Bytes,
        string? Filename,
        string? Host,
        string? ContentType,
        long Size,
        string? Error);

    public sealed class ImageUrlLoader
    {
        public const long MaxBytes = 25L * 1024 * 1024;
        public const int MaxRedirects = 3;
        public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

        public async Task<UrlFetchResult> TryFetchAsync(string url, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(url))
                return Fail("URL is empty.");

            if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
                return Fail("Could not parse URL.");

            return await FetchWithRedirectsAsync(uri, redirectsRemaining: MaxRedirects, ct).ConfigureAwait(false);
        }

        private static async Task<UrlFetchResult> FetchWithRedirectsAsync(Uri uri, int redirectsRemaining, CancellationToken ct)
        {
            // (1) Scheme must be https. http/file/data/javascript/ftp all rejected.
            if (!string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
                return Fail($"Scheme '{uri.Scheme}' is not allowed; HTTPS only.");

            // (2) Resolve host and reject if ANY address is private/loopback/etc.
            IPAddress[] addresses;
            try
            {
                addresses = await Dns.GetHostAddressesAsync(uri.Host, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return Fail($"DNS lookup failed for {uri.Host}: {ex.Message}");
            }
            if (addresses.Length == 0)
                return Fail($"DNS returned no addresses for {uri.Host}.");
            foreach (var ip in addresses)
            {
                if (IsBlockedAddress(ip))
                    return Fail($"Host {uri.Host} resolves to a blocked address ({ip}).");
            }
            // (3) Pin to the first resolved address for this hop.
            var pinnedIp = addresses[0];

            using var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = System.Net.DecompressionMethods.All,
                ConnectTimeout = TimeSpan.FromSeconds(15),
                ConnectCallback = async (context, callCt) =>
                {
                    // Bind to the validated IP rather than re-resolving via DNS.
                    // Defeats DNS rebinding between validate and connect.
                    var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                    try
                    {
                        await socket.ConnectAsync(new IPEndPoint(pinnedIp, context.DnsEndPoint.Port), callCt).ConfigureAwait(false);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch
                    {
                        socket.Dispose();
                        throw;
                    }
                },
            };

            using var client = new HttpClient(handler, disposeHandler: false)
            {
                Timeout = RequestTimeout,
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("PaletteBuilder/1.0");
            client.DefaultRequestHeaders.Accept.ParseAdd("image/*");

            using var request = new HttpRequestMessage(HttpMethod.Get, uri);

            HttpResponseMessage response;
            try
            {
                response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return Fail($"HTTP request failed: {ex.Message}");
            }

            try
            {
                // Manual redirect: 3xx with a Location header.
                if ((int)response.StatusCode >= 300 && (int)response.StatusCode < 400 && response.Headers.Location != null)
                {
                    if (redirectsRemaining <= 0)
                        return Fail($"Redirect limit ({MaxRedirects}) exceeded.");
                    var next = response.Headers.Location.IsAbsoluteUri
                        ? response.Headers.Location
                        : new Uri(uri, response.Headers.Location);
                    response.Dispose();
                    return await FetchWithRedirectsAsync(next, redirectsRemaining - 1, ct).ConfigureAwait(false);
                }

                if (!response.IsSuccessStatusCode)
                    return Fail($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");

                // (6) Content-Type allow-list.
                var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
                if (!contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                    return Fail($"Content-Type '{contentType}' is not an image.");

                // (5) Up-front Content-Length cap.
                long? declaredLength = response.Content.Headers.ContentLength;
                if (declaredLength.HasValue && declaredLength.Value > MaxBytes)
                    return Fail($"Image is {declaredLength.Value:N0} bytes; cap is {MaxBytes:N0}.");

                using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                var bytes = await ReadCappedAsync(body, MaxBytes, ct).ConfigureAwait(false);
                if (bytes == null)
                    return Fail($"Image exceeds {MaxBytes:N0}-byte cap.");

                // (7) Sanity-decode.
                using var ms = new MemoryStream(bytes, writable: false);
                using var codec = SKCodec.Create(ms);
                if (codec == null)
                    return Fail("Server returned bytes that are not a recognised image.");

                var filename = BuildFilename(uri, contentType);
                return new UrlFetchResult(bytes, filename, uri.Host, contentType, bytes.Length, null);
            }
            finally
            {
                response.Dispose();
            }
        }

        private static async Task<byte[]?> ReadCappedAsync(Stream body, long cap, CancellationToken ct)
        {
            using var ms = new MemoryStream();
            var buffer = ArrayPool<byte>.Shared.Rent(81920);
            try
            {
                long total = 0;
                int read;
                while ((read = await body.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > cap) return null;
                    ms.Write(buffer, 0, read);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
            return ms.ToArray();
        }

        // ── Allow-block IP guard ────────────────────────────────────────────

        public static bool IsBlockedAddress(IPAddress ip)
        {
            if (IPAddress.IsLoopback(ip)) return true;
            if (ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.Broadcast)) return true;
            if (ip.Equals(IPAddress.IPv6Any)) return true;
            if (ip.IsIPv6LinkLocal) return true;
            if (ip.IsIPv6SiteLocal) return true; // historical, still treat as private
            if (ip.IsIPv6Multicast) return true;

            if (ip.AddressFamily == AddressFamily.InterNetwork)
            {
                var b = ip.GetAddressBytes();
                // 10.0.0.0/8
                if (b[0] == 10) return true;
                // 172.16.0.0/12
                if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true;
                // 192.168.0.0/16
                if (b[0] == 192 && b[1] == 168) return true;
                // 169.254.0.0/16 (IPv4 link-local incl. cloud metadata endpoint)
                if (b[0] == 169 && b[1] == 254) return true;
                // 127.0.0.0/8 (loopback — also covered by IsLoopback)
                if (b[0] == 127) return true;
                // 0.0.0.0/8
                if (b[0] == 0) return true;
                // 100.64.0.0/10 (CGNAT)
                if (b[0] == 100 && (b[1] & 0xC0) == 64) return true;
                // 224.0.0.0/4 (multicast)
                if (b[0] >= 224 && b[0] <= 239) return true;
                // 240.0.0.0/4 (reserved)
                if (b[0] >= 240) return true;
            }
            else if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            {
                // fc00::/7 (ULA — IPv6 private)
                var b = ip.GetAddressBytes();
                if ((b[0] & 0xFE) == 0xFC) return true;
                // IPv4-mapped IPv6 (::ffff:0:0/96): unwrap and re-check
                if (ip.IsIPv4MappedToIPv6)
                {
                    return IsBlockedAddress(ip.MapToIPv4());
                }
            }
            return false;
        }

        private static string BuildFilename(Uri uri, string contentType)
        {
            string ext = ExtFromContentType(contentType) ?? ExtFromPath(uri) ?? ".img";
            var safeHost = new StringBuilder();
            foreach (var ch in uri.Host)
                safeHost.Append(char.IsLetterOrDigit(ch) || ch == '-' || ch == '.' ? ch : '_');
            return $"PaletteBuilder_{safeHost}_{Guid.NewGuid():N}{ext}";
        }

        private static string? ExtFromContentType(string ct) => ct.ToLowerInvariant() switch
        {
            "image/png" => ".png",
            "image/jpeg" or "image/jpg" => ".jpg",
            "image/webp" => ".webp",
            "image/gif" => ".gif",
            "image/bmp" => ".bmp",
            "image/tiff" => ".tiff",
            "image/heic" => ".heic",
            "image/heif" => ".heif",
            _ => null,
        };

        private static string? ExtFromPath(Uri uri)
        {
            var path = uri.AbsolutePath;
            var dot = path.LastIndexOf('.');
            if (dot < 0 || dot == path.Length - 1) return null;
            var ext = path.Substring(dot).ToLowerInvariant();
            return ext.Length is > 1 and < 8 && ext.All(c => char.IsLetterOrDigit(c) || c == '.') ? ext : null;
        }

        private static UrlFetchResult Fail(string msg)
            => new UrlFetchResult(null, null, null, null, 0, msg);
    }
}
