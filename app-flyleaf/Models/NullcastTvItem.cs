using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Serialization;

namespace VideoPlayer.Models
{
    // ──────────────────────────────────────────────────────────
    // Nullcast.TV REST DTOs (subset of https://nullcast.tv/api/v1).
    //
    // Field names mirror the wire exactly — the service is TypeScript
    // and emits camelCase. Only the fields this client actually reads
    // are declared; the payload carries considerably more.
    //
    // Timestamps are epoch MILLISECONDS. `publishedAt` is when Nullcast
    // put a film on air; `releasedAt` is when the film came out. They
    // are not the same thing and the UI never conflates them.
    // ──────────────────────────────────────────────────────────

    /// <summary>One film as the catalog serves it.</summary>
    public class TvApiVideo
    {
        [JsonPropertyName("id")]          public string   Id          { get; set; } = "";
        [JsonPropertyName("title")]       public string   Title       { get; set; } = "";
        [JsonPropertyName("description")] public string   Description { get; set; } = "";
        [JsonPropertyName("source")]      public string   Source      { get; set; } = "";
        [JsonPropertyName("sourceId")]    public string   SourceId    { get; set; } = "";
        [JsonPropertyName("sourceUrl")]   public string   SourceUrl   { get; set; } = "";

        /// <summary>Absolute URL on the host platform's CDN. Always present for a linked film.</summary>
        [JsonPropertyName("thumbUrl")]    public string   ThumbUrl    { get; set; } = "";

        /// <summary>
        /// A <c>/api/poster/{id}</c> RELATIVE path when a studio captured its own still, else null.
        /// Preferred over <see cref="ThumbUrl"/> — it is the frame the maker chose, and the only
        /// image URL under Nullcast's control. Must be joined to the origin before use.
        /// </summary>
        [JsonPropertyName("posterUrl")]   public string   PosterUrl   { get; set; }

        [JsonPropertyName("durationMs")]  public long     DurationMs  { get; set; }

        /// <summary>width / height, or null when nobody measured it. Never read width/height —
        /// those are 0 for a linked film.</summary>
        [JsonPropertyName("aspect")]      public double?  Aspect      { get; set; }

        [JsonPropertyName("model")]       public string   Model       { get; set; } = "";
        [JsonPropertyName("creator")]     public string   Creator     { get; set; } = "";

        /// <summary>Up to three genre IDs (not labels), primary first. Empty = uncatalogued.</summary>
        [JsonPropertyName("genres")]      public List<string> Genres  { get; set; } = new();

        /// <summary>BCP-47 primary subtag, or null for "not specified". 'zxx' = no dialogue.</summary>
        [JsonPropertyName("language")]    public string   Language    { get; set; }

        /// <summary>Always present; 'unrated' until classified.</summary>
        [JsonPropertyName("rating")]      public string   Rating      { get; set; } = "unrated";

        [JsonPropertyName("series")]      public TvVideoSeriesRef Series { get; set; }

        [JsonPropertyName("heartCount")]  public int      HeartCount  { get; set; }
        [JsonPropertyName("viewCount")]   public int      ViewCount   { get; set; }

        /// <summary>When Nullcast put it on air (epoch ms), or null.</summary>
        [JsonPropertyName("publishedAt")] public long?    PublishedAt { get; set; }
        /// <summary>When the film came out (epoch ms), or null. Often years before publishedAt.</summary>
        [JsonPropertyName("releasedAt")]  public long?    ReleasedAt  { get; set; }
        /// <summary>True when the release date was reckoned rather than read off the platform.</summary>
        [JsonPropertyName("releasedAtEstimated")] public bool ReleasedAtEstimated { get; set; }
    }

    /// <summary>What a film says about the series it belongs to.</summary>
    public class TvVideoSeriesRef
    {
        [JsonPropertyName("id")]      public int    Id      { get; set; }
        [JsonPropertyName("slug")]    public string Slug    { get; set; } = "";
        [JsonPropertyName("title")]   public string Title   { get; set; } = "";
        /// <summary>Place in the run, or null for "in the series, not yet numbered".</summary>
        [JsonPropertyName("episode")] public int?   Episode { get; set; }
    }

    /// <summary>A saved way of looking at the catalog.</summary>
    public class TvApiChannel
    {
        [JsonPropertyName("id")]    public int    Id    { get; set; }
        [JsonPropertyName("slug")]  public string Slug  { get; set; } = "";
        [JsonPropertyName("name")]  public string Name  { get; set; } = "";
        [JsonPropertyName("blurb")] public string Blurb { get; set; } = "";

