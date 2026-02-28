using System.Text.Json.Serialization;

namespace VideoPlayer.Models
{
    public class AppSettings
    {
        [JsonPropertyName("playlist_collapsed")]
        public bool PlaylistCollapsed { get; set; }
    }
}
