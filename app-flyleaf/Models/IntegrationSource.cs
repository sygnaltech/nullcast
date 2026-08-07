using System;
using System.Collections.Generic;
using System.Windows.Media;

namespace VideoPlayer.Models
{
    /// <summary>
    /// A content integration / "app" (Plex, Playlist, YT Music, …). Each carries one brand
    /// colour and one glyph, used to draw a small "app tile" badge — a coloured disc with the
    /// glyph on top — so items can be told apart at a glance (in the sidebar tabs and in
    /// History). The disc is the brand colour at low alpha and the glyph is the same colour at
    /// full strength, so a source reads as one consistent colour everywhere it appears.
    ///
    /// The glyphs are deliberately simple, standalone vector paths (24×24 canvas) meant to be
    /// swapped for hand-authored SVGs later — the circle is drawn separately by
    /// <c>SourceBadge</c>, never baked into the path here.
    /// </summary>
    public sealed class IntegrationSource
    {
        public string   Key         { get; }
        public string   DisplayName { get; }
        public Color    Color       { get; }
        public Geometry Icon        { get; }

        /// <summary>Glyph fill — the brand colour at full strength.</summary>
        public Brush IconBrush   { get; }
        /// <summary>Disc fill — the same brand colour, low alpha (the "tile").</summary>
        public Brush CircleBrush { get; }
        /// <summary>Hairline ring so the disc reads against the dark background.</summary>
        public Brush RingBrush   { get; }

        private IntegrationSource(string key, string displayName, string hex, string pathData)
        {
            Key         = key;
            DisplayName = displayName;
            Color       = (Color)ColorConverter.ConvertFromString(hex);
            Icon        = Geometry.Parse(pathData);
            Icon.Freeze();

            IconBrush   = Frozen(Color);
            CircleBrush = Frozen(Color, 0x2E); // ~18% — matches the AccentTint pattern
            RingBrush   = Frozen(Color, 0x59); // ~35% — subtle same-colour ring
        }

        private static SolidColorBrush Frozen(Color c, byte? alpha = null)
        {
            var col = alpha is byte a ? Color.FromArgb(a, c.R, c.G, c.B) : c;
            var b = new SolidColorBrush(col);
            b.Freeze();
            return b;
        }

        // ── Registry ──────────────────────────────────────────────────────────
        // Colours are the recognisable brand hue for each integration; History uses the
        // theme's muted grey since it is an aggregator, not a source.

        public static readonly IntegrationSource Playlist = new(
            "Playlist", "Playlist", "#7D97FF",
            "M4,6 L15,6 L15,8.2 L4,8.2 Z " +
            "M4,10.9 L15,10.9 L15,13.1 L4,13.1 Z " +
            "M4,15.8 L11,15.8 L11,18 L4,18 Z " +
            "M14,14 L20.5,17.75 L14,21.5 Z");

        public static readonly IntegrationSource Plex = new(
            "Plex", "Plex", "#E5A00D",
            "M9,4 L13.5,12 L9,20 L12,20 L16.5,12 L12,4 Z");

        public static readonly IntegrationSource YtMusic = new(
            "YtMusic", "YT Music", "#FF0000",
            "M6.5,17 A3,2.4 0 1 1 12.5,17 A3,2.4 0 1 1 6.5,17 Z " +
            "M12.2,6.4 L14,5.8 L14,17 L12.2,17 Z " +
            "M14,5.8 L18.5,4 L18.5,7.2 L14,9 Z");

        public static readonly IntegrationSource Podcasts = new(
            "Podcasts", "Podcasts", "#A855F7",
            "M12,3.5 A3,3 0 0 1 15,6.5 L15,11.5 A3,3 0 0 1 9,11.5 L9,6.5 A3,3 0 0 1 12,3.5 Z " +
            "M7,12 L8.7,12 A3.3,3.3 0 0 0 15.3,12 L17,12 A5,5 0 0 1 7,12 Z " +
            "M11,16 L13,16 L13,19.5 L11,19.5 Z " +
            "M8.5,19.5 L15.5,19.5 L15.5,21 L8.5,21 Z");

        public static readonly IntegrationSource History = new(
            "History", "History", "#848B9F",
            "F0 M12,3 A9,9 0 1 1 12,21 A9,9 0 1 1 12,3 Z " +
            "M12,5.4 A6.6,6.6 0 1 0 12,18.6 A6.6,6.6 0 1 0 12,5.4 Z " +
            "M11,7.5 L13,7.5 L13,12 L11,12 Z " +
            "M12,10.9 L16.6,13.4 L15.6,15.2 L11,12.7 Z");

        private static readonly Dictionary<string, IntegrationSource> ByKey =
            new(StringComparer.OrdinalIgnoreCase)
            {
                [Playlist.Key] = Playlist,
                [Plex.Key]     = Plex,
                [YtMusic.Key]  = YtMusic,
                [Podcasts.Key] = Podcasts,
                [History.Key]  = History,
            };

        /// <summary>Look up a source by its key. Unknown keys fall back to Playlist.</summary>
        public static IntegrationSource Get(string key) =>
            key != null && ByKey.TryGetValue(key, out var s) ? s : Playlist;

        /// <summary>
        /// Classify a recorded playback URL into its source. History stores raw URLs (Plex as
        /// <c>plex://…</c>), so classification is URL-shape based — good enough to tell the big
        /// integrations apart, and centralised here so it can grow (e.g. an explicit stored
        /// source for podcasts) without touching the UI.
        /// </summary>
        public static IntegrationSource Resolve(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return Playlist;

            if (url.StartsWith("plex://", StringComparison.OrdinalIgnoreCase))
                return Plex;

            var host = TryGetHost(url);
            if (host != null &&
                (host.EndsWith("youtube.com", StringComparison.Ordinal) ||
                 host == "youtu.be" || host.EndsWith(".youtu.be", StringComparison.Ordinal)))
                return YtMusic;

            return Playlist;
        }

        private static string TryGetHost(string url) =>
            Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host.ToLowerInvariant() : null;
    }
}
