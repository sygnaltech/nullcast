using System;

namespace VideoPlayer.Models
{
    // ──────────────────────────────────────────────────────────
    // Ctrl+K global search. A single source-agnostic row that the
    // command palette renders and dispatches on. Each concrete source
    // (Plex, bookmarks, and — later — podcasts/history) maps its own
    // item into one of these, stashing the original object in Payload
    // so the play handler can down-cast and hand it to the existing
    // per-source playback entry point.
    // ──────────────────────────────────────────────────────────

    public enum SearchSourceKind
    {
        Playlist,
        Plex,
        Podcast,
        History
    }

    public class SearchResult
    {
        public SearchSourceKind Kind { get; set; }

        /// <summary>Human label for the source, used as the group header in the "All" view.</summary>
        public string SourceBadge { get; set; }

        public string Title { get; set; }
        public string Subtitle { get; set; }

        /// <summary>Absolute, ready-to-bind image URL (Plex posters are token-signed); null when none.</summary>
        public string ThumbUrl { get; set; }
        public bool HasThumb => !string.IsNullOrEmpty(ThumbUrl);

        /// <summary>The original domain object (Bookmark, PlexItem, …) the palette dispatches on.</summary>
        public object Payload { get; set; }

        public static string BadgeFor(SearchSourceKind kind) => kind switch
        {
            SearchSourceKind.Playlist => "Playlists",
            SearchSourceKind.Plex     => "Plex",
            SearchSourceKind.Podcast  => "Podcasts",
            SearchSourceKind.History  => "History",
            _                         => "Other"
        };
    }
}
