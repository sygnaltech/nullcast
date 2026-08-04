using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace VideoPlayer.Services
{
    /// <summary>
    /// Client for the local <c>Tether</c> broker — a tray service that hands out the user's
    /// LIVE browser cookies (via a browser extension) for user-approved sites. This solves the
    /// cookie-freshness problem that kills exported cookies.txt: the broker's cookies are always
    /// current (the extension pushes on every change), including the HttpOnly session cookies.
    ///
    /// Contract (Tether INTEGRATION.md): discover the port from
    /// <c>%APPDATA%\tether\endpoint.json</c>, then
    /// <c>GET http://127.0.0.1:&lt;port&gt;/v1/cookies?domain=…&amp;format=netscape</c> with a Bearer
    /// token + <c>X-Tether-App</c> header. Loopback only. Everything here is best-effort:
    /// if the broker is down or a domain isn't shared, we return null and callers fall back to the
    /// manual cookies.txt path.
    /// </summary>
    public class TetherClient
    {
        // M1 dev credentials — the grace-period fallback used when no per-app credential is
        // configured. Once the user enters a provisioned app id + token (Services settings),
        // those take precedence (Tether M3 per-app auth).
        private const string FallbackAppId = "nullcast";
        private const string FallbackToken = "dev-token-tether";

        private readonly ServicesStore _store;

        public TetherClient(ServicesStore store) => _store = store;

        /// <summary>
        /// Credential attempts in priority order: the provisioned (appId, token) if configured,
        /// then the M1 dev token as a fallback. Any request that fails auth falls through to the next.
        /// </summary>
        private List<(string appId, string token)> CredentialAttempts()
        {
            var list = new List<(string, string)>();
            if (_store != null && _store.HasTetherCreds)
                list.Add((_store.TetherAppId, _store.GetTetherToken()));

            // Dev-token fallback — only when enabled (default on). Uses the configured dev token
            // or the built-in default.
            if (_store == null)
                list.Add((FallbackAppId, FallbackToken));
            else if (_store.TetherDevTokenFallback)
                list.Add((FallbackAppId, _store.TetherDevToken));

            return list;
        }

        private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };

        private static string EndpointFile => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "tether", "endpoint.json");

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

            var url = $"http://127.0.0.1:{port}/v1/cookies?domain={Uri.EscapeDataString(domains)}&format=netscape";

            foreach (var (appId, token) in CredentialAttempts())
            {
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, url);
                    req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
                    req.Headers.TryAddWithoutValidation("X-Tether-App", appId);

                    using var resp = await _http.SendAsync(req).ConfigureAwait(false);
                    // Auth failures fall through to the next credential (provisioned → dev token).
                    if (resp.StatusCode is System.Net.HttpStatusCode.Unauthorized
                                        or System.Net.HttpStatusCode.Forbidden)
                        continue;
                    if (!resp.IsSuccessStatusCode) return null; // 404 not shared etc. — nothing to fall back to

                    var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(body)) return null;

                    // Per-domain temp file so concurrent fetches for different sites don't clobber.
                    var safe = new string((domains ?? "").Where(char.IsLetterOrDigit).ToArray());
                    if (safe.Length == 0) safe = "cookies";
                    var tmp = Path.Combine(Path.GetTempPath(), $"nullcast-tether-{safe}.txt");
                    await File.WriteAllTextAsync(tmp, body).ConfigureAwait(false);
                    return tmp;
                }
                catch (Exception ex)
                {
                    App.Log($"[tether] fetch failed: {ex.Message}");
                    return null;
                }
            }
            return null;
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
