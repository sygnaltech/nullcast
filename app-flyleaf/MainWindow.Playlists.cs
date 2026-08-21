using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using VideoPlayer.Models;
using VideoPlayer.Services;

namespace VideoPlayer
{
    // ──────────────────────────────────────────────────────────────────────
    // Send History / Queue rows to an online playlist (workspace).
    //
    // History and Queue are local-only lists; playlists live on the bookmarks
    // service. Right-clicking a row offers "Add to playlist ▸" (copy) and
    // "Move to playlist ▸" (copy, then drop the local row). Both fan out to one
    // submenu entry per workspace, rebuilt each time the menu opens so a
    // workspace added elsewhere in the session shows up without a restart.
    //
    // Every outcome — added, already there, failed — reports back through the
    // bottom-centre toast, because the list that changed is usually not the one
    // the user is looking at and would otherwise change silently.
    // ──────────────────────────────────────────────────────────────────────
    public partial class MainWindow
    {
        // Feather-style icon path data (24×24), matching the existing menu icons.
        private const string IconFolderPlus =
            "M22 19 a2 2 0 0 1 -2 2 H4 a2 2 0 0 1 -2 -2 V5 a2 2 0 0 1 2 -2 h5 l2 3 h7 a2 2 0 0 1 2 2 z M12 11 v6 M9 14 h6";
        private const string IconArrowRightCircle =
            "M12 22 a10 10 0 1 0 0 -20 a10 10 0 0 0 0 20 M12 16 l4 -4 l-4 -4 M8 12 h8";

        private MenuItem _historyAddToPlaylist;
        private MenuItem _historyMoveToPlaylist;
        private MenuItem _queueAddToPlaylist;
        private MenuItem _queueMoveToPlaylist;

        /// <summary>
        /// Build the "Add to playlist" / "Move to playlist" submenu headers and splice them
        /// into the History and Queue context menus, above the existing Delete/Remove item.
        /// Called once from the constructor, after those menus exist.
        /// </summary>
        private void AttachPlaylistMenus(Style menuItemStyle)
        {
            _historyAddToPlaylist  = MakePlaylistHeader("Add to playlist",  IconFolderPlus,       menuItemStyle);
            _historyMoveToPlaylist = MakePlaylistHeader("Move to playlist", IconArrowRightCircle, menuItemStyle);
            _queueAddToPlaylist    = MakePlaylistHeader("Add to playlist",  IconFolderPlus,       menuItemStyle);
            _queueMoveToPlaylist   = MakePlaylistHeader("Move to playlist", IconArrowRightCircle, menuItemStyle);

            _historyContextMenu.Items.Insert(0, _historyAddToPlaylist);
            _historyContextMenu.Items.Insert(1, _historyMoveToPlaylist);
            _historyContextMenu.Opened += HistoryContextMenu_Opened;

            _queueContextMenu.Items.Insert(0, _queueAddToPlaylist);
            _queueContextMenu.Items.Insert(1, _queueMoveToPlaylist);
            _queueContextMenu.Opened += QueueContextMenu_Opened;
        }

        private static MenuItem MakePlaylistHeader(string header, string icon, Style menuItemStyle)
        {
            var item = new MenuItem
            {
                Header = header,
                Icon   = MakeMenuIcon(icon),
                // Submenu children aren't covered by the ContextMenu's ItemContainerStyle,
                // so hand them the same themed row style explicitly.
                ItemContainerStyle = menuItemStyle,
            };
            // A childless MenuItem has Role=SubmenuItem and renders without a flyout; seed a
            // placeholder so it starts life as a SubmenuHeader. Replaced on every Opened.
            item.Items.Add(new MenuItem { Header = "...", IsEnabled = false });
            return item;
        }

        // ── Menu opening: rebuild both submenus for the row under the cursor ──

        private void HistoryContextMenu_Opened(object sender, RoutedEventArgs e)
        {
            var entry = _historyContextMenuTarget;
            PopulatePlaylistSubmenus(
                entry?.Url,
                _historyAddToPlaylist, _historyMoveToPlaylist,
                ws => _ = SendToPlaylistAsync(entry?.Url, entry?.Title, ws, null, null),
                ws => _ = SendToPlaylistAsync(entry?.Url, entry?.Title, ws,
                             () => { _history.Delete(entry); HistoryItems.Remove(entry); }, "History"));
        }

