using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using YouTubeVideoPlayer.Infrastructure;
using YouTubeVideoPlayer.Playlists;

namespace YouTubeVideoPlayer.UI
{
    /// <summary>
    /// Lists the playlists and lets the user play, add, rename, remove and update them.
    /// </summary>
    public sealed class PlaylistDialog : Form
    {
        private const string AllPlaylistsTag = "\0all";

        private readonly PlaylistManager _playlists;
        private readonly ListView _listView;
        private readonly Label _statusLabel;
        private readonly Button _playButton;
        private readonly Button _addButton;
        private readonly Button _renameButton;
        private readonly Button _removeButton;
        private readonly Button _updateButton;
        private readonly Button _updateAllButton;
        private bool _refreshing;
        private bool _busy;

        public PlaylistDialog(PlaylistManager playlists)
        {
            _playlists = playlists;

            Text = "Playlists";
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            ShowInTaskbar = false;
            Size = new Size(820, 460);
            MinimumSize = new Size(600, 300);
            KeyPreview = true;

            _listView = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                MultiSelect = false,
                HideSelection = false,
                // The check box of each row (in the first column) is the playlist's "random order" setting:
                // checked = random, unchecked = one video after the other in playlist order.
                CheckBoxes = true
            };
            _listView.Columns.Add("Random order", 100);
            _listView.Columns.Add("Playlist", 240);
            _listView.Columns.Add("Videos", 70, HorizontalAlignment.Right);
            _listView.Columns.Add("Last updated", 130);
            _listView.Columns.Add("Status", 200);

            _playButton = CreateButton("Play", PlaySelectedAsync);
            _addButton = CreateButton("Add...", AddAsync);
            _renameButton = CreateButton("Rename...", () => { RenameSelected(); return Task.CompletedTask; });
            _removeButton = CreateButton("Remove", () => { RemoveSelected(); return Task.CompletedTask; });
            _updateButton = CreateButton("Update", UpdateSelectedAsync);
            _updateAllButton = CreateButton("Update all", UpdateAllAsync);
            var closeButton = NewButton("Close");
            closeButton.DialogResult = DialogResult.Cancel;

            // The panel grows with its buttons, which in turn grow with the font, so text is never clipped.
            var buttonPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Right,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(8, 0, 0, 0)
            };
            buttonPanel.Controls.AddRange(new Control[]
            {
                _playButton, _addButton, _renameButton, _removeButton, _updateButton, _updateAllButton, closeButton
            });

            _statusLabel = new Label
            {
                Dock = DockStyle.Bottom,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = SystemColors.GrayText,
                Text = "Double-click a playlist to play it."
            };
            UpdateStatusHeight();
            _statusLabel.FontChanged += (_, __) => UpdateStatusHeight();

