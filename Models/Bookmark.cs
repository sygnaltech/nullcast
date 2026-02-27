using System;
using System.Text.Json.Serialization;

namespace VideoPlayer.Models
{
    public class Bookmark
    {
        [JsonPropertyName("id")]          public int      Id        { get; set; }
        [JsonPropertyName("muid")]        public string   Muid      { get; set; } = "";
        [JsonPropertyName("url")]         public string   Url       { get; set; } = "";
        [JsonPropertyName("title")]       public string   Title     { get; set; } = "";
        [JsonPropertyName("type")]        public string   Type      { get; set; } = "";
        [JsonPropertyName("position")]    public int?     Position  { get; set; }
        [JsonPropertyName("starred")]     public bool     Starred   { get; set; }
        [JsonPropertyName("tags")]        public string[] Tags      { get; set; } = [];
        [JsonPropertyName("created_at")]  public string   CreatedAt { get; set; } = "";
        [JsonPropertyName("updated_at")]  public string   UpdatedAt { get; set; } = "";

        [JsonIgnore]
        public bool HasPosition => Position.HasValue && Position.Value > 0;

        [JsonIgnore]
        public string PositionLabel
        {
            get
            {
                if (!HasPosition) return "";
                var ts = TimeSpan.FromSeconds(Position!.Value);
                return ts.Hours > 0
                    ? $"▶ {ts.Hours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
                    : $"▶ {ts.Minutes}:{ts.Seconds:D2}";
            }
        }
    }
}
