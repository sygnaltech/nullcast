using System;
using System.Text.Json.Serialization;

namespace VideoPlayer.Models
{
    /// <summary>
    /// A single item on the local "watch later" queue. Like <see cref="HistoryEntry"/>
    /// this is local-only (never synced to the online bookmarks service) and
    /// de-duplicated by <see cref="Url"/>. Populated by dropping onto the canvas'
    /// Queue zone rather than by playing.
    /// </summary>
    public class QueueEntry
    {
        [JsonPropertyName("url")]      public string   Url     { get; set; } = "";
        [JsonPropertyName("title")]    public string   Title   { get; set; } = "";
        [JsonPropertyName("added_at")] public DateTime AddedAt { get; set; }

        [JsonIgnore]
        public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? Url : Title;

        [JsonIgnore]
        public string AddedLabel =>
            AddedAt == default
                ? ""
                : AddedAt.ToLocalTime().ToString("MMM d, yyyy · h:mm tt");
    }
}
