using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace VideoPlayer.Services
{
    /// <summary>
    /// Client for the local <c>browser-helper</c> broker — a tray service that hands out the user's
    /// LIVE browser cookies (via a browser extension) for user-approved sites. This solves the
    /// cookie-freshness problem that kills exported cookies.txt: the broker's cookies are always
    /// current (the extension pushes on every change), including the HttpOnly session cookies.
    ///
    /// Contract (browser-helper docs/protocol.md): discover the port from
    /// <c>%APPDATA%\browser-helper\endpoint.json</c>, then
    /// <c>GET http://127.0.0.1:&lt;port&gt;/v1/cookies?domain=…&amp;format=netscape</c> with a Bearer
    /// token + <c>X-Browser-Helper-App</c> header. Loopback only. Everything here is best-effort:
    /// if the broker is down or a domain isn't shared, we return null and callers fall back to the
    /// manual cookies.txt path.
    /// </summary>
    public class BrowserHelperClient
    {
        // M1 dev credentials (browser-helper hardcodes these until its M3 per-app token registry).
        private const string AppId    = "nullcast";
        private const string DevToken = "dev-token-browser-helper";

        private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };

        private static string EndpointFile => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "browser-helper", "endpoint.json");

        /// <summary>True when the broker's endpoint file exists (it may still be offline).</summary>
        public bool IsInstalled => File.Exists(EndpointFile);

        /// <summary>
        /// Fetches a Netscape cookies.txt for the given domain(s) and writes it to a temp file,
        /// returning that path — ready to hand to yt-dlp/InnerTube. Returns null if the broker is
        /// unreachable or has no cookies for the domain(s).
        /// </summary>
        public async Task<string?> FetchCookiesFileAsync(string domains)
        {
            var port = ReadPort();
            if (port is null) return null;

            try
            {
                var url = $"http://127.0.0.1:{port}/v1/cookies?domain={Uri.EscapeDataString(domains)}&format=netscape";
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {DevToken}");
                req.Headers.TryAddWithoutValidation("X-Browser-Helper-App", AppId);

                using var resp = await _http.SendAsync(req).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) return null; // 404 = domain not shared, etc.

                var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(body)) return null;

                var tmp = Path.Combine(Path.GetTempPath(), "nullcast-bh-cookies.txt");
                await File.WriteAllTextAsync(tmp, body).ConfigureAwait(false);
                return tmp;
            }
            catch (Exception ex)
            {
                App.Log($"[browser-helper] fetch failed: {ex.Message}");
                return null;
            }
        }

        private int? ReadPort()
        {
            try
            {
                if (!File.Exists(EndpointFile)) return null;
                using var doc = JsonDocument.Parse(File.ReadAllText(EndpointFile));
                if (doc.RootElement.TryGetProperty("httpPort", out var p) && p.TryGetInt32(out var port))
                    return port;
            }
            catch { /* missing/malformed → treat as not installed */ }
            return null;
        }
    }
}
