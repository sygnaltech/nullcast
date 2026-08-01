using System;

namespace VideoPlayer.Models
{
    // ──────────────────────────────────────────────────────────
    // View model bound to the "YT Music" sidebar list. One template
    // renders both playlist rows (drill in) and track rows (play),
    // mirroring how the Podcast tab reuses one row for shows + episodes.
    //
    // Phase 1 sources its data from yt-dlp's JSON extraction of
    // music.youtube.com, so a row is really "a thing yt-dlp can open":
    // a playlist URL to expand, or a video id to play.
    // ──────────────────────────────────────────────────────────

    /// <summary>What a <see cref="YtMusicItem"/> represents.</summary>
    public enum YtMusicKind { Track, Playlist }

    public class YtMusicItem
    {
        public YtMusicKind Kind { get; set; } = YtMusicKind.Track;

        /// <summary>Track / playlist title.</summary>
        public string Title { get; set; } = "";

        /// <summary>Artist(s) for a track, or item-count / owner for a playlist.</summary>
        public string Subtitle { get; set; } = "";

        /// <summary>YouTube video id (tracks). Play URL is built from this.</summary>
        public string VideoId { get; set; } = "";

        /// <summary>YouTube Music playlist id, e.g. "LM" or "PL…"/"VL…" (playlists).</summary>
        public string PlaylistId { get; set; } = "";

        /// <summary>Track length in seconds (0 when unknown).</summary>
        public int DurationSeconds { get; set; }

        /// <summary>Best available thumbnail URL, or null.</summary>
        public string? ThumbUrl { get; set; }

        public bool IsPlaylist => Kind == YtMusicKind.Playlist;
        public bool IsTrack    => Kind == YtMusicKind.Track;

        /// <summary>The music.youtube.com URL yt-dlp opens for this row.</summary>
        public string Url => IsPlaylist
            ? $"https://music.youtube.com/playlist?list={Uri.EscapeDataString(PlaylistId)}"
            : $"https://music.youtube.com/watch?v={Uri.EscapeDataString(VideoId)}";

        // The list template binds these so one row fits both kinds (like the Podcast tab).
        public string DisplayTitle => Title;

        public string DisplaySubtitle
        {
            get
            {
                if (IsPlaylist) return string.IsNullOrEmpty(Subtitle) ? "Playlist" : Subtitle;
                var dur = DurationLabel;
                if (string.IsNullOrEmpty(Subtitle)) return dur;
                return string.IsNullOrEmpty(dur) ? Subtitle : $"{Subtitle}  ·  {dur}";
            }
        }

        private string DurationLabel
        {
            get
            {
                if (DurationSeconds <= 0) return "";
                var ts = TimeSpan.FromSeconds(DurationSeconds);
                return ts.Hours > 0
                    ? $"{ts.Hours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
                    : $"{ts.Minutes}:{ts.Seconds:D2}";
            }
        }
    }
}
