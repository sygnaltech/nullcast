using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using VideoPlayer.Models;

namespace VideoPlayer.Services
{
    /// <summary>
    /// Talks to a Plex Media Server over its HTTP REST API. Config (server URL + token)
    /// comes from <see cref="ServicesStore"/>; the token is decrypted on demand and sent
    /// as the <c>X-Plex-Token</c> query parameter. Responses are requested as JSON.
    ///
    /// Playback is Direct Play: <see cref="ResolveStreamUrl"/> yields the raw Part URL,
    /// which the Flyleaf/FFmpeg backend opens directly (no server transcode).
    /// </summary>
    public class PlexService
    {
        private static readonly HttpClient _http = CreateClient();

        // Plex identifies clients via these headers; harmless but proper etiquette.
        private static HttpClient CreateClient()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            c.DefaultRequestHeaders.Add("Accept", "application/json");
            c.DefaultRequestHeaders.Add("X-Plex-Product", "Video Player");
            c.DefaultRequestHeaders.Add("X-Plex-Client-Identifier", "sygnal-video-player");
            return c;
        }

        private readonly ServicesStore _store;

        public PlexService(ServicesStore store) => _store = store;

        public bool IsConfigured => _store.IsPlexConfigured;

        // ──────────────────────────────────────────────────────
        // Connection test (used by the settings dialog)
        // ──────────────────────────────────────────────────────

        /// <summary>
        /// Verifies a server URL + token by hitting <c>/identity</c>. Returns a friendly
        /// (ok, message) pair — never throws.
        /// </summary>
        public static async Task<(bool Ok, string Message)> TestConnectionAsync(string baseUrl, string token)
        {
            baseUrl = (baseUrl ?? "").Trim().TrimEnd('/');
            token   = (token ?? "").Trim();

            if (string.IsNullOrEmpty(baseUrl))
                return (false, "Enter a server address, e.g. http://192.168.1.50:32400");
            if (string.IsNullOrEmpty(token))
                return (false, "Enter a Plex token.");
            if (!baseUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                return (false, "Server address must start with http:// or https://");

            try
            {
                var url  = $"{baseUrl}/identity?X-Plex-Token={Uri.EscapeDataString(token)}";
                var resp = await _http.GetAsync(url);

                if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                    return (false, "Server reached, but the token was rejected (401).");
                if (!resp.IsSuccessStatusCode)
                    return (false, $"Server responded {(int)resp.StatusCode} {resp.StatusCode}.");

                var body = await resp.Content.ReadAsStringAsync();
                try
                {
                    using var doc = JsonDocument.Parse(body);
                    var mc = doc.RootElement.GetProperty("MediaContainer");
                    string ver = mc.TryGetProperty("version", out var v) ? v.GetString() ?? "" : "";
                    return (true, string.IsNullOrEmpty(ver)
                        ? "Connected to Plex server."
                        : $"Connected to Plex server (v{ver}).");
                }
                catch
                {
                    return (true, "Connected to Plex server.");
                }
            }
            catch (Exception ex)
            {
                return (false, $"Could not reach server: {ex.Message}");
            }
        }

        // ──────────────────────────────────────────────────────
        // Search
        // ──────────────────────────────────────────────────────

        /// <summary>
        /// Searches all Plex libraries for playable items matching <paramref name="query"/>.
        /// Returns only items we can Direct Play (a resolvable Part); shows/seasons are
        /// dropped. Never throws — returns an empty list on error.
        /// </summary>
        public async Task<List<PlexItem>> SearchAsync(string query)
        {
            var results = new List<PlexItem>();
            if (!IsConfigured || string.IsNullOrWhiteSpace(query)) return results;

            var baseUrl = _store.PlexBaseUrl;
            var token   = _store.GetPlexToken();

            try
            {
                var url = $"{baseUrl}/hubs/search?query={Uri.EscapeDataString(query.Trim())}" +
                          $"&limit=30&X-Plex-Token={Uri.EscapeDataString(token)}";
                var body = await _http.GetStringAsync(url);
                var parsed = JsonSerializer.Deserialize<PlexSearchResponse>(body);

                var metas = new List<PlexMetadata>();
                foreach (var hub in parsed?.MediaContainer?.Hub ?? Array.Empty<PlexHub>())
                {
                    if (hub.Metadata == null) continue;
                    metas.AddRange(hub.Metadata);
                }

                var seen = new HashSet<string>();
                foreach (var m in metas)
                {
                    var type = (m.Type ?? "").ToLowerInvariant();
                    // Only leaf, directly-playable types.
                    if (type is not ("movie" or "episode" or "clip" or "track")) continue;

                    var item = PlexItem.FromMetadata(m);
                    if (!string.IsNullOrEmpty(item.RatingKey) && !seen.Add(item.RatingKey)) continue;

                    // Some hub results omit Media/Part — fetch full metadata to get the Part key.
                    if (string.IsNullOrEmpty(item.PartKey) && !string.IsNullOrEmpty(item.RatingKey))
                        item.PartKey = await FetchPartKeyAsync(baseUrl, token, item.RatingKey);

                    if (!string.IsNullOrEmpty(item.PartKey))
                        results.Add(item);
                }
            }
            catch
            {
                // Swallow — the UI shows "no results".
            }

            return results;
        }

        private async Task<string?> FetchPartKeyAsync(string baseUrl, string token, string ratingKey)
        {
            try
            {
                var url = $"{baseUrl}/library/metadata/{Uri.EscapeDataString(ratingKey)}" +
                          $"?X-Plex-Token={Uri.EscapeDataString(token)}";
                var body   = await _http.GetStringAsync(url);
                var parsed = JsonSerializer.Deserialize<PlexSearchResponse>(body);
                var meta   = parsed?.MediaContainer?.Metadata?.FirstOrDefault();
                return meta?.Media?.FirstOrDefault()?.Part?.FirstOrDefault()?.Key;
            }
            catch
            {
                return null;
            }
        }

        // ──────────────────────────────────────────────────────
        // Playback URL + progress reporting
        // ──────────────────────────────────────────────────────

        /// <summary>Direct-Play stream URL for an item, or null if it has no Part.</summary>
        public string? ResolveStreamUrl(PlexItem item)
        {
            if (!IsConfigured || string.IsNullOrEmpty(item?.PartKey)) return null;
            var token = _store.GetPlexToken();
            return $"{_store.PlexBaseUrl}{item.PartKey}?X-Plex-Token={Uri.EscapeDataString(token)}";
        }

        /// <summary>
        /// Best-effort playback-progress report so Plex tracks watched/resume state.
        /// <paramref name="state"/> is "playing", "paused", or "stopped". Never throws.
        /// </summary>
        public async Task ReportTimelineAsync(PlexItem item, string state, long timeMs)
        {
            if (!IsConfigured || item == null || string.IsNullOrEmpty(item.RatingKey)) return;
            try
            {
                var token = _store.GetPlexToken();
                var key   = Uri.EscapeDataString($"/library/metadata/{item.RatingKey}");
                var url =
                    $"{_store.PlexBaseUrl}/:/timeline" +
                    $"?ratingKey={Uri.EscapeDataString(item.RatingKey)}" +
                    $"&key={key}" +
                    $"&state={Uri.EscapeDataString(state)}" +
                    $"&time={timeMs}" +
                    $"&duration={item.DurationMs}" +
                    $"&X-Plex-Token={Uri.EscapeDataString(token)}";
                await _http.GetAsync(url);
            }
            catch { /* progress reporting is never critical */ }
        }
    }
}