        /// <summary>"query" (an ordering over the catalog), "curated" (a hand-built shelf),
        /// or "facet" (the catalog grouped by a column — a directory, not a feed).</summary>
        [JsonPropertyName("kind")]  public string Kind  { get; set; } = "";

        /// <summary>"model" | "creator" | "genre" | "language" on a facet channel, else null.</summary>
        [JsonPropertyName("facet")] public string Facet { get; set; }

        /// <summary>How many films carry this channel — NOT how many distinct facet values it has.</summary>
        [JsonPropertyName("count")] public int    Count { get; set; }

        [JsonIgnore] public bool IsFacet => string.Equals(Kind, "facet", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>One value inside a facet channel's directory (a "sub-channel").</summary>
    public class TvFacetValue
    {
        [JsonPropertyName("value")]    public string Value    { get; set; } = "";
        [JsonPropertyName("count")]    public int    Count    { get; set; }
        /// <summary>The most-hearted film's still — absolute, or a relative /api/poster/… path.</summary>
        [JsonPropertyName("thumbUrl")] public string ThumbUrl { get; set; }
    }

    /// <summary>One page of a channel: either videos, or (facet channel, no value) a directory.</summary>
    public class TvChannelFeed
    {
        [JsonPropertyName("channel")] public TvApiChannel       Channel { get; set; }
        [JsonPropertyName("videos")]  public List<TvApiVideo>   Videos  { get; set; } = new();
        [JsonPropertyName("facets")]  public List<TvFacetValue> Facets  { get; set; } = new();
        /// <summary>Opaque continuation. null means end of the list — never compute one.</summary>
        [JsonPropertyName("cursor")]  public string             Cursor  { get; set; }
    }

    /// <summary>A facet value that matched a search, tagged with the facet it belongs to.</summary>
    public class TvSearchChannelHit : TvFacetValue
    {
        [JsonPropertyName("facet")] public string Facet { get; set; } = "";
    }

    /// <summary>A series as its own page serves it.</summary>
    public class TvApiSeries
    {
        [JsonPropertyName("id")]           public int    Id           { get; set; }
        [JsonPropertyName("slug")]         public string Slug         { get; set; } = "";
        [JsonPropertyName("title")]        public string Title        { get; set; } = "";
        [JsonPropertyName("description")]  public string Description  { get; set; } = "";
        /// <summary>'' when nobody set one — the UI falls back to the first episode's still.</summary>
        [JsonPropertyName("thumbUrl")]     public string ThumbUrl     { get; set; } = "";
        /// <summary>Episodes ON AIR (a pending or hidden one is not in the run).</summary>
        [JsonPropertyName("episodeCount")] public int    EpisodeCount { get; set; }
    }

    /// <summary>A series and its episodes, in running order.</summary>
    public class TvSeriesFeed
    {
        [JsonPropertyName("series")]   public TvApiSeries      Series   { get; set; }
        [JsonPropertyName("episodes")] public List<TvApiVideo> Episodes { get; set; } = new();
    }

    /// <summary>Three lists, because a film, a channel and a series are different destinations.</summary>
    public class TvSearchResults
    {
        [JsonPropertyName("query")]    public string                   Query    { get; set; } = "";
        [JsonPropertyName("videos")]   public List<TvApiVideo>         Videos   { get; set; } = new();
        [JsonPropertyName("channels")] public List<TvSearchChannelHit> Channels { get; set; } = new();
        [JsonPropertyName("series")]   public List<TvApiSeries>        Series   { get; set; } = new();
    }

    /// <summary>The signed-in half of <c>GET /api/v1/me</c> — only the identity is read.</summary>
    public class TvSessionInfo
    {
        [JsonPropertyName("user")] public TvUser User { get; set; }
    }

    public class TvUser
    {
        [JsonPropertyName("id")]    public int    Id    { get; set; }
        [JsonPropertyName("email")] public string Email { get; set; } = "";
        [JsonPropertyName("name")]  public string Name  { get; set; } = "";
    }

    // ──────────────────────────────────────────────────────────
    // Vocabularies.
    //
    // Genre and language IDs are a closed set defined in the service's
    // TypeScript and deliberately NOT served over HTTP, so a non-TS
    // client has to carry its own copy. Genres are vendored verbatim;
    // languages go through CultureInfo, which already knows every
    // ISO 639-1 subtag the catalog can store.
    // ──────────────────────────────────────────────────────────

    public static class TvVocabulary
    {
        /// <summary>Genre id → display label, vendored from the service's <c>shared/genres.ts</c>.</summary>
        private static readonly Dictionary<string, string> GenreLabelById =
            new(StringComparer.OrdinalIgnoreCase)
            {
                // Narrative
                ["action"] = "Action",                 ["adventure"] = "Adventure",
                ["comedy"] = "Comedy",                 ["coming-of-age"] = "Coming of Age",
                ["crime"] = "Crime",                   ["drama"] = "Drama",
                ["family"] = "Family",                 ["fantasy"] = "Fantasy",
                ["historical"] = "Historical",         ["horror"] = "Horror",
                ["musical"] = "Musical",               ["mystery"] = "Mystery",
                ["noir"] = "Noir",                     ["post-apocalyptic"] = "Post-Apocalyptic",
                ["romance"] = "Romance",               ["science-fiction"] = "Science Fiction",
                ["superhero"] = "Superhero",           ["thriller"] = "Thriller",
                ["war"] = "War",                       ["western"] = "Western",
                // Traditions
                ["anime"] = "Anime",                   ["bollywood"] = "Bollywood",
                ["c-drama"] = "C-Drama",               ["j-drama"] = "J-Drama",
                ["k-drama"] = "K-Drama",               ["telenovela"] = "Telenovela",
                ["wuxia"] = "Wuxia",
                // Non-fiction
                ["biography"] = "Biography",           ["documentary"] = "Documentary",
                ["educational"] = "Educational",       ["food"] = "Food",
                ["nature"] = "Nature",                 ["sports"] = "Sports",
                ["travel"] = "Travel",                 ["true-crime"] = "True Crime",
                // Format
                ["asmr"] = "ASMR",                     ["commercial"] = "Commercial",
                ["fashion"] = "Fashion",               ["machinima"] = "Machinima",
                ["mockumentary"] = "Mockumentary",     ["music-video"] = "Music Video",
                ["sketch"] = "Sketch",                 ["trailer"] = "Trailer",
                ["vlog"] = "Vlog",
                // Style & technique
                ["abstract"] = "Abstract",             ["ambient"] = "Ambient",
                ["animation"] = "Animation",           ["cyberpunk"] = "Cyberpunk",
                ["experimental"] = "Experimental",     ["glitch"] = "Glitch",
                ["retro"] = "Retro",                   ["stop-motion"] = "Stop Motion",
                ["surreal"] = "Surreal",
            };

        /// <summary>
        /// A genre id as a display string. Unknown ids (the vocabulary grew server-side) fall
        /// back to the slug with its hyphens opened out, which reads acceptably because the ids
        /// are slugified labels.
        /// </summary>
        public static string GenreLabel(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return "";
            if (GenreLabelById.TryGetValue(id, out var label)) return label;
            var words = id.Split('-', StringSplitOptions.RemoveEmptyEntries)
                          .Select(w => char.ToUpperInvariant(w[0]) + w[1..]);
            return string.Join(" ", words);
        }

        /// <summary>Map a film's genre ids to display labels, dropping anything blank.</summary>
        public static List<string> GenreLabels(IEnumerable<string> ids) =>
            ids == null ? new List<string>()
                        : ids.Select(GenreLabel).Where(s => s.Length > 0).ToList();

        /// <summary>
        /// A BCP-47 primary subtag as a display string. Two codes are not languages but are
        /// valid answers to "what language is this?" and are spelled out; everything else goes
        /// through <see cref="CultureInfo"/>, which already knows the ISO 639-1 set.
        /// </summary>
        public static string LanguageLabel(string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return "";
            if (code.Equals("zxx", StringComparison.OrdinalIgnoreCase)) return "No dialogue";
            if (code.Equals("mul", StringComparison.OrdinalIgnoreCase)) return "Multilingual";
            try
            {
                var ci = CultureInfo.GetCultureInfo(code);
                return ci.EnglishName;
            }
            catch (CultureNotFoundException)
            {
                return code.ToUpperInvariant();
            }
        }

        /// <summary>Facet slug → the label the browse UI prints for the whole group.</summary>
        public static string FacetLabel(string facet) => (facet ?? "").ToLowerInvariant() switch
        {
            "model"    => "Model",
            "creator"  => "Creator",
            "genre"    => "Genre",
            "language" => "Language",
            _          => "",
        };

        /// <summary>
        /// A facet VALUE printed for a human. Genre values are vocabulary ids and need looking
        /// up; language values are subtags; model and creator values are already display strings.
        /// </summary>
        public static string FacetValueLabel(string facet, string value) =>
            (facet ?? "").ToLowerInvariant() switch
            {
                "genre"    => GenreLabel(value),
                "language" => LanguageLabel(value),
                _          => value ?? "",
            };
    }

    // ──────────────────────────────────────────────────────────
    // Browse view-models (segment bar → category dropdown → list).
    //
    // Mirrors the Plex tab's shape deliberately: the two integrations
    // are browsed the same way, so the muscle memory carries over.
    // ──────────────────────────────────────────────────────────

    /// <summary>Which top-level segment of the Nullcast.TV tab is showing.</summary>
    public enum TvSection { Browse, Series, Search }

    /// <summary>What a row in the results list actually is, and therefore what clicking it does.</summary>
    public enum TvItemKind
    {
        /// <summary>A film. Plays.</summary>
        Video,
        /// <summary>A facet value ("@qaramood", "science-fiction"). Drills into its films.</summary>
        Facet,
        /// <summary>A series. Drills into its episodes.</summary>
        Series,
    }

    /// <summary>
    /// One entry in the category dropdown — a channel off <c>GET /public/channels</c>.
    /// <see cref="Group"/> ("Views" / "Browse by") drives the grouped popup; a facet channel
    /// opens a directory of values rather than a feed.
    /// </summary>
    public class TvCategory
    {
        public TvCategory() { }
        public TvCategory(TvApiChannel channel, string group)
        {
            Channel = channel;
            Group   = group;
            Label   = channel.Name;
        }

        public string       Label   { get; set; } = "";
        public string       Group   { get; set; } = "";
        public TvApiChannel Channel { get; set; }

        public string Slug    => Channel?.Slug ?? "";
        public bool   IsFacet => Channel?.IsFacet == true;
        public string Facet   => Channel?.Facet ?? "";
    }

    // ──────────────────────────────────────────────────────────
    // The row bound to the Nullcast.TV results list.
    //
    // One view-model for all three row kinds so a single template
    // pair (list + 16:9 tile) covers the whole tab, exactly as
    // PlexItem does for Plex.
    // ──────────────────────────────────────────────────────────

    public class TvItem
    {
        public TvItemKind Kind { get; set; }

        public string Title    { get; set; } = "";
        public string Subtitle { get; set; } = "";

        /// <summary>Absolute, ready-to-bind image URL, or null.</summary>
        public string ThumbUrl { get; set; }
        public bool   HasThumb => !string.IsNullOrEmpty(ThumbUrl);

        // ── Video ────────────────────────────────────────────
        public string VideoId   { get; set; } = "";
        /// <summary>Where the film actually lives — handed to yt-dlp like any other page URL.</summary>
        public string SourceUrl { get; set; } = "";
        public string Source    { get; set; } = "";
        public long   DurationMs { get; set; }
        public int    HeartCount { get; set; }
        public string Model      { get; set; } = "";
        public string Creator    { get; set; } = "";
        public string Rating     { get; set; } = "";
        public string LanguageLabel { get; set; } = "";
        /// <summary>Genre display labels, primary first.</summary>
        public List<string> Genres { get; set; } = new();
        /// <summary>Episode number within its series, or 0 when it is not (or not yet) numbered.</summary>
        public int EpisodeNumber { get; set; }

        // ── Facet value ──────────────────────────────────────
        /// <summary>The channel slug this value came from (e.g. "creators"), for the drill query.</summary>
        public string FacetSlug  { get; set; } = "";
        /// <summary>The raw stored value, passed straight back as the <c>facet</c> parameter.</summary>
        public string FacetValue { get; set; } = "";

        // ── Series ───────────────────────────────────────────
        public string SeriesSlug { get; set; } = "";

        /// <summary>How many films sit under a facet value, or episodes under a series.</summary>
        public int ChildCount { get; set; }

        /// <summary>Facet values and series are navigational; a film is a leaf you play.</summary>
        public bool IsContainer => Kind != TvItemKind.Video;
        public bool IsPlayable  => Kind == TvItemKind.Video;

        public bool   HasEpisodeBadge => EpisodeNumber > 0;
        public string EpisodeBadge    => HasEpisodeBadge ? $"E{EpisodeNumber}" : "";
        /// <summary>Zero-padded label for the aligned episode list ("•" when unnumbered).</summary>
        public string EpisodeNumberLabel => HasEpisodeBadge ? $"E{EpisodeNumber:D2}" : "•";
        /// <summary>The leading art column has something to show.</summary>
        public bool HasLeadArt => HasThumb || HasEpisodeBadge;

        public bool   HasTags => Genres.Count > 0;
        public string TagLine => string.Join("  ·  ", Genres);

        /// <summary>Runtime as m:ss / h:mm:ss, or "" when the catalog doesn't know (durationMs 0).</summary>
        public string DurationLabel
        {
            get
            {
                if (DurationMs <= 0) return "";
                var ts = TimeSpan.FromMilliseconds(DurationMs);
                return ts.Hours > 0
                    ? $"{ts.Hours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
                    : $"{ts.Minutes}:{ts.Seconds:D2}";
            }
        }

        /// <summary>
        /// The accent line under the title. Containers print what is inside them; a film prints
        /// runtime, hearts and — only when it is actually restricted — its rating.
        /// </summary>
        public string MetaLine
        {
            get
            {
                if (Kind == TvItemKind.Facet)
                    return $"{ChildCount} film{(ChildCount == 1 ? "" : "s")}";
                if (Kind == TvItemKind.Series)
                    return $"{ChildCount} episode{(ChildCount == 1 ? "" : "s")}";

                var parts = new List<string>();
                if (DurationLabel.Length > 0) parts.Add(DurationLabel);
                if (HeartCount > 0)           parts.Add($"♥ {HeartCount}");
                if (IsRestricted)             parts.Add(Rating.ToUpperInvariant());
                return string.Join("  ·  ", parts);
            }
        }

        /// <summary>18+ content. Worth badging; 'unrated' and the general ratings are not.</summary>
        public bool IsRestricted =>
            Rating.Equals("adult", StringComparison.OrdinalIgnoreCase) ||
            Rating.Equals("explicit", StringComparison.OrdinalIgnoreCase);

        // ── Factories ────────────────────────────────────────

        /// <summary>
        /// Build a row from a film. <paramref name="resolveThumb"/> joins a relative
        /// <c>posterUrl</c> to the API origin — the caller owns the origin, this type does not.
        /// </summary>
        public static TvItem FromVideo(TvApiVideo v, Func<TvApiVideo, string> resolveThumb)
        {
            var credits = new[] { v.Creator, v.Model }.Where(s => !string.IsNullOrWhiteSpace(s));
            var subtitle = string.Join("  ·  ", credits);
            if (subtitle.Length == 0 && v.Series != null) subtitle = v.Series.Title;

            return new TvItem
            {
                Kind          = TvItemKind.Video,
                Title         = string.IsNullOrWhiteSpace(v.Title) ? "(untitled)" : v.Title,
                Subtitle      = subtitle,
                ThumbUrl      = resolveThumb?.Invoke(v),
                VideoId       = v.Id ?? "",
                SourceUrl     = v.SourceUrl ?? "",
                Source        = v.Source ?? "",
                DurationMs    = v.DurationMs,
                HeartCount    = v.HeartCount,
                Model         = v.Model ?? "",
                Creator       = v.Creator ?? "",
                Rating        = v.Rating ?? "",
                LanguageLabel = TvVocabulary.LanguageLabel(v.Language),
                Genres        = TvVocabulary.GenreLabels(v.Genres),
                EpisodeNumber = v.Series?.Episode ?? 0,
            };
        }

        /// <summary>Build a row from a directory entry inside a facet channel.</summary>
        public static TvItem FromFacet(TvFacetValue f, string channelSlug, string facet, string thumbUrl) =>
            new()
            {
                Kind       = TvItemKind.Facet,
                Title      = TvVocabulary.FacetValueLabel(facet, f.Value),
                Subtitle   = TvVocabulary.FacetLabel(facet),
                ThumbUrl   = thumbUrl,
                FacetSlug  = channelSlug,
                FacetValue = f.Value ?? "",
                ChildCount = f.Count,
            };

        /// <summary>Build a row from a series.</summary>
        public static TvItem FromSeries(TvApiSeries s, string thumbUrl) =>
            new()
            {
                Kind       = TvItemKind.Series,
                Title      = string.IsNullOrWhiteSpace(s.Title) ? s.Slug : s.Title,
                Subtitle   = s.Description ?? "",
                ThumbUrl   = thumbUrl,
                SeriesSlug = s.Slug ?? "",
                ChildCount = s.EpisodeCount,
            };
    }
}
