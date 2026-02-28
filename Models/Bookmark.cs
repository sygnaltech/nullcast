using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace VideoPlayer.Models
{
    public class Bookmark : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string name = "") =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

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

        // ── Runtime-only (not persisted) ──────────────────────

        private int? _durationSeconds;

        [JsonIgnore]
        public int? DurationSeconds
        {
            get => _durationSeconds;
            set
            {
                _durationSeconds = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ProgressPercent));
                OnPropertyChanged(nameof(IsCompleted));
            }
        }

        [JsonIgnore]
        public double ProgressPercent =>
            (Position.HasValue && DurationSeconds is int dur && dur > 0)
                ? Math.Min(100.0, Position.Value * 100.0 / dur)
                : 0;

        [JsonIgnore]
        public bool IsCompleted =>
            DurationSeconds is int dur && dur > 0 &&
            Position.HasValue && Position.Value >= dur;
    }
}