        private void QueueContextMenu_Opened(object sender, RoutedEventArgs e)
        {
            var entry = _queueContextMenuTarget;
            PopulatePlaylistSubmenus(
                entry?.Url,
                _queueAddToPlaylist, _queueMoveToPlaylist,
                ws => _ = SendToPlaylistAsync(entry?.Url, entry?.Title, ws, null, null),
                ws => _ = SendToPlaylistAsync(entry?.Url, entry?.Title, ws,
                             () => { _watchQueue.Delete(entry); QueueItems.Remove(entry); RefreshQueueView(); }, "Queue"));
        }

        /// <summary>
        /// Fill both submenu headers with one entry per workspace, or disable them with a
        /// reason when the row can't go to a playlist at all.
        /// </summary>
        private void PopulatePlaylistSubmenus(string url, MenuItem addItem, MenuItem moveItem,
                                              Action<Workspace> onAdd, Action<Workspace> onMove)
        {
            string blocked = PlaylistTargetProblem(url);

            foreach (var (header, pick) in new[] { (addItem, onAdd), (moveItem, onMove) })
            {
                header.IsEnabled = blocked == null;

                // Append first, then drop the previous rows. Emptying Items would clear
                // HasItems, flipping the header's Role back to SubmenuItem and re-applying
                // the leaf template (which has no PART_Popup) while the menu is on screen.
                int stale = header.Items.Count;

                if (blocked != null)
                {
                    header.Items.Add(new MenuItem { Header = blocked, IsEnabled = false });
                }
                else
                {
                    foreach (var ws in _workspaces)
                    {
                        var target = ws;                   // capture per iteration
                        var row = new MenuItem { Header = ws.Name };
                        row.Click += (_, _) => pick(target);
                        header.Items.Add(row);
                    }
                }

                for (int i = 0; i < stale; i++)
                    header.Items.RemoveAt(0);
            }
        }

