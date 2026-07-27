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
            c.DefaultRequestHeaders.Add("X-Plex-Product", "Nullcast");
            c.DefaultRequestHeaders.Add("X-Plex-Client-Identifier", "sygnal-video-player");
            return c;
        }

        private readonly ServicesStore _store;

        public PlexService(ServicesStore store) => _store = store;

        public bool IsConfigured => _store.IsPlexConfigured;

        // ──────────────────────────────────────────────────────
        // Item materialisation (thumb URL needs server + token, which
        // the static PlexItem.FromMetadata can't reach — do it here).
        // ──────────────────────────────────────────────────────

        /// <summary>FromMetadata + resolve the absolute poster URL for the tile/list views.</summary>
        private PlexItem Build(PlexMetadata m)
        {
            var item = PlexItem.FromMetadata(m);
            item.ThumbUrl = ResolveThumbUrl(item.ThumbPath);
            return item;
        }

        /// <summary>
        /// Token-signed poster URL for a thumb path, resized server-side via Plex's photo
        /// transcoder so we pull small thumbnails rather than full-resolution art. Null when
        /// unconfigured or the item has no thumb.
        /// </summary>
        public string? ResolveThumbUrl(string? thumbPath, int width = 240, int height = 360)
        {
            if (!IsConfigured || string.IsNullOrEmpty(thumbPath)) return null;
            var token = _store.GetPlexToken();
            return $"{_store.PlexBaseUrl}/photo/:/transcode" +
                   $"?width={width}&height={height}&minSize=1&upscale=1" +
                   $"&url={Uri.EscapeDataString(thumbPath)}" +
                   $"&X-Plex-Token={Uri.EscapeDataString(token)}";
        }

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
                          $"&limit=30&includeGenres=1&X-Plex-Token={Uri.EscapeDataString(token)}";
                var parsed = await GetJsonAsync(url).ConfigureAwait(false);

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

                    var item = Build(m);
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

        /// <summary>
        /// Fetch a single item by its ratingKey (used to replay a Plex entry from local
        /// History, which only stores <c>plex://&lt;ratingKey&gt;</c>). Returns null if the
        /// server is unreachable, the item is gone, or it has no playable Part.
        /// </summary>
        public async Task<PlexItem?> GetItemAsync(string ratingKey)
        {
            if (!IsConfigured || string.IsNullOrEmpty(ratingKey)) return null;

            var baseUrl = _store.PlexBaseUrl;
            var token   = _store.GetPlexToken();
            try
            {
                var url = $"{baseUrl}/library/metadata/{Uri.EscapeDataString(ratingKey)}" +
                          $"?includeGenres=1&X-Plex-Token={Uri.EscapeDataString(token)}";
                var parsed = await GetJsonAsync(url).ConfigureAwait(false);
                var meta   = parsed?.MediaContainer?.Metadata?.FirstOrDefault();
                if (meta == null) return null;

                var item = Build(meta);
                return string.IsNullOrEmpty(item.PartKey) ? null : item;
            }
            catch
            {
                return null;
            }
        }

        private async Task<string?> FetchPartKeyAsync(string baseUrl, string token, string ratingKey)
        {
            try
            {
                var url = $"{baseUrl}/library/metadata/{Uri.EscapeDataString(ratingKey)}" +
                          $"?X-Plex-Token={Uri.EscapeDataString(token)}";
                var parsed = await GetJsonAsync(url).ConfigureAwait(false);
                var meta   = parsed?.MediaContainer?.Metadata?.FirstOrDefault();
                return meta?.Media?.FirstOrDefault()?.Part?.FirstOrDefault()?.Key;
            }
            catch
            {
                return null;
            }
        }

        // ──────────────────────────────────────────────────────
        // Browse (libraries → categories/genres → items → TV drill-down)
        // ──────────────────────────────────────────────────────

        /// <summary>
        /// Lists the browsable video libraries (Plex sections of type movie/show). Music and
        /// photo sections are dropped. Never throws — returns an empty list on error.
        /// </summary>
        public async Task<List<PlexSection>> GetVideoSectionsAsync()
        {
            var sections = new List<PlexSection>();
            if (!IsConfigured) return sections;

            try
            {
                var url  = $"{_store.PlexBaseUrl}/library/sections" +
                           $"?X-Plex-Token={Uri.EscapeDataString(_store.GetPlexToken())}";
                var parsed = await GetJsonAsync(url);
                foreach (var d in parsed?.MediaContainer?.Directory ?? Array.Empty<PlexDirectory>())
                {
                    var type = (d.Type ?? "").ToLowerInvariant();
                    if (type is not ("movie" or "show")) continue;
                    if (string.IsNullOrEmpty(d.Key)) continue;
                    sections.Add(new PlexSection { Key = d.Key!, Type = type, Title = d.Title ?? type });
                }
            }
            catch { /* empty list → UI shows search only */ }

            return sections;
        }

        /// <summary>Lists the genres declared by a library section. Never throws.</summary>
        public async Task<List<PlexGenre>> GetGenresAsync(string sectionKey)
        {
            var genres = new List<PlexGenre>();
            if (!IsConfigured || string.IsNullOrEmpty(sectionKey)) return genres;

            try
            {
                var url = $"{_store.PlexBaseUrl}/library/sections/{Uri.EscapeDataString(sectionKey)}/genre" +
                          $"?X-Plex-Token={Uri.EscapeDataString(_store.GetPlexToken())}";
                var parsed = await GetJsonAsync(url);
                foreach (var d in parsed?.MediaContainer?.Directory ?? Array.Empty<PlexDirectory>())
                {
                    if (string.IsNullOrEmpty(d.Key) || string.IsNullOrEmpty(d.Title)) continue;
                    genres.Add(new PlexGenre { Id = d.Key!, Title = d.Title! });
                }
            }
            catch { /* no genres → dropdown shows Views only */ }

            return genres;
        }

        /// <summary>
        /// Browses a library section for a category/view. For a movie section, movies are returned
        /// directly. For a show section, <c>All</c>/<c>Genre</c> return shows (navigational — drill
        /// with <see cref="GetChildrenAsync"/>), while the smart views return a flat list of episodes.
        /// Never throws.
        /// </summary>
        public async Task<List<PlexItem>> BrowseAsync(
            string sectionKey, string sectionType, PlexBrowseView view, string? genreId, int limit = 300)
        {
            var results = new List<PlexItem>();
            if (!IsConfigured || string.IsNullOrEmpty(sectionKey)) return results;

            bool isShow = sectionType == "show";

            // Type: 1=movie, 2=show, 4=episode. Smart views on a show list flat episodes.
            int    typeNum = view switch
            {
                PlexBrowseView.All or PlexBrowseView.Genre => isShow ? 2 : 1,
                _                                          => isShow ? 4 : 1,
            };
            string sort = view switch
            {
                PlexBrowseView.RecentlyAdded   => "addedAt:desc",
                PlexBrowseView.RecentlyWatched => "lastViewedAt:desc",
                PlexBrowseView.NeverWatched    => "addedAt:desc",
                _                              => "titleSort:asc",
            };

            var extra = "";
            if (view == PlexBrowseView.Genre && !string.IsNullOrEmpty(genreId))
                extra += $"&genre={Uri.EscapeDataString(genreId)}";
            if (view == PlexBrowseView.NeverWatched)
                extra += "&unwatched=1";

            try
            {
                var url = $"{_store.PlexBaseUrl}/library/sections/{Uri.EscapeDataString(sectionKey)}/all" +
                          $"?type={typeNum}&sort={sort}{extra}&includeGenres=1" +
                          $"&X-Plex-Token={Uri.EscapeDataString(_store.GetPlexToken())}";
                // ConfigureAwait(false): build the (up to 300) items on a thread-pool thread, not
                // the UI thread. The UI caller resumes on the dispatcher via its own await.
                var parsed = await GetJsonAsync(url, limit).ConfigureAwait(false);

                foreach (var m in parsed?.MediaContainer?.Metadata ?? Array.Empty<PlexMetadata>())
                {
                    // "Recently Watched" filters client-side: only items actually watched.
                    if (view == PlexBrowseView.RecentlyWatched && (m.LastViewedAt ?? 0) <= 0) continue;
                    results.Add(Build(m));
                }
            }
            catch { /* empty → UI shows "nothing here" */ }

            return results;
        }

        /// <summary>
        /// Fetches the children of a container: a show → its seasons, a season → its episodes
        /// (in order). Used for the TV drill-down. Never throws.
        /// </summary>
        public async Task<List<PlexItem>> GetChildrenAsync(string ratingKey)
        {
            var results = new List<PlexItem>();
            if (!IsConfigured || string.IsNullOrEmpty(ratingKey)) return results;

            try
            {
                var url = $"{_store.PlexBaseUrl}/library/metadata/{Uri.EscapeDataString(ratingKey)}/children" +
                          $"?X-Plex-Token={Uri.EscapeDataString(_store.GetPlexToken())}";
                var parsed = await GetJsonAsync(url, 300).ConfigureAwait(false);
                foreach (var m in parsed?.MediaContainer?.Metadata ?? Array.Empty<PlexMetadata>())
                    results.Add(Build(m));
            }
            catch { /* empty */ }

            return results;
        }

        /// <summary>
        /// GET + JSON-deserialize, optionally paging with the Plex container-size headers so large
        /// libraries return in one shot rather than the server's small default page.
        ///
        /// The response is streamed and deserialized asynchronously with
        /// <see cref="ConfigureAwait"/>(false), so parsing a large library (hundreds of items,
        /// now with genres) runs on a thread-pool thread and never blocks the UI. Callers touch
        /// the UI only after awaiting, back on the dispatcher.
        /// </summary>
        private static async Task<PlexSearchResponse?> GetJsonAsync(string url, int? containerSize = null)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (containerSize is int cs)
            {
                req.Headers.Add("X-Plex-Container-Start", "0");
                req.Headers.Add("X-Plex-Container-Size", cs.ToString());
            }
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead)
                                        .ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            await using var stream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
            return await JsonSerializer.DeserializeAsync<PlexSearchResponse>(stream).ConfigureAwait(false);
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
