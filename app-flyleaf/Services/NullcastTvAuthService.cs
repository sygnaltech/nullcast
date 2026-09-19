using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using VideoPlayer.Models;

namespace VideoPlayer.Services
{
    /// <summary>
    /// Signs the player in to Nullcast.TV and hands out a bearer token for
    /// <c>https://nullcast.tv/api/v1</c>.
    ///
    /// <para>
    /// Two doors, and they produce the same thing on the wire:
    /// </para>
    /// <list type="bullet">
    ///   <item><b>OAuth 2.1 + PKCE</b> — the normal path. The client has no pre-issued id, so it
    ///   registers itself once (RFC 7591 dynamic registration) as a <i>public</i> client
    ///   (<c>token_endpoint_auth_method: none</c>) and keeps the id in <c>services.json</c>.
    ///   Access tokens last an hour; the refresh token lasts thirty days and <b>rotates on
    ///   use</b>, so a refreshed pair must be persisted or the next refresh fails.</item>
    ///   <item><b>Personal access token</b> — a string pasted into Settings, for an install that
    ///   cannot run a browser redirect. Never expires, revoked server-side. Takes precedence
    ///   when present, because a person who pasted one meant it.</item>
    /// </list>
    ///
    /// <para>
    /// There is no token-revocation endpoint on this authorization server, so signing out is a
    /// local act: the saved tokens are deleted. A personal access token is revoked on the site.
    /// </para>
    /// </summary>
    public class NullcastTvAuthService
    {
        /// <summary>The origin every catalog and OAuth route hangs off.</summary>
        public const string Origin = "https://nullcast.tv";

        /// <summary>The versioned, CORS-answering, promise-shaped API base. Never the bare /api.</summary>
        public const string ApiBase = Origin + "/api/v1";

        // 47891 is the Playlist service's OAuth listener and 47893 is the remote-control API,
        // so this one takes the gap between them. The URI is registered with the authorization
        // server at first sign-in and must match byte-for-byte on the token exchange.
        private const string RedirectUri = "http://127.0.0.1:47892/callback";
        private const string ClientName  = "Nullcast Player";

        private static readonly string TokenPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VideoPlayer", "nullcast-tv-tokens.json");

        private readonly HttpClient    _http = new() { Timeout = TimeSpan.FromSeconds(20) };
        private readonly ServicesStore _store;

        private TokenStore _tokens;

        /// <summary>Display name for a personal-access-token session, resolved from /me on demand.</summary>
        private string _patDisplayName = "";
        private string _patEmail       = "";

        public NullcastTvAuthService(ServicesStore store) => _store = store;

        // ──────────────────────────────────────────────────────
        // State
        // ──────────────────────────────────────────────────────

        /// <summary>True when a personal access token is configured — it wins over OAuth.</summary>
        public bool UsesPersonalToken => _store?.HasNullcastTvToken == true;

        public bool IsSignedIn =>
            UsesPersonalToken || (_tokens != null && !string.IsNullOrEmpty(_tokens.AccessToken));

        /// <summary>Who is signed in, or "" when we have a credential but no profile yet.</summary>
        public string DisplayName =>
            UsesPersonalToken ? _patDisplayName : (_tokens?.DisplayName ?? "");

        public string Email =>
            UsesPersonalToken ? _patEmail : (_tokens?.Email ?? "");

        // ──────────────────────────────────────────────────────
        // Public API
        // ──────────────────────────────────────────────────────

