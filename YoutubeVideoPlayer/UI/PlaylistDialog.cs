using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using YouTubeVideoPlayer.Infrastructure;
using YouTubeVideoPlayer.Playlists;

namespace YouTubeVideoPlayer.UI
{
    /// <summary>
    /// Lists the playlists and lets the user play, add, rename, remove and update them. Below the list, the automatic
    /// swap to the next playlist is switched on and off and its interval is set (in days and hours).
    /// </summary>
    public sealed class PlaylistDialog : Form
    {
        private const string AllPlaylistsTag = "\0all";

        // The status column only shows these symbols; the reason for a failure goes to the error log.
        internal const string LoadingIcon = "⟳";
        internal const string SuccessIcon = "✔";
        internal const string ErrorIcon = "✖";

        private static readonly Color SurfaceColor = Color.FromArgb(243, 243, 243);
        private static readonly Color BorderColor = Color.FromArgb(208, 208, 208);
        private static readonly Color AccentColor = Color.SeaGreen;

        private readonly PlaylistManager _playlists;
        private readonly ListView _listView;
        private readonly Label _statusLabel;
        private readonly Button _playButton;
        private readonly Button _addButton;
        private readonly Button _renameButton;
        private readonly Button _removeButton;
        private readonly Button _updateButton;
        private readonly Button _updateAllButton;
        private readonly ToggleSwitch _autoSwapSwitch;
        private readonly NumericUpDown _swapDays;
        private readonly NumericUpDown _swapHours;
        private readonly Label _swapInfoLabel;
        // Set while the swap controls are filled from the saved setting, which must not be taken for an edit.
        private bool _updatingSwap;
        // Playlists that an update (of several) has not finished yet; they all show the loading symbol from the start.
        private readonly HashSet<string> _pending = new();
        private bool _refreshing;
        private bool _busy;

        public PlaylistDialog(PlaylistManager playlists)
        {
            _playlists = playlists;

            Text = "Playlists";
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            ShowInTaskbar = false;
            // Estimated once (about 7 px per character at 100 %), not measured at runtime: all five columns plus
            // the button column fit without resizing the window (columns 815 + scroll bar 20 + buttons 162 + margins 48).
            // The height leaves room for the automatic swap below the list.
            Size = new Size(1060, 530);
            MinimumSize = new Size(600, 380);
            KeyPreview = true;
            BackColor = SurfaceColor;
            if (AppIcon.Instance is { } icon)
                Icon = icon;

            _listView = new BufferedListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                // Shift+click selects a range, Ctrl+click adds single rows; several rows can be removed or updated at once.
                MultiSelect = true,
                HideSelection = false,
                BorderStyle = BorderStyle.None,
                BackColor = Color.White,
                // The check box of each row (in the first column) is the playlist's "random order" setting:
                // checked = random, unchecked = one video after the other in playlist order.
                CheckBoxes = true,
                // A 1 x 30 image list is the usual way to get taller, airier rows.
                SmallImageList = new ImageList { ImageSize = new Size(1, 30) }
            };
            // The Explorer visual style gives the list the modern flat header and soft selection of Windows 10/11.
            _listView.HandleCreated += (_, __) => SetWindowTheme(_listView.Handle, "Explorer", null);
            // The playlist title varies most in length, so it is the last column. Widths are estimates (see the
            // window size): the header "Random order" needs about 120, a title (with the "▶ " marker) up to
            // about 60 characters = 420. The status is a single symbol.
            _listView.Columns.Add("Random order", 125);
            _listView.Columns.Add("Videos", 70, HorizontalAlignment.Right);
            _listView.Columns.Add("Last updated", 130);
            _listView.Columns.Add("Status", 70, HorizontalAlignment.Center);
            _listView.Columns.Add("Playlist", 420);

            _playButton = CreateButton("Switch to playlist", PlaySelectedAsync);
            StylePrimaryButton(_playButton);
            _playButton.Margin = new Padding(0, 20, 0, 8); // extra space above: it is not one of the list actions
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
                Padding = new Padding(12, 0, 0, 0)
            };
            buttonPanel.Controls.AddRange(new Control[]
            {
                _addButton, _renameButton, _removeButton, _updateButton, _updateAllButton, _playButton, closeButton
            });

