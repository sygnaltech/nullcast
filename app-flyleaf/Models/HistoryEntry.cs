using System;
using System.Text.Json.Serialization;

namespace VideoPlayer.Models
{
    /// <summary>
    /// A single locally-recorded playback. History is local-only (never synced
    /// to the online bookmarks service). De-duplicated by <see cref="Url"/>.
    /// </summary>
    public class HistoryEntry
    {
        [JsonPropertyName("url")]         public string   Url          { get; set; } = "";
        [JsonPropertyName("title")]       public string   Title        { get; set; } = "";
        [JsonPropertyName("last_played")] public DateTime LastPlayedAt { get; set; }
        [JsonPropertyName("play_count")]  public int      PlayCount    { get; set; }

        [JsonIgnore]
        public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? Url : Title;

        [JsonIgnore]
        public string LastPlayedLabel =>
            LastPlayedAt == default
                ? ""
                : LastPlayedAt.ToLocalTime().ToString("MMM d, yyyy · h:mm tt");
    }
}
