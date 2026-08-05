using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using VideoPlayer.Models;

namespace VideoPlayer.Services
{
    public class PlaylistApiService
    {
        private const string BaseUrl = "https://nullcast-api.sygnal.com";
        private static readonly HttpClient _http = new();

        private readonly PlaylistAuthService _auth;

        public PlaylistApiService(PlaylistAuthService auth)
        {
            _auth = auth;
        }

        public async Task<List<Workspace>> GetWorkspacesAsync()
        {
            var json = await GetAsync("/api/v1/workspaces");
            return JsonSerializer.Deserialize<List<Workspace>>(json) ?? new();
        }

        /// <summary>
        /// All bookmarks for a workspace, regardless of source type. The server classifies
        /// each URL (youtube / reddit / facebook / … / web); we no longer filter to youtube
        /// so the in-app playlist shows every source the player can now play.
        /// </summary>
        public async Task<List<Bookmark>> GetBookmarksAsync(int workspaceId)
        {
            var json = await GetAsync($"/api/v1/bookmarks?workspace_id={workspaceId}");
            return JsonSerializer.Deserialize<List<Bookmark>>(json) ?? new();
        }

        public async Task<Bookmark?> GetBookmarkAsync(string muid)
        {
            try
            {
                var json = await GetAsync($"/api/v1/bookmarks/{muid}");
                return JsonSerializer.Deserialize<Bookmark>(json);
            }
            catch
            {
                return null;
            }
        }

        public async Task SavePositionAsync(string muid, int seconds)
        {
            try
            {
                await PutJsonAsync($"/api/v1/bookmarks/{muid}", $"{{\"position\":{seconds}}}");
            }
            catch { /* never interrupt playback */ }
        }

        public async Task DeleteBookmarkAsync(string muid)
        {
            try
            {
                await _auth.EnsureValidTokenAsync();
                var response = await SendAsync(HttpMethod.Delete, $"/api/v1/bookmarks/{muid}", null);
                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    await _auth.EnsureValidTokenAsync();
                    await SendAsync(HttpMethod.Delete, $"/api/v1/bookmarks/{muid}", null);
                }
            }
            catch { }
        }

        public async Task<Bookmark?> CreateBookmarkAsync(string url, int workspaceId, string title = null)
        {
            object payload = string.IsNullOrEmpty(title)
                ? new { url, workspace_id = workspaceId }
                : (object)new { url, workspace_id = workspaceId, title };
            var body = JsonSerializer.Serialize(payload);
            var json = await PostJsonAsync("/api/v1/bookmarks", body);
            return JsonSerializer.Deserialize<Bookmark>(json);
        }

        // ──────────────────────────────────────────────────────
        // HTTP helpers — auto-retry once on 401
        // ──────────────────────────────────────────────────────

        private async Task<string> GetAsync(string path)
        {
            await _auth.EnsureValidTokenAsync();
            var response = await SendAsync(HttpMethod.Get, path, null);

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                await _auth.EnsureValidTokenAsync();
                response = await SendAsync(HttpMethod.Get, path, null);
            }

            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync();
        }

        private async Task PutJsonAsync(string path, string body)
        {
            await _auth.EnsureValidTokenAsync();
            var response = await SendAsync(HttpMethod.Put, path, body);

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                await _auth.EnsureValidTokenAsync();
                await SendAsync(HttpMethod.Put, path, body);
            }
        }

        private async Task<string> PostJsonAsync(string path, string body)
        {
            await _auth.EnsureValidTokenAsync();
            var response = await SendAsync(HttpMethod.Post, path, body);

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                await _auth.EnsureValidTokenAsync();
                response = await SendAsync(HttpMethod.Post, path, body);
            }

            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync();
        }

        private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? jsonBody)
        {
            var req = new HttpRequestMessage(method, BaseUrl + path);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _auth.GetAccessToken());
            if (jsonBody != null)
                req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
            return await _http.SendAsync(req);
        }
    }
}
