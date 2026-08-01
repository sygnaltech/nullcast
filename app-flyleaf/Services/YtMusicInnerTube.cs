using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using VideoPlayer.Models;

namespace VideoPlayer.Services
{
    /// <summary>
    /// YouTube Music via the site's own **InnerTube** web API (the same path ytmusicapi uses).
    /// This is what yt-dlp cannot do: it reaches the user's *library* — Liked Music, their
    /// playlists, and each playlist's tracks — because it calls the real YT Music endpoints with
    /// an authenticated <c>SAPISIDHASH</c> derived from the user's cookies.
    ///
    /// Auth reuses the exported <c>cookies.txt</c> the app already knows about
    /// (<see cref="AppSettings.CookieFilePath"/>): we read the Google/YouTube cookies, build the
    /// Cookie header, and sign each request. yt-dlp still does playback (resolve the watch URL);
    /// this class only does browse/metadata.
    /// </summary>
    public class YtMusicInnerTube
    {
        private const string ApiKey    = "AIzaSyC9XL3ZjWddXya6X74dJoCTL-WEYFDNX30"; // public WEB_REMIX key
        private const string ClientVer = "1.20241009.01.00";
        private const string Origin    = "https://music.youtube.com";

        private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };

        /// <summary>Returns the current cookies.txt path (from settings), or null/empty if none.</summary>
        private readonly Func<string?> _cookieFilePath;

        public YtMusicInnerTube(Func<string?> cookieFilePath) => _cookieFilePath = cookieFilePath;

        /// <summary>True when a cookies.txt with a usable SAPISID is available (i.e. we can auth).</summary>
        public bool IsConfigured
        {
            get
            {
                var (cookieHeader, sapisid) = ReadCookies();
                return !string.IsNullOrEmpty(cookieHeader) && !string.IsNullOrEmpty(sapisid);
            }
        }

        // ──────────────────────────────────────────────────────
        // Public browse operations
        // ──────────────────────────────────────────────────────

        /// <summary>The user's library playlists (the "your playlists" list). Never throws.</summary>
        public async Task<List<YtMusicItem>> GetLibraryPlaylistsAsync()
        {
            using var json = await BrowseAsync("FEmusic_liked_playlists");
            return json is null ? new() : ParsePlaylists(json);
        }

        /// <summary>The user's Liked Music songs (browseId VLLM). Never throws.</summary>
        public Task<List<YtMusicItem>> GetLikedSongsAsync() => GetPlaylistTracksAsync("LM");

        /// <summary>Tracks of a playlist by its id (with or without the "VL" prefix). Never throws.</summary>
        public async Task<List<YtMusicItem>> GetPlaylistTracksAsync(string playlistId)
        {
            if (string.IsNullOrWhiteSpace(playlistId)) return new();
            var browseId = playlistId.StartsWith("VL", StringComparison.Ordinal) ? playlistId : "VL" + playlistId;
            using var json = await BrowseAsync(browseId);
            return json is null ? new() : ParseTracks(json);
        }

        // ──────────────────────────────────────────────────────
        // HTTP + auth
        // ──────────────────────────────────────────────────────

        private async Task<JsonDocument?> BrowseAsync(string browseId)
        {
            var body = new Dictionary<string, object> { ["browseId"] = browseId };
            return await PostAsync("browse", body);
        }

