using System.Text.Json.Serialization;

namespace VideoPlayer.Models
{
    public class Workspace
    {
        [JsonPropertyName("id")]         public int    Id        { get; set; }
        [JsonPropertyName("name")]       public string Name      { get; set; } = "";
        [JsonPropertyName("is_default")] public int    IsDefault { get; set; }

        public override string ToString() => Name;
    }
}