        /// <summary>Why this row can't be sent to a playlist, or null when it can.</summary>
        private string PlaylistTargetProblem(string url)
        {
            if (string.IsNullOrWhiteSpace(url))            return "Nothing to add";
            if (_auth?.IsSignedIn != true || _api == null) return "Sign in to use playlists";
            if (_workspaces.Count == 0)                    return "No playlists available";

            // Playlists hold shareable web links. A plex:// row is a rating key that only
            // resolves against this machine's configured server, and a local file path means
            // nothing to anyone else — neither belongs in an online playlist.
            if (!url.StartsWith("http://",  StringComparison.OrdinalIgnoreCase) &&
                !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return url.StartsWith("plex://", StringComparison.OrdinalIgnoreCase)
                    ? "Plex items can't be added"
                    : "Only web links can be added";

            return null;
        }

        // ── The actual add / move ────────────────────────────────────────

        /// <summary>
        /// Copy <paramref name="url"/> into <paramref name="ws"/>, reporting the outcome as a toast.
        /// When <paramref name="removeFromSource"/> is non-null this is a move: the local row is
        /// dropped once the item is known to be in the playlist — including when it was already
        /// there, since the intent ("get this out of here and into that playlist") is satisfied
        /// either way. <paramref name="sourceLabel"/> names that list in the toast.
        /// </summary>
        private async Task SendToPlaylistAsync(string url, string title, Workspace ws,
                                               Action removeFromSource, string sourceLabel)
        {
            if (ws == null || string.IsNullOrWhiteSpace(url) || _api == null) return;
            url = url.Trim();

            string movedSuffix = removeFromSource != null && sourceLabel != null
                ? $" — removed from {sourceLabel}"
                : "";

            try
            {
                if (await FindInWorkspaceAsync(url, ws) != null)
                {
                    removeFromSource?.Invoke();
                    ShowToast($"Already in “{ws.Name}”{movedSuffix}", ToastTone.Neutral);
                    Telemetry.Track("playlist_add_skipped", new()
                    {
                        ["reason"] = "duplicate",
                        ["moved"]  = removeFromSource != null,
                    });
                    return;
                }

                var created = await _api.CreateBookmarkAsync(url, ws.Id, title);
                if (created == null)
                {
                    ShowToast($"Couldn't add to “{ws.Name}”", ToastTone.Error);
                    return;
                }

                removeFromSource?.Invoke();

                // Only the visible playlist needs re-pulling; other workspaces load on demand.
                if (_selectedWorkspace != null && _selectedWorkspace.Id == ws.Id)
                    await RefreshBookmarksAsync(ws.Id);

                ShowToast($"Added to “{ws.Name}”{movedSuffix}", ToastTone.Success);
                Telemetry.Track("playlist_add", new()
                {
                    ["source"] = sourceLabel ?? "context_menu",
                    ["moved"]  = removeFromSource != null,
                    ["url"]    = url,
                });
            }
            catch (Exception ex)
            {
                App.Log($"[Playlist] Add to '{ws.Name}' failed: {ex}");
                ShowToast($"Couldn't add to “{ws.Name}”: {ex.Message}", ToastTone.Error);
            }
        }

        /// <summary>
        /// The bookmark in <paramref name="ws"/> with this URL, or null. Reads the in-memory list
        /// for the workspace already on screen and only hits the network for the others, so the
        /// common "add to the playlist I'm looking at" case costs no extra request.
        /// </summary>
        private async Task<Bookmark> FindInWorkspaceAsync(string url, Workspace ws)
        {
            IEnumerable<Bookmark> items =
                (_selectedWorkspace != null && _selectedWorkspace.Id == ws.Id)
                    ? PlaylistItems
                    : await _api.GetBookmarksAsync(ws.Id);

            return items.FirstOrDefault(b => SameUrl(b.Url, url));
        }

        /// <summary>
        /// Case-insensitive URL match ignoring a trailing slash — without it a stored
        /// "…/watch?v=x/" would read as new and we'd claim "Added" for a row the server
        /// then silently de-dupes.
        /// </summary>
        private static bool SameUrl(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            return string.Equals(a.TrimEnd('/'), b.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
        }

        // ── Toast ────────────────────────────────────────────────────────

        private enum ToastTone { Success, Neutral, Error }

        private DispatcherTimer _toastTimer;

        private static readonly Brush ToastDotSuccess = new SolidColorBrush(Color.FromRgb(0x7D, 0x97, 0xFF));
        private static readonly Brush ToastDotNeutral = new SolidColorBrush(Color.FromRgb(0x84, 0x8B, 0x9F));
        private static readonly Brush ToastDotError   = new SolidColorBrush(Color.FromRgb(0xFF, 0x66, 0x66));

        /// <summary>
        /// Flash a short confirmation at the bottom of the window. Re-entrant: a second call
        /// replaces the message in place and restarts the hold, so a burst of actions doesn't
        /// leave a backlog of stale cards.
        /// </summary>
        private void ShowToast(string message, ToastTone tone = ToastTone.Success)
        {
            if (ToastPopup == null || string.IsNullOrWhiteSpace(message)) return;

            ToastText.Text = message;
            ToastDot.Fill = tone switch
            {
                ToastTone.Error   => ToastDotError,
                ToastTone.Neutral => ToastDotNeutral,
                _                 => ToastDotSuccess,
            };

            ToastCard.BeginAnimation(UIElement.OpacityProperty, null);
            ToastCard.Opacity = 0;
            ToastPopup.IsOpen = true;
            ToastCard.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));

            _toastTimer ??= new DispatcherTimer();
            _toastTimer.Stop();
            _toastTimer.Interval = TimeSpan.FromMilliseconds(tone == ToastTone.Error ? 4200 : 2600);
            _toastTimer.Tick -= ToastTimer_Tick;
            _toastTimer.Tick += ToastTimer_Tick;
            _toastTimer.Start();
        }

        private void ToastTimer_Tick(object sender, EventArgs e)
        {
            _toastTimer.Stop();
            if (ToastCard == null) return;

            var fade = new DoubleAnimation(ToastCard.Opacity, 0, TimeSpan.FromMilliseconds(260));
            fade.Completed += (_, _) =>
            {
                // A newer toast may have opened during the fade — don't close it out from under.
                if (ToastCard.Opacity <= 0.01) ToastPopup.IsOpen = false;
            };
            ToastCard.BeginAnimation(UIElement.OpacityProperty, fade);
        }
    }
}
