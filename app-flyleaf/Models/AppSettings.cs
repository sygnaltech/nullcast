using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace VideoPlayer.Models
{
    public class AppSettings
    {
        [JsonPropertyName("playlist_collapsed")]
        public bool PlaylistCollapsed { get; set; }

        [JsonPropertyName("completed_muids")]
        public HashSet<string> CompletedMuids { get; set; } = new();

        [JsonPropertyName("known_durations")]
        public Dictionary<string, int> KnownDurations { get; set; } = new();

        /// <summary>
        /// When true, yt-dlp is invoked with <c>--cookies-from-browser</c> so private /
        /// logged-in content (e.g. a friends-only Facebook video) can be resolved using the
        /// user's existing browser session. Off by default — public content needs no cookies
        /// and reading the cookie store is slower and can contend with a running browser.
        /// </summary>
        [JsonPropertyName("use_browser_cookies")]
        public bool UseBrowserCookies { get; set; }

        /// <summary>Which browser yt-dlp reads cookies from when <see cref="UseBrowserCookies"/> is on.</summary>
        [JsonPropertyName("cookie_browser")]
        public string CookieBrowser { get; set; } = "edge";
    }
}