        /// <summary>
        /// Read the saved OAuth tokens off disk. Returns null when there are none — which is
        /// also the normal state for a personal-token install.
        /// </summary>
        public async Task<TokenStore> LoadTokensAsync()
        {
            try
            {
                if (!File.Exists(TokenPath)) return null;
                var json = await File.ReadAllTextAsync(TokenPath);
                _tokens = JsonSerializer.Deserialize<TokenStore>(json);
                return _tokens;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Run the browser sign-in: register the client if this install has never done so, send
        /// the user to authorize, catch the redirect on loopback, exchange the code, then read
        /// the profile. Throws with a human-readable message on every failure path.
        /// </summary>
        public async Task<TokenStore> LoginAsync()
        {
            var clientId = await EnsureClientRegisteredAsync();

            var verifier  = Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
            var challenge = Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
            var state     = Base64UrlEncode(RandomNumberGenerator.GetBytes(16));

            var authorizeUrl =
                $"{Origin}/oauth/authorize" +
                $"?client_id={Uri.EscapeDataString(clientId)}" +
                $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}" +
                $"&response_type=code" +
                $"&state={Uri.EscapeDataString(state)}" +
                $"&code_challenge={Uri.EscapeDataString(challenge)}" +
                $"&code_challenge_method=S256";

            var query = await AwaitCallbackAsync(authorizeUrl);
            var qs    = ParseQueryString(query);

            if (qs.TryGetValue("error", out var oauthError))
            {
                qs.TryGetValue("error_description", out var desc);
                throw new Exception(string.IsNullOrEmpty(desc)
                    ? $"Authorization denied: {oauthError}"
                    : $"Authorization denied: {oauthError} — {desc}");
            }
            if (!qs.TryGetValue("state", out var returnedState) || returnedState != state)
                throw new Exception("State mismatch — possible CSRF. Please try again.");
            if (!qs.TryGetValue("code", out var code) || string.IsNullOrEmpty(code))
                throw new Exception("No authorization code received.");

            var tokenJson = await PostFormAsync("/oauth/token", new[]
            {
                ("grant_type",    "authorization_code"),
                ("code",          code),
                ("redirect_uri",  RedirectUri),
                ("client_id",     clientId),
                ("code_verifier", verifier),
            });
            var resp = JsonSerializer.Deserialize<OAuthTokenResponse>(tokenJson)
                       ?? throw new Exception("The token endpoint returned nothing usable.");

            _tokens = new TokenStore
            {
                AccessToken  = resp.AccessToken,
                RefreshToken = resp.RefreshToken,
                // A minute of slack so a token isn't spent on the request that discovers it expired.
                ExpiresAt    = DateTime.UtcNow.AddSeconds(Math.Max(60, resp.ExpiresIn) - 60),
            };

            var profile = await FetchProfileAsync(_tokens.AccessToken);
            if (profile != null)
            {
                _tokens.DisplayName = profile.Name ?? "";
                _tokens.Email       = profile.Email ?? "";
            }

            await PersistTokensAsync();
            return _tokens;
        }

        /// <summary>
        /// The bearer token to send, refreshing the OAuth pair first if it has expired.
        /// Returns "" when this install has no credential at all — the caller decides whether
        /// that is an error or simply an anonymous read.
        /// </summary>
        public async Task<string> GetAccessTokenAsync()
        {
            if (UsesPersonalToken) return _store.GetNullcastTvToken();
            if (_tokens == null || string.IsNullOrEmpty(_tokens.AccessToken)) return "";

            if (DateTime.UtcNow >= _tokens.ExpiresAt)
            {
                try
                {
                    await RefreshAsync();
                }
                catch (Exception ex)
                {
                    // The refresh token rotated out from under us, expired, or was revoked. Drop
                    // the pair rather than retrying it forever — the UI reads IsSignedIn and
                    // offers a fresh sign-in.
                    App.Log($"[Nullcast.TV] Token refresh failed, signing out: {ex.Message}");
                    _tokens = null;
                    TryDeleteTokenFile();
                    return "";
                }
            }

            return _tokens.AccessToken;
        }

        /// <summary>
        /// Re-read the signed-in identity from <c>GET /api/v1/me</c>. Used after a restart, when
        /// the saved tokens carry a name but a personal-token install carries none.
        /// </summary>
        public async Task RefreshProfileAsync()
        {
            var token = await GetAccessTokenAsync();
            if (string.IsNullOrEmpty(token)) return;

            var profile = await FetchProfileAsync(token);
            if (profile == null) return;

            if (UsesPersonalToken)
            {
                _patDisplayName = profile.Name  ?? "";
                _patEmail       = profile.Email ?? "";
            }
            else if (_tokens != null)
            {
                _tokens.DisplayName = profile.Name  ?? "";
                _tokens.Email       = profile.Email ?? "";
                await PersistTokensAsync();
            }
        }

        /// <summary>
        /// Forget the local credential. This authorization server publishes no revocation
        /// endpoint, so there is nothing to tell it — the refresh token simply goes unused and
        /// expires. A personal access token is cleared from the store too, since "sign out"
        /// meaning "but keep the other credential" would be a lie.
        /// </summary>
        public Task SignOutAsync()
        {
            _tokens         = null;
            _patDisplayName = "";
            _patEmail       = "";
            TryDeleteTokenFile();
            if (UsesPersonalToken) _store.SetNullcastTvToken("");
            return Task.CompletedTask;
        }

        // ──────────────────────────────────────────────────────
        // Private helpers
        // ──────────────────────────────────────────────────────

        /// <summary>
        /// The client id for this install, registering one if we have never had it. Registration
        /// is a public, unauthenticated call by design: the caller has no credentials yet.
        /// </summary>
        private async Task<string> EnsureClientRegisteredAsync()
        {
            var existing = _store?.NullcastTvClientId ?? "";
            if (!string.IsNullOrEmpty(existing)) return existing;

            var body = JsonSerializer.Serialize(new
            {
                client_name                = ClientName,
                redirect_uris              = new[] { RedirectUri },
                // A desktop app cannot keep a secret, so ask for a public client. The server
                // only mints a client_secret when this says otherwise.
                token_endpoint_auth_method = "none",
                grant_types                = new[] { "authorization_code", "refresh_token" },
                response_types             = new[] { "code" },
            });

            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            var response = await _http.PostAsync(Origin + "/oauth/register", content);
            var text     = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
                throw new Exception($"Could not register with Nullcast.TV ({(int)response.StatusCode}): {Trim(text)}");

            var reg = JsonSerializer.Deserialize<ClientRegistrationResponse>(text);
            if (reg == null || string.IsNullOrEmpty(reg.ClientId))
                throw new Exception("Nullcast.TV registration returned no client id.");

            _store?.SetNullcastTvClientId(reg.ClientId);
            return reg.ClientId;
        }

        /// <summary>
        /// Open the authorize URL in the user's browser and block on the loopback redirect,
        /// returning its raw query string. Two minutes, then it gives up.
        /// </summary>
        private static async Task<string> AwaitCallbackAsync(string authorizeUrl)
        {
            using var listener = new HttpListener();
            listener.Prefixes.Add("http://127.0.0.1:47892/");
            try
            {
                listener.Start();
            }
            catch (HttpListenerException ex)
            {
                throw new Exception($"Could not open the sign-in listener on port 47892: {ex.Message}");
            }

            Process.Start(new ProcessStartInfo(authorizeUrl) { UseShellExecute = true });

            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            try
            {
                // Browsers fire stray pre-requests (favicon and friends) at a new origin; skip
                // anything that isn't the redirect we are waiting for.
                HttpListenerContext ctx;
                while (true)
                {
                    ctx = await listener.GetContextAsync().WaitAsync(cts.Token);
                    if (ctx.Request.Url?.AbsolutePath == "/callback") break;
                    ctx.Response.StatusCode = 404;
                    ctx.Response.Close();
                }

                // Capture the query and answer the browser BEFORE Stop() — stopping closes the
                // underlying socket, and writing afterwards throws ObjectDisposedException.
                var query = ctx.Request.Url?.Query ?? "";

                const string html =
                    "<html><body style='font-family:sans-serif;background:#0F1118;color:#E7E9F1;" +
                    "display:flex;align-items:center;justify-content:center;height:100vh;margin:0'>" +
                    "<h2>Connected to Nullcast.TV. You can close this tab.</h2></body></html>";
                var bytes = Encoding.UTF8.GetBytes(html);
                ctx.Response.ContentType     = "text/html";
                ctx.Response.ContentLength64 = bytes.Length;
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();

                return query;
            }
            catch (OperationCanceledException)
            {
                throw new Exception("Sign-in timed out. Please try again.");
            }
            finally
            {
                listener.Stop();
            }
        }

        /// <summary>
        /// Swap the refresh token for a fresh pair. The server ROTATES: the old refresh token
        /// dies the moment it is used, so the new one has to be persisted here or the next
        /// refresh presents a token the server has already deleted.
        /// </summary>
        private async Task RefreshAsync()
        {
            var clientId = _store?.NullcastTvClientId ?? "";
            if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(_tokens?.RefreshToken))
                throw new Exception("No refresh credential.");

            var json = await PostFormAsync("/oauth/token", new[]
            {
                ("grant_type",    "refresh_token"),
                ("refresh_token", _tokens.RefreshToken),
                ("client_id",     clientId),
            });
            var resp = JsonSerializer.Deserialize<OAuthTokenResponse>(json)
                       ?? throw new Exception("The token endpoint returned nothing usable.");

            _tokens.AccessToken  = resp.AccessToken;
            _tokens.RefreshToken = resp.RefreshToken;
            _tokens.ExpiresAt    = DateTime.UtcNow.AddSeconds(Math.Max(60, resp.ExpiresIn) - 60);
            await PersistTokensAsync();
        }