        private async Task<JsonDocument?> PostAsync(string endpoint, Dictionary<string, object> body)
        {
            var (cookieHeader, sapisid) = ReadCookies();
            if (string.IsNullOrEmpty(cookieHeader) || string.IsNullOrEmpty(sapisid))
            {
                App.Log("[YTMusic/InnerTube] No usable cookies (need an exported cookies.txt while signed in).");
                return null;
            }

            body["context"] = new Dictionary<string, object>
            {
                ["client"] = new Dictionary<string, object>
                {
                    ["clientName"] = "WEB_REMIX", ["clientVersion"] = ClientVer, ["hl"] = "en", ["gl"] = "US",
                },
            };

            var url = $"{Origin}/youtubei/v1/{endpoint}?key={ApiKey}&prettyPrint=false";
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
                };
                req.Headers.TryAddWithoutValidation("Authorization", BuildSapisidHash(sapisid));
                req.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
                req.Headers.TryAddWithoutValidation("X-Goog-AuthUser", "0");
                req.Headers.TryAddWithoutValidation("Origin", Origin);
                req.Headers.TryAddWithoutValidation("User-Agent",
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36");

                using var resp = await _http.SendAsync(req).ConfigureAwait(false);
                var text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    App.Log($"[YTMusic/InnerTube] {endpoint} {browseIdOf(body)} → HTTP {(int)resp.StatusCode}");
                    return null;
                }
                return JsonDocument.Parse(text);
            }
            catch (Exception ex)
            {
                App.Log($"[YTMusic/InnerTube] {endpoint} failed: {ex.Message}");
                return null;
            }
        }

        private static string browseIdOf(Dictionary<string, object> body) =>
            body.TryGetValue("browseId", out var b) ? b?.ToString() ?? "" : "";

        private static string BuildSapisidHash(string sapisid)
        {
            var ts  = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var raw = $"{ts} {sapisid} {Origin}";
            using var sha1 = SHA1.Create();
            var hex = Convert.ToHexString(sha1.ComputeHash(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
            return $"SAPISIDHASH {ts}_{hex}";
        }

        /// <summary>
        /// Reads the exported cookies.txt (Netscape format) and returns the Cookie header for
        /// Google/YouTube domains plus the SAPISID value used to sign requests. Values are used
        /// only to build request headers; nothing is logged.
        /// </summary>
        private (string cookieHeader, string sapisid) ReadCookies()
        {
            var path = _cookieFilePath();
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return ("", "");

            try
            {
                var jar = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var line in File.ReadLines(path))
                {
                    if (line.Length == 0 || line[0] == '#') continue;
                    var p = line.Split('\t');
                    if (p.Length < 7) continue;
                    var domain = p[0].TrimStart('.');
                    if (!domain.EndsWith("youtube.com", StringComparison.OrdinalIgnoreCase) &&
                        !domain.EndsWith("google.com",  StringComparison.OrdinalIgnoreCase)) continue;
                    jar[p[5]] = p[6]; // name → value (last one wins)
                }

                if (jar.Count == 0) return ("", "");
                var sapisid = jar.GetValueOrDefault("SAPISID")
                           ?? jar.GetValueOrDefault("__Secure-3PAPISID") ?? "";
                var header = string.Join("; ", jar.Select(kv => $"{kv.Key}={kv.Value}"));
                return (header, sapisid);
            }
            catch (Exception ex)
            {
                App.Log($"[YTMusic/InnerTube] cookie read failed: {ex.Message}");
                return ("", "");
            }
        }

        // ──────────────────────────────────────────────────────
        // Response parsing (recursive — resilient to InnerTube's deep nesting)
        // ──────────────────────────────────────────────────────

        /// <summary>Tracks = every musicResponsiveListItemRenderer that carries a videoId.</summary>
        private static List<YtMusicItem> ParseTracks(JsonDocument doc)
        {
            var items = new List<YtMusicItem>();
            foreach (var mrlir in FindAll(doc.RootElement, "musicResponsiveListItemRenderer"))
            {
                var videoId = TrackVideoId(mrlir);
                if (string.IsNullOrEmpty(videoId)) continue;

                var cols   = FlexColumnTexts(mrlir);
                var title  = cols.Count > 0 ? cols[0] : "(untitled)";
                var artist = cols.Count > 1 ? cols[1] : "";
                items.Add(new YtMusicItem
                {
                    Kind = YtMusicKind.Track, VideoId = videoId, Title = title, Subtitle = artist,
                });
            }
            return items;
        }

        /// <summary>
        /// Playlists = musicTwoRowItemRenderer (grid) OR any renderer whose browseEndpoint points at
        /// a "VL…" (playlist) id. Covers both the grid and list library layouts.
        /// </summary>
        private static List<YtMusicItem> ParsePlaylists(JsonDocument doc)
        {
            var items = new List<YtMusicItem>();
            var seen  = new HashSet<string>();

            void Add(string browseId, string title, string subtitle)
            {
                if (string.IsNullOrEmpty(browseId) || !browseId.StartsWith("VL", StringComparison.Ordinal)) return;
                var id = browseId.Substring(2);
                if (string.IsNullOrEmpty(id) || !seen.Add(id)) return;
                items.Add(new YtMusicItem
                {
                    Kind = YtMusicKind.Playlist, PlaylistId = id,
                    Title = string.IsNullOrEmpty(title) ? id : title,
                    Subtitle = string.IsNullOrEmpty(subtitle) ? "Playlist" : subtitle,
                });
            }

            foreach (var r in FindAll(doc.RootElement, "musicTwoRowItemRenderer"))
                Add(BrowseId(r), RunsText(Prop(r, "title")), RunsText(Prop(r, "subtitle")));

            foreach (var r in FindAll(doc.RootElement, "musicResponsiveListItemRenderer"))
            {
                var bid = BrowseId(r);
                if (!string.IsNullOrEmpty(bid))
                {
                    var cols = FlexColumnTexts(r);
                    Add(bid, cols.Count > 0 ? cols[0] : "", cols.Count > 1 ? cols[1] : "");
                }
            }
            return items;
        }

        // ── field extractors ──────────────────────────────────

        private static string TrackVideoId(JsonElement mrlir)
        {
            // playlistItemData.videoId is the reliable one; fall back to any watchEndpoint.videoId.
            if (Prop(mrlir, "playlistItemData") is { ValueKind: JsonValueKind.Object } pid
                && pid.TryGetProperty("videoId", out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString() ?? "";

            foreach (var we in FindAll(mrlir, "watchEndpoint"))
                if (we.TryGetProperty("videoId", out var vid) && vid.ValueKind == JsonValueKind.String)
                    return vid.GetString() ?? "";
            return "";
        }

        private static List<string> FlexColumnTexts(JsonElement mrlir)
        {
            var texts = new List<string>();
            if (Prop(mrlir, "flexColumns") is { ValueKind: JsonValueKind.Array } cols)
                foreach (var c in cols.EnumerateArray())
                {
                    var t = RunsText(Prop(c, "text")); // the flex column renderer wraps a "text" runs object
                    texts.Add(t);
                }
            return texts;
        }

        /// <summary>First browseEndpoint.browseId found anywhere under a node.</summary>
        private static string BrowseId(JsonElement node)
        {
            foreach (var be in FindAll(node, "browseEndpoint"))
                if (be.TryGetProperty("browseId", out var b) && b.ValueKind == JsonValueKind.String)
                    return b.GetString() ?? "";
            return "";
        }

        /// <summary>Concatenate a {"runs":[{"text":...}]} object's run texts. Handles nesting.</summary>
        private static string RunsText(JsonElement node)
        {
            if (node.ValueKind != JsonValueKind.Object) return "";
            if (node.TryGetProperty("runs", out var runs) && runs.ValueKind == JsonValueKind.Array)
            {
                var sb = new StringBuilder();
                foreach (var r in runs.EnumerateArray())
                    if (r.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                        sb.Append(t.GetString());
                return sb.ToString();
            }
            // Some fields are a bare {"text": "..."}.
            var direct = FindAll(node, "text").FirstOrDefault();
            return direct.ValueKind == JsonValueKind.String ? direct.GetString() ?? "" : "";
        }

        private static JsonElement Prop(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v : default;

        /// <summary>Yield every value stored under a property named <paramref name="name"/>, recursively.</summary>
        private static IEnumerable<JsonElement> FindAll(JsonElement node, string name)
        {
            switch (node.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var prop in node.EnumerateObject())
                    {
                        if (prop.Name == name) yield return prop.Value;
                        foreach (var hit in FindAll(prop.Value, name)) yield return hit;
                    }
                    break;
                case JsonValueKind.Array:
                    foreach (var el in node.EnumerateArray())
                        foreach (var hit in FindAll(el, name)) yield return hit;
                    break;
            }
        }
    }
}
