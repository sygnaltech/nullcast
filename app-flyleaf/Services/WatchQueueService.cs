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
    /// Local-only "watch later" queue, persisted to
    /// %AppData%\VideoPlayer\queue.json. Never touches the online service.
    /// Mirrors <see cref="HistoryService"/> so the two sidebar sub-tabs behave
    /// identically; the only difference is what feeds them (playing vs. dropping
    /// onto the canvas Queue zone).
    /// </summary>
    public class WatchQueueService
    {
        private static readonly string QueuePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VideoPlayer", "queue.json");

        private const int MaxEntries = 500;

        private List<QueueEntry> _entries = new();

        public async Task LoadAsync()
        {
            try
            {
                if (!File.Exists(QueuePath)) return;
                var json = await File.ReadAllTextAsync(QueuePath);
                _entries = JsonSerializer.Deserialize<List<QueueEntry>>(json) ?? new();
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
                Directory.CreateDirectory(Path.GetDirectoryName(QueuePath)!);
                var json = JsonSerializer.Serialize(_entries,
                    new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(QueuePath, json);
            }
            catch { }
        }

        /// <summary>
        /// Queue a URL to watch later. De-dupes by URL: an existing entry is bumped
        /// to the top (freshest AddedAt) rather than duplicated. Returns the
        /// (new or updated) entry.
        /// </summary>
        public QueueEntry Add(string url, string title)
        {
            if (string.IsNullOrWhiteSpace(url)) return null;
            url = url.Trim();

            var entry = _entries.FirstOrDefault(
                e => string.Equals(e.Url, url, StringComparison.OrdinalIgnoreCase));
            if (entry == null)
            {
                entry = new QueueEntry { Url = url };
                _entries.Add(entry);
            }

            if (!string.IsNullOrWhiteSpace(title))
                entry.Title = title.Trim();
            entry.AddedAt = DateTime.UtcNow;

            _entries = _entries
                .OrderByDescending(e => e.AddedAt)
                .Take(MaxEntries)
                .ToList();

            Save();
            return entry;
        }

        /// <summary>Most-recently-added-first, optionally filtered by a title/URL substring.</summary>
        public IEnumerable<QueueEntry> Search(string query)
        {
            IEnumerable<QueueEntry> results = _entries;
            if (!string.IsNullOrWhiteSpace(query))
            {
                query = query.Trim();
                results = _entries.Where(e =>
                    e.DisplayTitle.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    e.Url.Contains(query, StringComparison.OrdinalIgnoreCase));
            }
            return results.OrderByDescending(e => e.AddedAt);
        }

        /// <summary>Remove a single entry (matched by reference or URL) and persist.</summary>
        public void Delete(QueueEntry entry)
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