        private async Task<TvUser> FetchProfileAsync(string accessToken)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, ApiBase + "/me");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                var response = await _http.SendAsync(req);
                if (!response.IsSuccessStatusCode) return null;

                var body = await response.Content.ReadAsStringAsync();
                return JsonSerializer.Deserialize<TvSessionInfo>(body)?.User;
            }
            catch (Exception ex)
            {
                // A missing profile is cosmetic — the credential still works, we just don't
                // know whose it is. Never let it break sign-in.
                App.Log($"[Nullcast.TV] Could not read profile: {ex.Message}");
                return null;
            }
        }

        private async Task<string> PostFormAsync(string path, (string Key, string Value)[] fields)
        {
            var kvps = new List<KeyValuePair<string, string>>(fields.Length);
            foreach (var (key, value) in fields)
                kvps.Add(new KeyValuePair<string, string>(key, value));

            var response = await _http.PostAsync(Origin + path, new FormUrlEncodedContent(kvps));
            var body     = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode) return body;

            try
            {
                var err = JsonSerializer.Deserialize<OAuthErrorResponse>(body);
                var msg = string.IsNullOrEmpty(err?.Error) ? response.StatusCode.ToString() : err.Error;
                if (!string.IsNullOrEmpty(err?.ErrorDescription)) msg += $" — {err.ErrorDescription}";
                throw new Exception($"OAuth error: {msg}");
            }
            catch (JsonException)
            {
                throw new Exception($"OAuth request failed: {(int)response.StatusCode} {response.StatusCode}");
            }
        }

        private async Task PersistTokensAsync()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(TokenPath)!);
                var json = JsonSerializer.Serialize(_tokens,
                    new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(TokenPath, json);
            }
            catch (Exception ex)
            {
                App.Log($"[Nullcast.TV] Could not save tokens: {ex.Message}");
            }
        }

        private static void TryDeleteTokenFile()
        {
            try { if (File.Exists(TokenPath)) File.Delete(TokenPath); } catch { }
        }

        private static string Trim(string s) =>
            string.IsNullOrEmpty(s) ? "" : (s.Length <= 200 ? s : s[..200] + "…");

        private static string Base64UrlEncode(byte[] bytes) =>
            Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');

        private static Dictionary<string, string> ParseQueryString(string query)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(query)) return result;
            if (query.StartsWith('?')) query = query[1..];
            foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var idx = pair.IndexOf('=');
                if (idx >= 0)
                    result[Uri.UnescapeDataString(pair[..idx])] = Uri.UnescapeDataString(pair[(idx + 1)..]);
            }
            return result;
        }

        // ──────────────────────────────────────────────────────
        // Private DTO types
        // ──────────────────────────────────────────────────────

        private class ClientRegistrationResponse
        {
            [JsonPropertyName("client_id")] public string ClientId { get; set; } = "";
        }

        private class OAuthTokenResponse
        {
            [JsonPropertyName("access_token")]  public string AccessToken  { get; set; } = "";
            [JsonPropertyName("refresh_token")] public string RefreshToken { get; set; } = "";
            [JsonPropertyName("expires_in")]    public int    ExpiresIn    { get; set; } = 3600;
        }

        private class OAuthErrorResponse
        {
            [JsonPropertyName("error")]             public string Error            { get; set; } = "";
            [JsonPropertyName("error_description")] public string ErrorDescription { get; set; } = "";
        }
    }
}
