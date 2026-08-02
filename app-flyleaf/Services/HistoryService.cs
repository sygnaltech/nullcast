using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using VideoPlayer.Models;

namespace VideoPlayer.Services
{
    /// <summary>
    /// Local-only playback history, persisted to
    /// %AppData%\VideoPlayer\history.json. Never touches the online service.
    /// </summary>
    public class HistoryService
    {
        private static readonly string HistoryPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VideoPlayer", "history.json");

        private const int MaxEntries = 500;

        private List<HistoryEntry> _entries = new();

        public async Task LoadAsync()
        {
            try
            {
                if (!File.Exists(HistoryPath)) return;
                var json = await File.ReadAllTextAsync(HistoryPath);
                _entries = JsonSerializer.Deserialize<List<HistoryEntry>>(json) ?? new();
            }
            catch
            {
                _entries = new();
            }
        }

        private void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(HistoryPath)!);
                var json = JsonSerializer.Serialize(_entries,
                    new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(HistoryPath, json);
            }
            catch { }
        }

        /// <summary>
        /// Record a play. De-dupes by URL: bumps play count + timestamp and moves
        /// the entry to the top. Returns the (new or updated) entry.
        /// </summary>
        public HistoryEntry Record(string url, string title)
        {
            if (string.IsNullOrWhiteSpace(url)) return null;
            url = url.Trim();

            var entry = _entries.FirstOrDefault(
                e => string.Equals(e.Url, url, StringComparison.OrdinalIgnoreCase));
            if (entry == null)
            {
                entry = new HistoryEntry { Url = url };
                _entries.Add(entry);
            }

            if (!string.IsNullOrWhiteSpace(title))
                entry.Title = title.Trim();
            entry.LastPlayedAt = DateTime.UtcNow;
            entry.PlayCount++;

            _entries = _entries
                .OrderByDescending(e => e.LastPlayedAt)
                .Take(MaxEntries)
                .ToList();

            Save();
            return entry;
        }

        /// <summary>Most-recent-first, optionally filtered by a title/URL substring.</summary>
        public IEnumerable<HistoryEntry> Search(string query)
        {
            IEnumerable<HistoryEntry> results = _entries;
            if (!string.IsNullOrWhiteSpace(query))
            {
                query = query.Trim();
                results = _entries.Where(e =>
                    e.DisplayTitle.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    e.Url.Contains(query, StringComparison.OrdinalIgnoreCase));
            }
            return results.OrderByDescending(e => e.LastPlayedAt);
        }

        /// <summary>Remove a single entry (matched by reference or URL) and persist.</summary>
        public void Delete(HistoryEntry entry)
        {
            if (entry == null) return;
            _entries.RemoveAll(e => ReferenceEquals(e, entry)
                || string.Equals(e.Url, entry.Url, StringComparison.OrdinalIgnoreCase));
            Save();
        }

        public void Clear()
        {
            _entries.Clear();
            Save();
        }
    }
}
