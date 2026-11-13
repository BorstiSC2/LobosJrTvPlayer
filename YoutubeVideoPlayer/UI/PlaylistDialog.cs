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
        private readonly CheckBox _shuffleCheckBox;
        private bool _updatingShuffleCheckBox;
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
                HideSelection = false
            };
            _listView.Columns.Add("Playlist", 270);
            _listView.Columns.Add("Videos", 70, HorizontalAlignment.Right);
            _listView.Columns.Add("Order", 80);
            _listView.Columns.Add("Last updated", 130);
            _listView.Columns.Add("Status", 220);

            _playButton = CreateButton("Play", PlaySelectedAsync);
            _addButton = CreateButton("Add...", AddAsync);
            _renameButton = CreateButton("Rename...", () => { RenameSelected(); return Task.CompletedTask; });
            _removeButton = CreateButton("Remove", () => { RemoveSelected(); return Task.CompletedTask; });
            _updateButton = CreateButton("Update", UpdateSelectedAsync);
            _updateAllButton = CreateButton("Update all", UpdateAllAsync);
            // Saved with the playlist; off = the videos play one after the other in playlist order.
            _shuffleCheckBox = new CheckBox
            {
                Text = "Random order",
                AutoSize = true,
                Margin = new Padding(6, 10, 3, 10)
            };
            _shuffleCheckBox.CheckedChanged += (_, __) =>
            {
                if (_updatingShuffleCheckBox || SelectedPlaylistKey is not { } key)
                    return;

                var shuffle = _shuffleCheckBox.Checked;
                RunAction(() =>
                {
                    _playlists.SetShuffle(key, shuffle);
                    return Task.CompletedTask;
                });
            };

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
                _playButton, _addButton, _renameButton, _removeButton, _updateButton, _updateAllButton, _shuffleCheckBox, closeButton
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
            _listView.DoubleClick += (_, __) => RunAction(PlaySelectedAsync);
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
            var selectedTag = SelectedTag ?? _playlists.SelectedKey ?? AllPlaylistsTag;

            _listView.BeginUpdate();
            _listView.Items.Clear();

            var allLoaded = _playlists.Playlists.Count(p => p.State == PlaylistLoadState.Loaded);
            var allItem = new ListViewItem(FormatTitle("All playlists", _playlists.SelectedKey == null)) { Tag = AllPlaylistsTag };
            allItem.SubItems.Add(_playlists.SelectedKey == null ? _playlists.PoolCount.ToString() : string.Empty);
            allItem.SubItems.Add(FormatOrder(shuffle: true)); // all playlists together always play in random order
            allItem.SubItems.Add(string.Empty);
            allItem.SubItems.Add(_playlists.IsLoading ? $"{allLoaded}/{_playlists.Playlists.Count} loaded..." : string.Empty);
            _listView.Items.Add(allItem);

            foreach (var playlist in _playlists.Playlists)
            {
                var isActive = playlist.Key == _playlists.SelectedKey;
                var item = new ListViewItem(FormatTitle(playlist.Title, isActive)) { Tag = playlist.Key };
                item.SubItems.Add(playlist.State == PlaylistLoadState.Loaded ? playlist.VideoIds.Count.ToString() : string.Empty);
                item.SubItems.Add(FormatOrder(playlist.Shuffle));
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
            UpdateButtons();
        }

        private static string FormatTitle(string title, bool isActive) => isActive ? "▶ " + title : "    " + title;

        private static string FormatOrder(bool shuffle) => shuffle ? "Random" : "In order";

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

            // The box shows the order of the selected playlist. "All playlists" always plays in random
            // order, which cannot be changed, so it is shown checked but disabled.
            _updatingShuffleCheckBox = true;
            try
            {
                _shuffleCheckBox.Checked = playlist?.Shuffle ?? tag != null;
            }
            finally
            {
                _updatingShuffleCheckBox = false;
            }

            _shuffleCheckBox.Enabled = !_busy && playlist != null;
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