            var content = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10) };
            content.Controls.Add(_listView);
            content.Controls.Add(buttonPanel);
            content.Controls.Add(_statusLabel);
            Controls.Add(content);
            CancelButton = closeButton;

            _listView.SelectedIndexChanged += (_, __) => UpdateButtons();
            _listView.ItemChecked += OnItemChecked;
            _listView.MouseDoubleClick += (_, e) =>
            {
                // Two quick clicks on a check box must not also start the playlist.
                if (_listView.HitTest(e.Location).Location != ListViewHitTestLocations.StateImage)
                    RunAction(PlaySelectedAsync);
            };
            KeyDown += OnKeyDown;

            // The ListView drops a selection made before its window exists, so select the active playlist again.
            Load += (_, __) =>
            {
                RefreshList();
                EnsureButtonsFit(buttonPanel, content);
            };

            _playlists.PlaylistsChanged += OnPlaylistsChanged;
            _playlists.PoolChanged += OnPlaylistsChanged;
            FormClosed += (_, __) =>
            {
                _playlists.PlaylistsChanged -= OnPlaylistsChanged;
                _playlists.PoolChanged -= OnPlaylistsChanged;
            };

            RefreshList();
        }

        /// <summary>
        /// Makes the window tall enough for the whole button column, which grows with the font
        /// (a scaled display or a large system font would otherwise push the last button out of view).
        /// </summary>
        private void EnsureButtonsFit(Control buttonPanel, Control content)
        {
            var needed = buttonPanel.PreferredSize.Height + content.Padding.Vertical + _statusLabel.Height;
            if (ClientSize.Height < needed)
                ClientSize = new Size(ClientSize.Width, needed);
        }

        private Button CreateButton(string text, Func<Task> action)
        {
            var button = NewButton(text);
            button.Click += (_, __) => RunAction(action);
            return button;
        }

        /// <summary>
        /// A button that is at least 110 wide and comfortably tall, and grows when the font (e.g. on a
        /// scaled display) needs more room. Fixed pixel sizes clipped the text at larger font sizes.
        /// </summary>
        private static Button NewButton(string text) => new()
        {
            Text = text,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(110, 30),
            Padding = new Padding(6, 3, 6, 3)
        };

        /// <summary>Room for two lines of the current font (long error messages wrap).</summary>
        private void UpdateStatusHeight() => _statusLabel.Height = Math.Max(28, _statusLabel.Font.Height * 2 + 8);

        private void OnKeyDown(object? sender, KeyEventArgs e)
        {
            if (_busy)
                return;

            if (e.KeyCode == Keys.F2 && _renameButton.Enabled)
            {
                RenameSelected();
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Delete && _removeButton.Enabled)
            {
                RemoveSelected();
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Enter && _playButton.Enabled)
            {
                RunAction(PlaySelectedAsync);
                e.Handled = true;
            }
        }

        private void OnPlaylistsChanged(object? sender, EventArgs e)
        {
            if (!IsDisposed)
                RefreshList();
        }

        /// <summary>
        /// The tag of the selected row: <see cref="AllPlaylistsTag"/>, a playlist key, or <c>null</c> if nothing is selected.
        /// </summary>
        private string? SelectedTag => _listView.SelectedItems.Count > 0 ? (string)_listView.SelectedItems[0].Tag! : null;

        private string? SelectedPlaylistKey => SelectedTag is { } tag && tag != AllPlaylistsTag ? tag : null;

        private PlaylistInfo? SelectedPlaylist =>
            SelectedPlaylistKey is { } key ? _playlists.Playlists.FirstOrDefault(p => p.Key == key) : null;

        private void RefreshList()
        {
            // Filling the list also sets the check boxes; that must not be taken for a click.
            _refreshing = true;
            try
            {
                FillList();
            }
            finally
            {
                _refreshing = false;
            }

            UpdateButtons();
        }

        private void FillList()
        {
            var selectedTag = SelectedTag ?? _playlists.SelectedKey ?? AllPlaylistsTag;

            _listView.BeginUpdate();
            _listView.Items.Clear();

            var allLoaded = _playlists.Playlists.Count(p => p.State == PlaylistLoadState.Loaded);
            // All playlists together always play in random order, so this box is always checked.
            var allItem = new ListViewItem(string.Empty) { Tag = AllPlaylistsTag, Checked = true };
            allItem.SubItems.Add(FormatTitle("All playlists", _playlists.SelectedKey == null));
            allItem.SubItems.Add(_playlists.SelectedKey == null ? _playlists.PoolCount.ToString() : string.Empty);
            allItem.SubItems.Add(string.Empty);
            allItem.SubItems.Add(_playlists.IsLoading ? $"{allLoaded}/{_playlists.Playlists.Count} loaded..." : string.Empty);
            _listView.Items.Add(allItem);

            foreach (var playlist in _playlists.Playlists)
            {
                var isActive = playlist.Key == _playlists.SelectedKey;
                var item = new ListViewItem(string.Empty) { Tag = playlist.Key, Checked = playlist.Shuffle };
                item.SubItems.Add(FormatTitle(playlist.Title, isActive));
                item.SubItems.Add(playlist.State == PlaylistLoadState.Loaded ? playlist.VideoIds.Count.ToString() : string.Empty);
                item.SubItems.Add(playlist.FetchedAtUtc?.ToLocalTime().ToString("g") ?? string.Empty);
                item.SubItems.Add(FormatState(playlist));
                if (playlist.State == PlaylistLoadState.Failed)
                    item.ForeColor = Color.Firebrick;
                _listView.Items.Add(item);
            }

            foreach (ListViewItem item in _listView.Items)
            {
                if ((string)item.Tag! == selectedTag)
                {
                    item.Selected = true;
                    item.EnsureVisible();
                    break;
                }
            }

            _listView.EndUpdate();
        }

        /// <summary>A check box in the list was clicked: save the new order of that playlist.</summary>
        private void OnItemChecked(object? sender, ItemCheckedEventArgs e)
        {
            if (_refreshing)
                return;

            var item = e.Item;
            var tag = (string)item.Tag!;
            var shuffle = item.Checked;

            // The list also reports the items it inserts (e.g. when its window is created), not only clicks.
            // Only a box that differs from the saved order is a change; "All playlists" must stay checked.
            if (tag == AllPlaylistsTag)
            {
                if (shuffle)
                    return;
            }
            else if (_playlists.Playlists.FirstOrDefault(p => p.Key == tag) is not { } playlist || playlist.Shuffle == shuffle)
            {
                return;
            }

            // The change is applied after this notification: saving refreshes (rebuilds) the list.
            void Apply()
            {
                // While its window is created the list reports an item first as unchecked and then as
                // checked; only a state that is still there after the notification is a click.
                if (IsDisposed || item.Checked != shuffle)
                    return;

                // "All playlists" cannot be changed, and nothing may change while an action runs.
                if (tag == AllPlaylistsTag || _busy)
                {
                    RefreshList();
                    return;
                }

                RunAction(() =>
                {
                    try
                    {
                        _playlists.SetShuffle(tag, shuffle);
                    }
                    catch
                    {
                        RefreshList(); // show the value that is really saved
                        throw;
                    }

                    return Task.CompletedTask;
                });
            }

            if (IsHandleCreated)
                BeginInvoke(Apply);
            else
                Apply();
        }

        private static string FormatTitle(string title, bool isActive) => isActive ? "▶ " + title : "    " + title;

        private static string FormatState(PlaylistInfo playlist) => playlist.State switch
        {
            PlaylistLoadState.NotLoaded => "Waiting...",
            PlaylistLoadState.Loading => "Loading...",
            PlaylistLoadState.Failed => "Failed: " + playlist.Error,
            _ when playlist.Error != null => "Last update failed: " + playlist.Error,
            _ => string.Empty
        };

        private void UpdateButtons()
        {
            var tag = SelectedTag;
            var playlist = SelectedPlaylist;

            _playButton.Enabled = !_busy && tag != null;
            _addButton.Enabled = !_busy;
            _renameButton.Enabled = !_busy && playlist != null;
            _removeButton.Enabled = !_busy && playlist != null;
            _updateButton.Enabled = !_busy && playlist != null && playlist.State != PlaylistLoadState.Loading;
            _updateAllButton.Enabled = !_busy && _playlists.Playlists.Count > 0;
        }

        private async void RunAction(Func<Task> action)
        {
            if (_busy)
                return;

            _busy = true;
            UpdateButtons();
            try
            {
                await action();
            }
            catch (Exception ex)
            {
                Logger.Log("Playlist dialog action failed.", ex);
                if (!IsDisposed)
                {
                    SetStatus(ex.Message, isError: true);
                    MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            finally
            {
                _busy = false;
                if (!IsDisposed)
                    UpdateButtons();
            }
        }

        private async Task PlaySelectedAsync()
        {
            var tag = SelectedTag;
            if (tag == null)
                return;

            var playlist = SelectedPlaylist;
            if (playlist != null && playlist.State != PlaylistLoadState.Loaded)
                SetStatus($"Loading \"{playlist.Title}\"...", isError: false);

            await _playlists.SelectAsync(playlist?.Key);

            if (!IsDisposed)
                Close();
        }

        private async Task AddAsync()
        {
            string title;
            string url;
            using (var dialog = new AddPlaylistDialog(_playlists))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK)
                    return;

                title = dialog.PlaylistTitle;
                url = dialog.PlaylistUrl;
            }

            SetStatus($"Downloading the video list of \"{title}\"...", isError: false);
            var playlist = await _playlists.AddAsync(title, url);
            SetStatus($"Added \"{playlist.Title}\" with {playlist.VideoIds.Count} videos.", isError: false);
        }

        private void RenameSelected()
        {
            var playlist = SelectedPlaylist;
            if (playlist == null)
                return;

            var newTitle = TextPromptDialog.Show(this, "Rename playlist", "New name:", playlist.Title);
            if (string.IsNullOrWhiteSpace(newTitle) || newTitle == playlist.Title)
                return;

            _playlists.Rename(playlist.Key, newTitle);
            SetStatus($"Renamed to \"{newTitle}\".", isError: false);
        }

        private void RemoveSelected()
        {
            var playlist = SelectedPlaylist;
            if (playlist == null)
                return;

            var answer = MessageBox.Show(
                this,
                $"Remove \"{playlist.Title}\" from the list?",
                "Remove playlist",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);
            if (answer != DialogResult.Yes)
                return;

            _playlists.Remove(playlist.Key);
            SetStatus($"Removed \"{playlist.Title}\".", isError: false);
        }

        private async Task UpdateSelectedAsync()
        {
            var playlist = SelectedPlaylist;
            if (playlist == null)
                return;

            SetStatus($"Downloading the video list of \"{playlist.Title}\" again...", isError: false);
            var result = await _playlists.UpdateAsync(playlist.Key);
            SetStatus(FormatResult(result), isError: false);
        }

        private async Task UpdateAllAsync()
        {
            var results = new List<PlaylistUpdateResult>();
            var failures = new List<string>();
            var playlists = _playlists.Playlists.ToList();

            for (var i = 0; i < playlists.Count; i++)
            {
                var playlist = playlists[i];
                if (!_playlists.Contains(playlist.Key))
                    continue;

                SetStatus($"Updating {i + 1}/{playlists.Count}: \"{playlist.Title}\"...", isError: false);
                try
                {
                    results.Add(await _playlists.UpdateAsync(playlist.Key));
                }
                catch (Exception ex)
                {
                    Logger.Log($"Updating playlist '{playlist.Title}' failed.", ex);
                    failures.Add($"{playlist.Title}: {ex.Message}");
                }
            }

            var summary = $"Updated {results.Count} playlist(s): +{results.Sum(r => r.Added)} / −{results.Sum(r => r.Removed)} videos.";
            if (failures.Count == 0)
            {
                SetStatus(summary, isError: false);
                return;
            }

            SetStatus($"{summary} {failures.Count} failed.", isError: true);
            if (!IsDisposed)
            {
                MessageBox.Show(
                    this,
                    "These playlists could not be updated; their previous video lists are still used:\n\n" + string.Join("\n", failures),
                    Text,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }

        private static string FormatResult(PlaylistUpdateResult result) =>
            $"Updated \"{result.Title}\": +{result.Added} / −{result.Removed} videos ({result.Total} in total).";

        private void SetStatus(string text, bool isError)
        {
            if (IsDisposed)
                return;

            _statusLabel.Text = text;
            _statusLabel.ForeColor = isError ? Color.Firebrick : SystemColors.GrayText;
        }
    }
}
