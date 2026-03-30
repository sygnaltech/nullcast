using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using VideoPlayer.Models;

namespace VideoPlayer.Services
{
    public class PlaylistAuthService
    {
        private const string BaseUrl     = "https://playlist-api.sygnal.com";
        private const string ClientId    = "video-player";
        private const string RedirectUri = "http://127.0.0.1:47891/callback";
        private const string Scopes      = "profile:read workspaces:read bookmarks:read bookmarks:write offline_access";

        private static readonly string TokenPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VideoPlayer", "tokens.json");

        private readonly HttpClient _http = new();
        private TokenStore? _tokens;

        public bool   IsSignedIn   => _tokens != null && !string.IsNullOrEmpty(_tokens.AccessToken);
        public string DisplayName  => _tokens?.DisplayName ?? "";

        // ──────────────────────────────────────────────────────
        // Public API
        // ──────────────────────────────────────────────────────

        public async Task<TokenStore?> LoadTokensAsync()
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

        public async Task<TokenStore> LoginAsync()
        {
            var verifier  = GenerateCodeVerifier();
            var challenge = GenerateCodeChallenge(verifier);
            var state     = GenerateState();

            var authorizeUrl =
                $"{BaseUrl}/oauth/authorize" +
                $"?client_id={Uri.EscapeDataString(ClientId)}" +
                $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}" +
                $"&response_type=code" +
                $"&scope={Uri.EscapeDataString(Scopes)}" +
                $"&state={Uri.EscapeDataString(state)}" +
                $"&code_challenge={Uri.EscapeDataString(challenge)}" +
                $"&code_challenge_method=S256";

            using var listener = new HttpListener();
            listener.Prefixes.Add("http://127.0.0.1:47891/");
            listener.Start();

            Process.Start(new ProcessStartInfo(authorizeUrl) { UseShellExecute = true });

            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            string callbackQuery;
            try
            {
                // Loop past any stray browser pre-requests (favicon, etc.)
                HttpListenerContext ctx;
                while (true)
                {
                    ctx = await listener.GetContextAsync().WaitAsync(cts.Token);
                    if (ctx.Request.Url?.AbsolutePath == "/callback") break;
                    ctx.Response.StatusCode = 404;
                    ctx.Response.Close();
                }

                // Capture query string and respond to the browser BEFORE stopping the listener.
                // Stopping closes the underlying socket — writing to the response after Stop()
                // causes ObjectDisposedException on ThreadPoolBoundHandle.
                callbackQuery = ctx.Request.Url?.Query ?? "";

                const string html = "<html><body style='font-family:sans-serif;background:#111;color:#eee;" +
                    "display:flex;align-items:center;justify-content:center;height:100vh;margin:0'>" +
                    "<h2>Signed in successfully. You can close this tab.</h2></body></html>";
                var htmlBytes = Encoding.UTF8.GetBytes(html);
                ctx.Response.ContentType = "text/html";
                ctx.Response.ContentLength64 = htmlBytes.Length;
                await ctx.Response.OutputStream.WriteAsync(htmlBytes);
                ctx.Response.Close();
            }
            catch (OperationCanceledException)
            {
                throw new Exception("Authentication timed out. Please try again.");
            }
            finally
            {
                listener.Stop();
            }

            // Parse query string manually (no System.Web dependency)
            var qs = ParseQueryString(callbackQuery);

            if (qs.TryGetValue("error", out var oauthError))
                throw new Exception($"Authorization denied: {oauthError}");

            if (!qs.TryGetValue("state", out var returnedState) || returnedState != state)
                throw new Exception("State mismatch — possible CSRF. Please try again.");

            if (!qs.TryGetValue("code", out var code) || string.IsNullOrEmpty(code))
                throw new Exception("No authorization code received.");

            // Exchange code for tokens
            var tokenJson = await PostFormAsync("/oauth/token", new[]
            {
                ("grant_type",    "authorization_code"),
                ("code",          code),
                ("redirect_uri",  RedirectUri),
                ("client_id",     ClientId),
                ("code_verifier", verifier),
            });
            var tokenResp = JsonSerializer.Deserialize<OAuthTokenResponse>(tokenJson)!;

            // Fetch user profile
            var profileJson = await GetWithTokenAsync("/api/v1/me", tokenResp.AccessToken);
            var profile     = JsonSerializer.Deserialize<ProfileResponse>(profileJson)!;

