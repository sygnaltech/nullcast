using System;
using System.Text.Json.Serialization;

namespace VideoPlayer.Models
{
    // ──────────────────────────────────────────────────────────
    // Plex REST DTOs (subset). Plex returns these when the request
    // carries `Accept: application/json`. Field names match Plex's
    // JSON exactly (camelCase / PascalCase as Plex emits them).
    // ──────────────────────────────────────────────────────────

    public class PlexSearchResponse
    {
        [JsonPropertyName("MediaContainer")]
        public PlexMediaContainer? MediaContainer { get; set; }
    }

    public class PlexMediaContainer
    {
        // /hubs/search groups results into hubs...
        [JsonPropertyName("Hub")]      public PlexHub[]?      Hub      { get; set; }
        // ...while /library/metadata/{id} returns Metadata directly.
        [JsonPropertyName("Metadata")] public PlexMetadata[]? Metadata { get; set; }
    }

    public class PlexHub
    {
        [JsonPropertyName("type")]     public string?         Type     { get; set; }
        [JsonPropertyName("Metadata")] public PlexMetadata[]? Metadata { get; set; }
    }

    public class PlexMetadata
    {
        [JsonPropertyName("title")]            public string?      Title            { get; set; }
        [JsonPropertyName("type")]             public string?      Type             { get; set; }
        [JsonPropertyName("year")]             public int?         Year             { get; set; }
        [JsonPropertyName("duration")]         public long?        Duration         { get; set; } // ms
        [JsonPropertyName("viewOffset")]       public long?        ViewOffset       { get; set; } // ms
        [JsonPropertyName("ratingKey")]        public string?      RatingKey        { get; set; }
        [JsonPropertyName("key")]              public string?      Key              { get; set; }
        [JsonPropertyName("thumb")]            public string?      Thumb            { get; set; }
        [JsonPropertyName("grandparentTitle")] public string?      GrandparentTitle { get; set; } // show
        [JsonPropertyName("parentTitle")]      public string?      ParentTitle      { get; set; } // season/album
        [JsonPropertyName("parentIndex")]      public int?         ParentIndex      { get; set; } // season #
        [JsonPropertyName("index")]            public int?         Index            { get; set; } // episode #
        [JsonPropertyName("Media")]            public PlexMedia[]? Media            { get; set; }
    }

    public class PlexMedia
    {
        [JsonPropertyName("Part")] public PlexPart[]? Part { get; set; }
    }

    public class PlexPart
    {
        [JsonPropertyName("key")]      public string? Key      { get; set; }
        [JsonPropertyName("file")]     public string? File     { get; set; }
        [JsonPropertyName("duration")] public long?   Duration { get; set; }
    }

    // ──────────────────────────────────────────────────────────
    // View model bound to the Plex results list in the sidebar.
    // ──────────────────────────────────────────────────────────

    public class PlexItem
    {
        public string  Title       { get; set; } = "";
        public string  Subtitle    { get; set; } = "";
        public string  TypeLabel   { get; set; } = "";
        public string  RatingKey   { get; set; } = "";
        public string? PartKey     { get; set; }
        public long    DurationMs  { get; set; }
        public long    ViewOffsetMs { get; set; }

        public bool   HasResume => ViewOffsetMs > 0;

        public string ResumeLabel
        {
            get
            {
                if (!HasResume) return "";
                var ts = TimeSpan.FromMilliseconds(ViewOffsetMs);
                return ts.Hours > 0
                    ? $"▶ {ts.Hours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
                    : $"▶ {ts.Minutes}:{ts.Seconds:D2}";
            }
        }

        /// <summary>Line shown under the title: type badge + resume, e.g. "Movie · ▶ 12:03".</summary>
        public string MetaLine =>
            string.IsNullOrEmpty(ResumeLabel) ? TypeLabel : $"{TypeLabel} · {ResumeLabel}";

        /// <summary>Build a PlexItem from a raw metadata record.</summary>
        public static PlexItem FromMetadata(PlexMetadata m)
        {
            var type = (m.Type ?? "").ToLowerInvariant();
            string typeLabel = type switch
            {
                "movie"   => "Movie",
                "episode" => "Episode",
                "show"    => "Show",
                "season"  => "Season",
                "track"   => "Track",
                "clip"    => "Clip",
                _         => string.IsNullOrEmpty(m.Type) ? "Video" : char.ToUpper(m.Type[0]) + m.Type[1..],
            };

            // Subtitle: episodes read "Show · S02E05"; everything else uses the year.
            string subtitle;
            if (type == "episode" && !string.IsNullOrEmpty(m.GrandparentTitle))
            {
                var code = (m.ParentIndex.HasValue && m.Index.HasValue)
                    ? $" · S{m.ParentIndex:D2}E{m.Index:D2}"
                    : "";
                subtitle = $"{m.GrandparentTitle}{code}";
            }
            else
            {
                subtitle = m.Year?.ToString() ?? "";
            }

            return new PlexItem
            {
                Title        = string.IsNullOrEmpty(m.Title) ? "(untitled)" : m.Title!,
                Subtitle     = subtitle,
                TypeLabel    = typeLabel,
                RatingKey    = m.RatingKey ?? "",
                PartKey      = m.Media?.Length > 0 ? m.Media[0].Part?[0]?.Key : null,
                DurationMs   = m.Duration ?? 0,
                ViewOffsetMs = m.ViewOffset ?? 0,
            };
        }
    }
}