            _statusLabel = new Label
            {
                Name = "status",
                Dock = DockStyle.Bottom,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = SystemColors.GrayText,
                Text = "Select a playlist and click \"Switch to playlist\" (or press Enter) to play it."
            };
            UpdateStatusHeight();
            _statusLabel.FontChanged += (_, __) => UpdateStatusHeight();

            // The automatic swap, below the list (the button column stays to the right of both):
            // [switch] Swap playlists automatically every [7] days [0] hours, and a line that says when and to which playlist.
            _autoSwapSwitch = new ToggleSwitch
            {
                Text = "Swap playlists automatically every",
                Anchor = AnchorStyles.Left, // centered in the row of the flow panel
                Margin = new Padding(0, 0, 6, 0)
            };
            _swapDays = NewNumberBox(AutoSwapSettings.MaxIntervalHours / 24);
            _swapHours = NewNumberBox(23);
            var swapRow = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Margin = Padding.Empty
            };
            swapRow.Controls.AddRange(new Control[] { _autoSwapSwitch, _swapDays, NewInlineLabel("days"), _swapHours, NewInlineLabel("hours") });

            _swapInfoLabel = new Label
            {
                Name = "autoSwapInfo",
                AutoSize = true,
                ForeColor = SystemColors.GrayText,
                Margin = new Padding(0, 4, 0, 0)
            };
            var swapPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(0, 10, 0, 0)
            };
            swapPanel.Controls.Add(swapRow);
            swapPanel.Controls.Add(_swapInfoLabel);
            // A long line wraps instead of being cut off.
            swapPanel.SizeChanged += (_, __) => _swapInfoLabel.MaximumSize = new Size(Math.Max(100, swapPanel.ClientSize.Width), 0);

            // Docked from the last one added: the status line at the bottom, the buttons to the right, the swap below the list.
            var content = new Panel { Dock = DockStyle.Fill, Padding = new Padding(16, 16, 16, 8) };
            content.Controls.Add(_listView);
            content.Controls.Add(swapPanel);
            content.Controls.Add(buttonPanel);
            content.Controls.Add(_statusLabel);
            Controls.Add(content);
            CancelButton = closeButton;

            _listView.SelectedIndexChanged += (_, __) => UpdateButtons();
            _listView.ItemChecked += OnItemChecked;
            _autoSwapSwitch.CheckedChanged += (_, __) => ApplyAutoSwap();
            _swapDays.ValueChanged += (_, __) => ApplyAutoSwap();
            _swapHours.ValueChanged += (_, __) => ApplyAutoSwap();
            // No double-click handler on purpose: the ListView itself toggles the check box ("random order")
            // on a double-click, and playing a playlist is what the highlighted button and Enter are for.
            KeyDown += OnKeyDown;

            // The ListView drops a selection made before its window exists, so select the active playlist again.
            Load += (_, __) =>
            {
                RefreshList();
                SizeNumberBoxes();
                EnsureButtonsFit(buttonPanel, content);
            };
            FontChanged += (_, __) => SizeNumberBoxes();

            _playlists.PlaylistsChanged += OnPlaylistsChanged;
            _playlists.PoolChanged += OnPlaylistsChanged;
            _playlists.AutoSwapChanged += OnAutoSwapChanged;
            FormClosed += (_, __) =>
            {
                _listView.SmallImageList?.Dispose();
                _playlists.PlaylistsChanged -= OnPlaylistsChanged;
                _playlists.PoolChanged -= OnPlaylistsChanged;
                _playlists.AutoSwapChanged -= OnAutoSwapChanged;
            };

            RefreshList();
        }

        /// <summary>
        /// Makes the window tall enough for the whole button column, which grows with the font
        /// (a scaled display or a large system font would otherwise push the last button out of view).
        /// </summary>
        private void EnsureButtonsFit(Control buttonPanel, Control content)
        {
            // All buttons as wide as the widest one (the bold "Switch to playlist" is the widest by default).
            var buttons = buttonPanel.Controls.OfType<Button>().ToList();
            var width = buttons.Max(b => b.GetPreferredSize(Size.Empty).Width);
            foreach (var button in buttons)
                button.MinimumSize = new Size(width, button.MinimumSize.Height);

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
        private static Button NewButton(string text)
        {
            // Flat white buttons with a thin border and a hover tint, in the style of current Windows dialogs.
            var button = new Button
            {
                Text = text,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                MinimumSize = new Size(110, 34),
                Padding = new Padding(6, 3, 6, 3),
                Margin = new Padding(0, 0, 0, 8),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.White,
                UseVisualStyleBackColor = false,
                Cursor = Cursors.Hand
            };
            button.FlatAppearance.BorderColor = BorderColor;
            button.FlatAppearance.MouseOverBackColor = Color.FromArgb(235, 235, 235);
            button.FlatAppearance.MouseDownBackColor = Color.FromArgb(222, 222, 222);
            return button;
        }

        /// <summary>Makes the button stand out (green, bold white text); a disabled one fades to a neutral grey.</summary>
        private static void StylePrimaryButton(Button button)
        {
            void Apply()
            {
                button.BackColor = button.Enabled ? AccentColor : Color.FromArgb(235, 235, 235);
                button.ForeColor = button.Enabled ? Color.White : SystemColors.GrayText;
                button.FlatAppearance.BorderColor = button.Enabled ? AccentColor : BorderColor;
                button.Cursor = button.Enabled ? Cursors.Hand : Cursors.Default;
            }

            button.FlatAppearance.MouseOverBackColor = Color.FromArgb(38, 115, 72);
            button.FlatAppearance.MouseDownBackColor = Color.FromArgb(30, 95, 60);
            var bold = new Font(button.Font, FontStyle.Bold);
            button.Font = bold;
            button.Disposed += (_, __) => bold.Dispose();
            button.EnabledChanged += (_, __) => Apply();
            Apply();
        }

        /// <summary>A number box for the swap interval, sized for its largest value by <see cref="SizeNumberBoxes"/>.</summary>
        private static NumericUpDown NewNumberBox(int maximum) => new()
        {
            Minimum = 0,
            Maximum = maximum,
            TextAlign = HorizontalAlignment.Right,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 0, 4, 0)
        };

        private static Label NewInlineLabel(string text) => new()
        {
            Text = text,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 0, 12, 0)
        };

        /// <summary>The number boxes are as wide as their largest value needs in the current font, plus the arrows.</summary>
        private void SizeNumberBoxes()
        {
            foreach (var box in new[] { _swapDays, _swapHours })
            {
                var digits = new string('0', box.Maximum.ToString().Length + 1);
                box.Width = TextRenderer.MeasureText(digits, box.Font).Width + SystemInformation.VerticalScrollBarWidth + 8;
            }
        }

        /// <summary>A list view that paints without flicker (important with the Explorer theme and taller rows).</summary>
        private sealed class BufferedListView : ListView
        {
            public BufferedListView() => DoubleBuffered = true;
        }

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        private static extern int SetWindowTheme(IntPtr hWnd, string pszSubAppName, string? pszSubIdList);

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

        private void OnAutoSwapChanged(object? sender, EventArgs e)
        {
            if (!IsDisposed)
                UpdateAutoSwapControls();
        }

        /// <summary>
        /// The tag of the selected row: <see cref="AllPlaylistsTag"/>, a playlist key, or <c>null</c> if nothing is selected.
        /// </summary>
        private string? SelectedTag => _listView.SelectedItems.Count > 0 ? (string)_listView.SelectedItems[0].Tag! : null;

        private IReadOnlyList<string> SelectedTags =>
            _listView.SelectedItems.Cast<ListViewItem>().Select(item => (string)item.Tag!).ToList();

        /// <summary>The selected playlists; the "All playlists" row is not one.</summary>
        private IReadOnlyList<PlaylistInfo> SelectedPlaylists =>
            SelectedTags.Where(tag => tag != AllPlaylistsTag)
                .Select(tag => _playlists.Playlists.FirstOrDefault(p => p.Key == tag))
                .OfType<PlaylistInfo>()
                .ToList();

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
            UpdateAutoSwapControls(); // the number of playlists and the next playlist may have changed
        }

        private void FillList()
        {
            var selectedTags = SelectedTags.ToHashSet();
            if (selectedTags.Count == 0)
                selectedTags.Add(_playlists.SelectedKey ?? AllPlaylistsTag);

            _listView.BeginUpdate();
            _listView.Items.Clear();

            // All playlists together always play in random order, so this box is always checked.
            var allItem = new ListViewItem(string.Empty) { Tag = AllPlaylistsTag, Checked = true };
            allItem.SubItems.Add(_playlists.SelectedKey == null ? _playlists.PoolCount.ToString() : string.Empty);
            allItem.SubItems.Add(string.Empty);
            allItem.SubItems.Add(_playlists.IsLoading || _pending.Count > 0 ? LoadingIcon : string.Empty);
            allItem.SubItems.Add(FormatTitle("All playlists", _playlists.SelectedKey == null));
            _listView.Items.Add(allItem);

            foreach (var playlist in _playlists.Playlists)
            {
                var isActive = playlist.Key == _playlists.SelectedKey;
                var item = new ListViewItem(string.Empty) { Tag = playlist.Key, Checked = playlist.Shuffle };
                item.SubItems.Add(playlist.State == PlaylistLoadState.Loaded ? playlist.VideoIds.Count.ToString() : string.Empty);
                item.SubItems.Add(playlist.FetchedAtUtc?.ToLocalTime().ToString("g") ?? string.Empty);
                item.SubItems.Add(FormatState(playlist));
                item.SubItems.Add(FormatTitle(playlist.Title, isActive));
                if (playlist.State == PlaylistLoadState.Failed)
                    item.ForeColor = Color.Firebrick;
                _listView.Items.Add(item);
            }

            var scrolled = false;
            foreach (ListViewItem item in _listView.Items)
            {
                if (!selectedTags.Contains((string)item.Tag!))
                    continue;

                item.Selected = true;
                if (!scrolled)
                {
                    item.EnsureVisible();
                    scrolled = true;
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

        /// <summary>A symbol only: waiting/loading, loaded, or failed (also a failed update of a loaded playlist).</summary>
        private string FormatState(PlaylistInfo playlist) => _pending.Contains(playlist.Key) ? LoadingIcon : playlist.State switch
        {
            PlaylistLoadState.NotLoaded or PlaylistLoadState.Loading => LoadingIcon,
            PlaylistLoadState.Failed => ErrorIcon,
            _ when playlist.Error != null => ErrorIcon,
            _ => SuccessIcon
        };

        private void UpdateButtons()
        {
            var rows = _listView.SelectedItems.Count;
            var playlists = SelectedPlaylists;

            // With several rows selected only remove, update and update all make sense.
            _playButton.Enabled = !_busy && rows == 1;
            _addButton.Enabled = !_busy && rows <= 1;
            _renameButton.Enabled = !_busy && rows == 1 && playlists.Count == 1;
            _removeButton.Enabled = !_busy && playlists.Count > 0;
            _updateButton.Enabled = !_busy && playlists.Any(p => p.State != PlaylistLoadState.Loading);
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
                ReportError(ex);
            }
            finally
            {
                _busy = false;
                if (!IsDisposed)
                    UpdateButtons();
            }
        }

        private void ReportError(Exception ex)
        {
            Logger.Log("Playlist dialog action failed.", ex);
            if (IsDisposed)
                return;

            SetStatus(ex.Message, isError: true);
            MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        #region Automatic swap

        /// <summary>
        /// The switch or the interval was changed: save it (which starts the interval over). Saving is quick and touches
        /// nothing an action in progress uses, so this also works while an update runs.
        /// </summary>
        private void ApplyAutoSwap()
        {
            if (_updatingSwap || IsDisposed)
                return;

            // An hour is the shortest interval; setting it applies it through ValueChanged.
            if (_swapDays.Value == 0 && _swapHours.Value == 0)
            {
                _swapHours.Value = 1;
                return;
            }

            var interval = TimeSpan.FromDays((double)_swapDays.Value) + TimeSpan.FromHours((double)_swapHours.Value);
            try
            {
                _playlists.SetAutoSwap(_autoSwapSwitch.Checked, interval);
            }
            catch (Exception ex)
            {
                UpdateAutoSwapControls(); // show the setting that is really saved
                ReportError(ex);
            }
        }

        /// <summary>Shows the saved setting; the switch can only be turned on with two playlists or more.</summary>
        private void UpdateAutoSwapControls()
        {
            _updatingSwap = true;
            try
            {
                var available = _playlists.CanAutoSwap;
                _autoSwapSwitch.Enabled = available;
                _swapDays.Enabled = available;
                _swapHours.Enabled = available;

                if (_autoSwapSwitch.Checked != _playlists.AutoSwapEnabled)
                    _autoSwapSwitch.Checked = _playlists.AutoSwapEnabled;

                // Only set when different, so a number that is being typed is not replaced by a refresh.
                var interval = _playlists.AutoSwapInterval;
                var days = (decimal)(int)interval.TotalDays;
                var hours = (decimal)interval.Hours;
                if (_swapDays.Value != days)
                    _swapDays.Value = days;
                if (_swapHours.Value != hours)
                    _swapHours.Value = hours;
            }
            finally
            {
                _updatingSwap = false;
            }

            var (text, isWarning) = DescribeAutoSwap();
            _swapInfoLabel.Text = text;
            _swapInfoLabel.ForeColor = isWarning ? Color.Firebrick : SystemColors.GrayText;
        }

        /// <summary>The line below the switch: when and to which playlist the next swap goes.</summary>
        private (string Text, bool IsWarning) DescribeAutoSwap()
        {
            if (!_playlists.CanAutoSwap)
                return ("Add a second playlist to swap playlists automatically.", false);

            if (!_playlists.AutoSwapEnabled)
                return ("Off: the playlist only changes when you switch it.", false);

            var next = _playlists.GetNextPlaylist();
            if (next == null)
                return ("No other playlist can be played right now, so the playlist is not swapped.", true);

            var since = _playlists.LastSwapUtc is { } last ? $" (counted from the last change, {last.LocalDateTime:g})" : string.Empty;
            if (_playlists.IsSwapDue)
                return ($"Swap due: \"{next.Title}\" follows the current video{since}.", false);

            var due = _playlists.NextSwapUtc!.Value.LocalDateTime;
            return ($"Next: \"{next.Title}\" on {due:g}, after the video that is playing then{since}.", false);
        }

        #endregion

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
            var selected = SelectedPlaylists;
            if (selected.Count == 0)
                return;

            var question = selected.Count == 1
                ? $"Remove \"{selected[0].Title}\" from the list?"
                : $"Remove these {selected.Count} playlists from the list?\n\n" + string.Join("\n", selected.Select(p => p.Title));
            var answer = MessageBox.Show(
                this,
                question,
                selected.Count == 1 ? "Remove playlist" : "Remove playlists",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);
            if (answer != DialogResult.Yes)
                return;

            foreach (var playlist in selected)
                _playlists.Remove(playlist.Key);

            SetStatus(
                selected.Count == 1 ? $"Removed \"{selected[0].Title}\"." : $"Removed {selected.Count} playlists.",
                isError: false);
        }

        private async Task UpdateSelectedAsync()
        {
            var selected = SelectedPlaylists;
            if (selected.Count > 1 || _listView.SelectedItems.Count > 1)
            {
                await UpdatePlaylistsAsync(selected);
                return;
            }

            var playlist = SelectedPlaylist;
            if (playlist == null)
                return;

            SetStatus($"Downloading the video list of \"{playlist.Title}\" again...", isError: false);
            var result = await _playlists.UpdateAsync(playlist.Key);
            SetStatus(FormatResult(result), isError: false);
        }

        private Task UpdateAllAsync() => UpdatePlaylistsAsync(_playlists.Playlists.ToList());

        /// <summary>
        /// Updates the playlists one after another. All of them show the loading symbol from the start, not only
        /// the one that is being downloaded, and get their final symbol as soon as their own update is done.
        /// </summary>
        private async Task UpdatePlaylistsAsync(IReadOnlyList<PlaylistInfo> playlists)
        {
            var results = new List<PlaylistUpdateResult>();
            var failures = new List<string>();

            foreach (var playlist in playlists)
                _pending.Add(playlist.Key);
            RefreshList();

            try
            {
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

                    _pending.Remove(playlist.Key);
                    if (!IsDisposed)
                        RefreshList();
                }
            }
            finally
            {
                _pending.Clear();
                if (!IsDisposed)
                    RefreshList();
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
