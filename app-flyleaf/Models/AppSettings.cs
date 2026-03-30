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
    }
}