            _tokens = new TokenStore
            {
                AccessToken  = tokenResp.AccessToken,
                RefreshToken = tokenResp.RefreshToken,
                ExpiresAt    = DateTime.UtcNow.AddSeconds(tokenResp.ExpiresIn - 60),
                DisplayName  = profile.DisplayName,
                Email        = profile.Email,
            };

            await PersistTokensAsync();
            return _tokens;
        }

        public async Task EnsureValidTokenAsync()
        {
            if (_tokens == null)
            {
                _tokens = await LoginAsync();
                return;
            }

            if (DateTime.UtcNow >= _tokens.ExpiresAt)
            {
                try
                {
                    await RefreshAsync();
                }
                catch
                {
                    // Refresh token expired or revoked — full re-login
                    _tokens = await LoginAsync();
                }
            }
        }

        public async Task SignOutAsync()
        {
            if (_tokens != null)
            {
                try
                {
                    await PostFormAsync("/oauth/revoke", new[]
                    {
                        ("token",     _tokens.AccessToken),
                        ("client_id", ClientId),
                    });
                }
                catch { /* ignore revocation errors */ }
                _tokens = null;
            }

            try { File.Delete(TokenPath); } catch { }
        }

        public string GetAccessToken() => _tokens?.AccessToken ?? "";

        // ──────────────────────────────────────────────────────
        // Private helpers
        // ──────────────────────────────────────────────────────

        private async Task RefreshAsync()
        {
            var json = await PostFormAsync("/oauth/token", new[]
            {
                ("grant_type",    "refresh_token"),
                ("refresh_token", _tokens!.RefreshToken),
                ("client_id",     ClientId),
            });
            var resp = JsonSerializer.Deserialize<OAuthTokenResponse>(json)!;
            _tokens.AccessToken = resp.AccessToken;
            _tokens.ExpiresAt   = DateTime.UtcNow.AddSeconds(resp.ExpiresIn - 60);
            await PersistTokensAsync();
        }

        private async Task<string> PostFormAsync(string path, (string key, string value)[] fields)
        {
            var kvps = new List<KeyValuePair<string, string>>();
            foreach (var (key, value) in fields)
                kvps.Add(new KeyValuePair<string, string>(key, value));

            var response = await _http.PostAsync(BaseUrl + path, new FormUrlEncodedContent(kvps));
            var body     = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                try
                {
                    var err = JsonSerializer.Deserialize<OAuthErrorResponse>(body);
                    var msg = err?.Error ?? response.StatusCode.ToString();
                    if (!string.IsNullOrEmpty(err?.ErrorDescription))
                        msg += $" — {err.ErrorDescription}";
                    throw new Exception($"OAuth error: {msg}");
                }
                catch (JsonException)
                {
                    throw new Exception($"OAuth request failed: {response.StatusCode}");
                }
            }

            return body;
        }

        private async Task<string> GetWithTokenAsync(string path, string accessToken)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, BaseUrl + path);
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
            var response = await _http.SendAsync(req);
            return await response.Content.ReadAsStringAsync();
        }

        private async Task PersistTokensAsync()
        {
            var dir = Path.GetDirectoryName(TokenPath)!;
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(_tokens, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(TokenPath, json);
        }

        private static string GenerateCodeVerifier()
            => Base64UrlEncode(RandomNumberGenerator.GetBytes(32));

        private static string GenerateCodeChallenge(string verifier)
            => Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

        private static string GenerateState()
            => Base64UrlEncode(RandomNumberGenerator.GetBytes(16));

        private static string Base64UrlEncode(byte[] bytes)
            => Convert.ToBase64String(bytes)
                .Replace('+', '-')
                .Replace('/', '_')
                .TrimEnd('=');

        private static Dictionary<string, string> ParseQueryString(string query)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(query)) return result;
            if (query.StartsWith('?')) query = query[1..];
            foreach (var pair in query.Split('&'))
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

        private class OAuthTokenResponse
        {
            [JsonPropertyName("access_token")]  public string AccessToken  { get; set; } = "";
            [JsonPropertyName("refresh_token")] public string RefreshToken { get; set; } = "";
            [JsonPropertyName("expires_in")]    public int    ExpiresIn    { get; set; } = 3600;
        }

        private class ProfileResponse
        {
            [JsonPropertyName("display_name")] public string DisplayName { get; set; } = "";
            [JsonPropertyName("email")]        public string Email       { get; set; } = "";
        }

        private class OAuthErrorResponse
        {
            [JsonPropertyName("error")]             public string Error            { get; set; } = "";
            [JsonPropertyName("error_description")] public string ErrorDescription { get; set; } = "";
        }
    }
}
