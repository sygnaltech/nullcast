using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using VideoPlayer.Models;

namespace VideoPlayer.Services
{
    /// <summary>
    /// YouTube Music data layer (Phase 1), backed by the app's bundled <c>yt-dlp</c> rather than
    /// a hand-rolled InnerTube client. yt-dlp already speaks YouTube's internal API, tracks
    /// Google's changes, and — crucially — reuses the exact browser-cookie auth the player
    /// already wires (<c>--cookies-from-browser</c>, File ▸ Use Edge cookies), so a logged-in
    /// user's private playlists and "Liked Music" resolve without any new auth surface.
    ///
    /// It runs yt-dlp via a runner injected from the main window (so cookie handling and the
    /// yt-dlp path live in one place) and parses its <c>--flat-playlist -J</c> JSON.
    ///
    /// Scope note: this covers search, browsing a playlist's tracks, and Liked Music. Full
    /// library auto-enumeration and playlist *management* (create / add / remove / reorder)
    /// are Phase 2 and require a real InnerTube client.
    /// </summary>
    public class YtMusicService
    {
        /// <summary>Runs yt-dlp: (arguments, url) → stdout. Applies cookies per app settings.</summary>
        private readonly Func<string, string, Task<string>> _runYtDlp;

        public YtMusicService(Func<string, string, Task<string>> runYtDlp)
            => _runYtDlp = runYtDlp;

        /// <summary>The special YouTube Music playlist id for the user's liked songs.</summary>
        public const string LikedMusicId = "LM";

        // ──────────────────────────────────────────────────────
        // Search
        // ──────────────────────────────────────────────────────

        /// <summary>Searches the YouTube Music catalog and returns track rows. Never throws.</summary>
        public async Task<List<YtMusicItem>> SearchTracksAsync(string query)
        {
            var items = new List<YtMusicItem>();
            if (string.IsNullOrWhiteSpace(query)) return items;

            var url = "https://music.youtube.com/search?q=" + Uri.EscapeDataString(query.Trim());
            try
            {
                var json = await _runYtDlp("--flat-playlist -J --no-warnings", url);
                ParseEntries(json, items, YtMusicKind.Track);
            }
            catch (Exception ex)
            {
                App.Log($"[YTMusic] Search failed: {ex.Message}");
            }
            return items;
        }

        // ──────────────────────────────────────────────────────
        // Playlist tracks
        // ──────────────────────────────────────────────────────

        /// <summary>Lists a playlist's tracks in order (cookies required for private/Liked). Never throws.</summary>
        public async Task<List<YtMusicItem>> GetPlaylistTracksAsync(string playlistId)
        {
            var items = new List<YtMusicItem>();
            if (string.IsNullOrWhiteSpace(playlistId)) return items;

            var url = $"https://music.youtube.com/playlist?list={Uri.EscapeDataString(playlistId)}";
            try
            {
                var json = await _runYtDlp("--flat-playlist -J --no-warnings", url);
                ParseEntries(json, items, YtMusicKind.Track);
            }
            catch (Exception ex)
            {
                App.Log($"[YTMusic] Playlist load failed ({playlistId}): {ex.Message}");
            }
            return items;
        }

        // ──────────────────────────────────────────────────────
        // Pin a playlist by URL
        // ──────────────────────────────────────────────────────

        /// <summary>
        /// Resolves a pasted playlist URL to its id + title (title fetched via yt-dlp).
        /// Returns null if the URL has no playlist id or resolution fails.
        /// </summary>
        public async Task<YtMusicPlaylistRef?> ResolvePlaylistAsync(string playlistUrl)
        {
            var id = ExtractPlaylistId(playlistUrl);
            if (string.IsNullOrEmpty(id)) return null;

            var title = id;
            try
            {
                // --playlist-items 0 asks for metadata only (no entry expansion) — fast + cheap.
                var url  = $"https://music.youtube.com/playlist?list={Uri.EscapeDataString(id)}";
                var json = await _runYtDlp("--flat-playlist --playlist-items 0 -J --no-warnings", url);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String)
                    title = t.GetString() ?? id;
            }
            catch (Exception ex)
            {
                App.Log($"[YTMusic] Pin resolve failed ({id}): {ex.Message}");
            }
            return new YtMusicPlaylistRef { Id = id, Title = title };
        }

        /// <summary>Pulls the <c>list=</c> id out of any YouTube / YT Music URL (or a bare id).</summary>
        public static string ExtractPlaylistId(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return "";
            input = input.Trim();

            if (!input.Contains("/") && !input.Contains("?"))
                return input; // already a bare id

            try
            {
                var uri   = new Uri(input, UriKind.Absolute);
                var query = uri.Query.TrimStart('?');
                foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
                {
                    var eq = pair.IndexOf('=');
                    if (eq <= 0) continue;
                    if (pair.Substring(0, eq) == "list")
                        return Uri.UnescapeDataString(pair.Substring(eq + 1));
                }
            }
            catch { /* not a valid absolute URL → no id */ }
            return "";
        }

        // ──────────────────────────────────────────────────────
        // JSON parsing (yt-dlp --flat-playlist -J)
        // ──────────────────────────────────────────────────────

        private static void ParseEntries(string json, List<YtMusicItem> into, YtMusicKind kind)
        {
            if (string.IsNullOrWhiteSpace(json)) return;

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Single video (a /watch URL) — no entries array.
            if (!root.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
            {
                var single = ParseEntry(root);
                if (single != null) into.Add(single);
                return;
            }

            foreach (var e in entries.EnumerateArray())
            {
                var item = ParseEntry(e);
                if (item != null) into.Add(item);
            }
        }

        private static YtMusicItem? ParseEntry(JsonElement e)
        {
            var id = GetStr(e, "id");
            if (string.IsNullOrEmpty(id)) return null;

            // Skip nested playlists/channels that occasionally appear in search results —
            // Phase 1 rows here are tracks. (A YT video id is 11 chars.)
            var type = GetStr(e, "_type");
            if (type == "playlist" || type == "channel") return null;

            return new YtMusicItem
            {
                Kind            = YtMusicKind.Track,
                VideoId         = id,
                Title           = GetStr(e, "title") ?? "(untitled)",
                Subtitle        = GetStr(e, "channel") ?? GetStr(e, "uploader") ?? GetStr(e, "artist") ?? "",
                DurationSeconds = GetDurationSeconds(e),
                ThumbUrl        = GetBestThumb(e),
            };
        }

        private static int GetDurationSeconds(JsonElement e)
        {
            if (e.TryGetProperty("duration", out var d))
            {
                if (d.ValueKind == JsonValueKind.Number && d.TryGetDouble(out var secs))
                    return (int)secs;
            }
            return 0;
        }

        private static string? GetBestThumb(JsonElement e)
        {
            if (e.TryGetProperty("thumbnails", out var thumbs) && thumbs.ValueKind == JsonValueKind.Array)
            {
                string? best = null;
                foreach (var t in thumbs.EnumerateArray())
                    if (t.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String)
                        best = u.GetString(); // last (yt-dlp orders ascending by size) = largest
                if (!string.IsNullOrEmpty(best)) return best;
            }
            return GetStr(e, "thumbnail");
        }

        private static string? GetStr(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }
}
