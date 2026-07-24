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
        // ...while /library/metadata/{id} + /all return Metadata directly.
        [JsonPropertyName("Metadata")] public PlexMetadata[]? Metadata { get; set; }
        // ...and /library/sections + /library/sections/{id}/genre return Directory.
        [JsonPropertyName("Directory")] public PlexDirectory[]? Directory { get; set; }
    }

    /// <summary>
    /// A Plex <c>Directory</c> row. Serves double duty: <c>/library/sections</c> returns one
    /// per library (key = section id, type = movie/show/artist), and
    /// <c>/library/sections/{id}/genre</c> returns one per genre (key = genre id, title = name).
    /// </summary>
    public class PlexDirectory
    {
        [JsonPropertyName("key")]   public string? Key   { get; set; }
        [JsonPropertyName("type")]  public string? Type  { get; set; }
        [JsonPropertyName("title")] public string? Title { get; set; }
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
        // Browse/sort metadata (present on /all + /children responses).
        [JsonPropertyName("addedAt")]          public long?        AddedAt          { get; set; } // epoch s
        [JsonPropertyName("lastViewedAt")]     public long?        LastViewedAt     { get; set; } // epoch s
        [JsonPropertyName("viewCount")]        public int?         ViewCount        { get; set; }
        [JsonPropertyName("childCount")]       public int?         ChildCount       { get; set; } // seasons on a show
        [JsonPropertyName("leafCount")]        public int?         LeafCount        { get; set; } // episodes total
        [JsonPropertyName("viewedLeafCount")]  public int?         ViewedLeafCount  { get; set; } // episodes watched
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
    // Browse view-models (library selector + category dropdown).
    // ──────────────────────────────────────────────────────────

    /// <summary>The virtual "smart" views plus the genre view offered in the category dropdown.</summary>
    public enum PlexBrowseView { All, RecentlyAdded, RecentlyWatched, NeverWatched, Genre }

    /// <summary>A browsable video library (Plex section of type movie or show).</summary>
    public class PlexSection
    {
        public string Key   { get; set; } = "";   // section id, e.g. "1"
        public string Type  { get; set; } = "";   // "movie" or "show"
        public string Title { get; set; } = "";   // e.g. "Movies", "TV Shows"
        public bool   IsShow => Type == "show";
    }

    /// <summary>A genre reported by a library, used to filter its browse list.</summary>
    public class PlexGenre
    {
        public string Id    { get; set; } = "";
        public string Title { get; set; } = "";
    }

    /// <summary>
    /// One entry in the category dropdown. <see cref="Group"/> ("Views" / "Genres") drives the
    /// grouped popup; <see cref="View"/> (+ <see cref="GenreId"/> when a genre) drives the query.
    /// </summary>
    public class PlexCategory
    {
        public PlexCategory() { }
        public PlexCategory(string label, PlexBrowseView view, string group)
        {
            Label = label; View = view; Group = group;
        }

        public string         Label   { get; set; } = "";
        public PlexBrowseView View    { get; set; }
        public string         Group   { get; set; } = "";
        public string?        GenreId { get; set; }
    }

    // ──────────────────────────────────────────────────────────
    // View model bound to the Plex results list in the sidebar.
    // ──────────────────────────────────────────────────────────

    public class PlexItem
    {
        public string  Title       { get; set; } = "";
        public string  Subtitle    { get; set; } = "";
        public string  TypeLabel   { get; set; } = "";
        public string  Kind        { get; set; } = "";   // raw Plex type: movie/show/season/episode…
        public string  RatingKey   { get; set; } = "";
        public string? PartKey     { get; set; }
        public long    DurationMs  { get; set; }
        public long    ViewOffsetMs { get; set; }

        // Browse metadata (0 when unknown).
        public long    AddedAt         { get; set; }
        public long    LastViewedAt    { get; set; }
        public int     ChildCount      { get; set; }
        public int     LeafCount       { get; set; }
        public int     ViewedLeafCount { get; set; }

        /// <summary>Shows and seasons are navigational (drill in); everything else is a leaf you play.</summary>
        public bool IsContainer => Kind is "show" or "season";
        public bool IsPlayable  => !IsContainer;

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

        /// <summary>Watched/count summary for a container, e.g. "3/62 watched" or "5 seasons".</summary>
        public string ProgressLabel
        {
            get
            {
                if (Kind == "show")
                {
                    if (LeafCount > 0)  return $"{ViewedLeafCount}/{LeafCount} watched";
                    if (ChildCount > 0) return $"{ChildCount} season{(ChildCount == 1 ? "" : "s")}";
                    return "Show";
                }
                if (Kind == "season")
                    return LeafCount > 0 ? $"{LeafCount} episode{(LeafCount == 1 ? "" : "s")}" : "Season";
                return TypeLabel;
            }
        }

        /// <summary>
        /// Line shown under the title. Containers show their progress/count; leaves show the
        /// type badge plus resume, e.g. "Movie · ▶ 12:03".
        /// </summary>
        public string MetaLine => IsContainer
            ? ProgressLabel
            : (string.IsNullOrEmpty(ResumeLabel) ? TypeLabel : $"{TypeLabel} · {ResumeLabel}");

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
                Title           = string.IsNullOrEmpty(m.Title) ? "(untitled)" : m.Title!,
                Subtitle        = subtitle,
                TypeLabel       = typeLabel,
                Kind            = type,
                RatingKey       = m.RatingKey ?? "",
                PartKey         = m.Media?.Length > 0 ? m.Media[0].Part?[0]?.Key : null,
                DurationMs      = m.Duration ?? 0,
                ViewOffsetMs    = m.ViewOffset ?? 0,
                AddedAt         = m.AddedAt ?? 0,
                LastViewedAt    = m.LastViewedAt ?? 0,
                ChildCount      = m.ChildCount ?? 0,
                LeafCount       = m.LeafCount ?? 0,
                ViewedLeafCount = m.ViewedLeafCount ?? 0,
            };
        }
    }
}
