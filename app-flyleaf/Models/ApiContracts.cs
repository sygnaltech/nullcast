namespace VideoPlayer.Models
{
    // ──────────────────────────────────────────────────────────
    // Wire contracts for the Remote Control API (F-654).
    //
    // These are serialized/deserialized with camelCase JSON (the service configures a
    // CamelCase naming policy), so C# stays PascalCase here. All times are milliseconds.
    // ──────────────────────────────────────────────────────────

    /// <summary>The canonical state document returned by every command and GET /state.</summary>
    public class StateSnapshot
    {
        /// <summary>idle | loading | playing | paused | stopped | ended</summary>
        public string State { get; set; } = "idle";

        /// <summary>The now-playing item, or null when idle.</summary>
        public NowPlaying? Item { get; set; }

        public long PositionMs { get; set; }
        public long DurationMs { get; set; }
        public int  Volume     { get; set; }
        public bool Muted      { get; set; }
        public double Speed    { get; set; } = 1.0;
        public bool Fullscreen { get; set; }

        /// <summary>Active playlist workspace id, or null.</summary>
        public int? WorkspaceId { get; set; }

        public AppInfo App { get; set; } = new();

        /// <summary>Server epoch-milliseconds when this snapshot was taken.</summary>
        public long Ts { get; set; }
    }

    public class NowPlaying
    {
        /// <summary>youtube | plex | url | file | bookmark</summary>
        public string Type { get; set; } = "url";
        public string? Title { get; set; }
        /// <summary>Original reference where safe to expose (never a tokenized Plex URL).</summary>
        public string? SourceUrl { get; set; }
        public string? Muid { get; set; }
        public string? RatingKey { get; set; }
    }

    public class AppInfo
    {
        public string Name { get; set; } = "video-player-win";
        public string Version { get; set; } = "";
        public string Protocol { get; set; } = "v1";
    }

    // ── Request bodies ────────────────────────────────────────

    /// <summary>A typed reference to something to play. Only the fields for <see cref="Type"/> apply.</summary>
    public class MediaReference
    {
        /// <summary>youtube | plex | url | file | bookmark</summary>
        public string? Type { get; set; }
        public string? Url { get; set; }        // youtube | url
        public string? RatingKey { get; set; }  // plex
        public string? Path { get; set; }       // file
        public string? Muid { get; set; }       // bookmark
        public int? Quality { get; set; }       // youtube (max height, e.g. 1080)
    }

    public class PlayRequest
    {
        public MediaReference? Source { get; set; }
        public long? StartPositionMs { get; set; }
    }

    public class SeekRequest
    {
        public long? PositionMs { get; set; }  // absolute
        public long? DeltaMs { get; set; }     // relative (± )
    }

    public class VolumeRequest
    {
        public int? Level { get; set; }  // absolute 0..100
        public int? Delta { get; set; }  // relative (± )
        public bool? Mute { get; set; }  // set mute state
    }

    // ── Error envelope ────────────────────────────────────────

    public class ApiError
    {
        public ApiErrorBody Error { get; set; } = new();
        public ApiError() { }
        public ApiError(string code, string message) => Error = new ApiErrorBody { Code = code, Message = message };
    }

    public class ApiErrorBody
    {
        public string Code { get; set; } = "internal";
        public string Message { get; set; } = "";
    }
}
