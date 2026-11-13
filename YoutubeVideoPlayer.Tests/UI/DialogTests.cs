using System.Drawing;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using YouTubeVideoPlayer.Playlists;
using YouTubeVideoPlayer.Tests.TestSupport;
using YouTubeVideoPlayer.Twitch.Auth;
using YouTubeVideoPlayer.UI;
using YouTubeVideoPlayer.YouTube;
using static YouTubeVideoPlayer.Tests.TestSupport.PlaylistSetup;

namespace YouTubeVideoPlayer.Tests.UI;

public class PlaylistDialogTests
{
    private static async Task<(PlaylistSetup Setup, PlaylistManager Manager)> LoadAsync(PlaylistSetup setup)
    {
        var manager = setup.Create();
        await manager.InitializeAsync();
        await Async.WaitUntilAsync(() => !manager.IsLoading && manager.Playlists.All(p => p.State != PlaylistLoadState.NotLoaded));
        return (setup, manager);
    }

    private static PlaylistSetup ThreePlaylists() => new PlaylistSetup()
        .WithPlaylist(IdA, "Alpha", selected: true).WithCache(IdA, "a1", "a2")
        .WithPlaylist(IdB, "Beta").WithCache(IdB, "b1")
        .WithPlaylist(IdC, "Unreachable");

    private static ListView List(Form dialog) => dialog.All<ListView>().Single();

    private static Label Status(Form dialog) => dialog.All<Label>().Single(l => l.Name == "status");

    private static void SelectRow(Form dialog, int index)
    {
        var list = List(dialog);
        list.SelectedItems.Clear();
        list.Items[index].Selected = true;
    }

    [Fact]
    public void ListsAllPlaylists_WithCountsStatusAndTheActiveOne() => UiContext.Run(async () =>
    {
        var (setup, manager) = await LoadAsync(ThreePlaylists());
        using var _ = setup;
        using var dialog = new PlaylistDialog(manager).ShowHidden(); // the selection needs a window handle

        // Columns: random order (a check box, no text), videos, last updated, status (a symbol), playlist.
        var rows = List(dialog).Items.Cast<ListViewItem>().Select(i => i.SubItems.Cast<ListViewItem.ListViewSubItem>().Select(s => s.Text).ToArray()).ToList();

        Assert.Equal(new[] { "Random order", "Videos", "Last updated", "Status", "Playlist" }, List(dialog).Columns.Cast<ColumnHeader>().Select(c => c.Text));
        Assert.Equal(4, rows.Count);
        Assert.Equal(new[] { "", "", "", "", "    All playlists" }, rows[0]);
        Assert.Equal("▶ Alpha", rows[1][4]);
        Assert.Equal("2", rows[1][1]);
        Assert.Equal(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).ToLocalTime().ToString("g"), rows[1][2]);
        Assert.Equal("    Beta", rows[2][4]);
        // Only symbols in the status column; the reason ("no such playlist") goes to the error log.
        Assert.Equal(PlaylistDialog.SuccessIcon, rows[1][3]);
        Assert.Equal(PlaylistDialog.ErrorIcon, rows[3][3]);
        Assert.DoesNotContain(rows, row => row.Any(text => text.Contains("no such playlist")));
        Assert.Equal(Color.Firebrick, List(dialog).Items[3].ForeColor);
        Assert.True(List(dialog).Items[1].Selected); // the active playlist is preselected
    }, sta: true);

    [Fact]
    public void TheRandomOrderCheckBoxesAreInTheGrid_NotNextToTheButtons() => UiContext.Run(async () =>
    {
        var (setup, manager) = await LoadAsync(ThreePlaylists());
        using var _ = setup;
        using var dialog = new PlaylistDialog(manager).ShowHidden();

        Assert.True(List(dialog).CheckBoxes);
        // No separate check box for the order any more; the only one outside the grid is the automatic swap.
        Assert.IsType<ToggleSwitch>(Assert.Single(dialog.All<CheckBox>()));
        Assert.All(List(dialog).Items.Cast<ListViewItem>(), item => Assert.True(item.Checked)); // all random by default
    }, sta: true);

    [Fact]
    public void ClickingACheckBoxInTheGrid_ChangesAndSavesTheOrderOfThatPlaylist() => UiContext.Run(async () =>
    {
        var (setup, manager) = await LoadAsync(ThreePlaylists());
        using var _ = setup;
        using var dialog = new PlaylistDialog(manager).ShowHidden();

        List(dialog).Items[2].Checked = false; // Beta
        await Async.SettleAsync();

        Assert.False(manager.Playlists[1].Shuffle);
        Assert.True(manager.Playlists[0].Shuffle); // the others are not touched
        var saved = setup.SavedLibrary().Playlists;
        Assert.False(saved[1].Shuffle); // saved with the playlist
        Assert.True(saved[0].Shuffle);
        Assert.False(List(dialog).Items[2].Checked);
        Assert.True(List(dialog).Items[1].Checked);
        Assert.True(List(dialog).Items[1].Selected); // the list was rebuilt, the selection stays

        List(dialog).Items[2].Checked = true;
        await Async.SettleAsync();
        Assert.True(manager.Playlists[1].Shuffle);
        Assert.True(List(dialog).Items[2].Checked);
    }, sta: true);

    [Fact]
    public void TheCheckBoxesShowTheSavedOrder_AndFollowChangesFromElsewhere() => UiContext.Run(async () =>
    {
        var setup = ThreePlaylists();
        setup.Library.Playlists[0].Shuffle = false;
        var (_, manager) = await LoadAsync(setup);
        using var __ = setup;
        using var dialog = new PlaylistDialog(manager).ShowHidden();

        Assert.False(List(dialog).Items[1].Checked); // Alpha plays in order
        Assert.True(List(dialog).Items[2].Checked);

        manager.ToggleShuffle(); // e.g. !toggleshuffle in the chat
        await Async.SettleAsync();

        Assert.True(List(dialog).Items[1].Checked);
    }, sta: true);

    [Fact]
    public void TheAllPlaylistsCheckBox_CannotBeChanged() => UiContext.Run(async () =>
    {
        var (setup, manager) = await LoadAsync(ThreePlaylists());
        using var _ = setup;
        using var dialog = new PlaylistDialog(manager).ShowHidden();

        List(dialog).Items[0].Checked = false; // "All playlists" always plays in random order
        await Async.SettleAsync();

        Assert.True(List(dialog).Items[0].Checked);
        Assert.All(manager.Playlists, p => Assert.True(p.Shuffle));
    }, sta: true);

    [Fact]
    public void WhenSavingTheOrderFails_TheCheckBoxShowsTheOldValueAgain() => UiContext.Run(async () =>
    {
        var (setup, manager) = await LoadAsync(ThreePlaylists());
        using var _ = setup;
        using var dialog = new PlaylistDialog(manager).ShowHidden();
        var file = Path.Combine(setup.Folder, "playlists.json");
        File.SetAttributes(file, FileAttributes.ReadOnly); // can be read, cannot be replaced
        Ui.AnswerNextMessageBox(DialogResult.OK);
        try
        {
            List(dialog).Items[2].Checked = false;
            await Async.SettleAsync();

            Assert.True(manager.Playlists[1].Shuffle);
            Assert.True(List(dialog).Items[2].Checked);
        }
        finally
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
    }, sta: true);

    [Theory]
    [InlineData(9f)]
    [InlineData(14f)]
    [InlineData(16f)]
    public void ButtonsGrowWithTheFont_SoTheirTextIsNeverCutOff(float fontSize) => UiContext.Run(async () =>
    {
        var (setup, manager) = await LoadAsync(ThreePlaylists());
        using var _ = setup;
        // A larger font stands in for a scaled display: the buttons used to keep their fixed 23 px height.
        using var dialog = new PlaylistDialog(manager) { Font = new Font("Segoe UI", fontSize) };
        dialog.ShowHidden();
        await Async.SettleAsync();

        foreach (var button in dialog.All<Button>())
        {
            var text = TextRenderer.MeasureText(button.Text, button.Font);
            Assert.True(button.Height >= text.Height + 6, $"'{button.Text}' is {button.Height} px high for {text.Height} px of text");
            Assert.True(button.Width >= text.Width + 8, $"'{button.Text}' is {button.Width} px wide for {text.Width} px of text");

            // ...and every button is fully inside the dialog.
            var bounds = dialog.RectangleToClient(button.RectangleToScreen(button.ClientRectangle));
            Assert.True(dialog.ClientRectangle.Contains(bounds), $"'{button.Text}' lies outside the dialog: {bounds} vs {dialog.ClientRectangle}");
        }
    }, sta: true);

    [Fact]
    public void AllPlaylistsRow_ShowsThePoolSizeWhenActive() => UiContext.Run(async () =>
    {
        var (setup, manager) = await LoadAsync(ThreePlaylists());
        using var _ = setup;
        await manager.SelectAsync(null);
        using var dialog = new PlaylistDialog(manager);

        Assert.Equal("▶ All playlists", List(dialog).Items[0].SubItems[4].Text);
        Assert.Equal("3", List(dialog).Items[0].SubItems[1].Text);
    }, sta: true);

    [Fact]
    public void Buttons_DependOnTheSelection() => UiContext.Run(async () =>
    {
        var (setup, manager) = await LoadAsync(ThreePlaylists());
        using var _ = setup;
        using var dialog = new PlaylistDialog(manager).ShowHidden(); // selection events need a window handle

        SelectRow(dialog, 0); // "All playlists"
        Assert.True(dialog.Button("Switch to playlist").Enabled);
        Assert.False(dialog.Button("Rename...").Enabled);
        Assert.False(dialog.Button("Remove").Enabled);
        Assert.False(dialog.Button("Update").Enabled);
        Assert.True(dialog.Button("Update all").Enabled);

        SelectRow(dialog, 2);
        Assert.True(dialog.Button("Rename...").Enabled);
        Assert.True(dialog.Button("Remove").Enabled);
        Assert.True(dialog.Button("Update").Enabled);

        List(dialog).SelectedItems.Clear();
        Assert.False(dialog.Button("Switch to playlist").Enabled);
    }, sta: true);

    [Fact]
    public void Play_SelectsThePlaylistAndClosesTheDialog() => UiContext.Run(async () =>
    {
        var (setup, manager) = await LoadAsync(ThreePlaylists());
        using var _ = setup;
        using var dialog = new PlaylistDialog(manager).ShowHidden();

        SelectRow(dialog, 2);
        dialog.Button("Switch to playlist").PerformClick();
        await Async.WaitUntilAsync(() => dialog.IsDisposed || !dialog.Visible);

        Assert.Equal(IdB, manager.SelectedKey);
    }, sta: true);

    [Fact]
    public void Update_ReportsTheResultInTheStatusLine() => UiContext.Run(async () =>
    {
        var (setup, manager) = await LoadAsync(ThreePlaylists());
        using var _ = setup;
        setup.OnYouTube(IdB, "b1", "b2", "b3");
        using var dialog = new PlaylistDialog(manager).ShowHidden();

        SelectRow(dialog, 2);
        dialog.Button("Update").PerformClick();
        await Async.WaitUntilAsync(() => Status(dialog).Text.StartsWith("Updated"));

        Assert.Equal("Updated \"Beta\": +2 / −0 videos (3 in total).", Status(dialog).Text);
        Assert.Equal("3", List(dialog).Items[2].SubItems[1].Text); // the list refreshed
    }, sta: true);

    [Fact]
    public void UpdateAll_ShowsTheLoadingSymbolForEveryPlaylistAtOnce_AndTheResultAsEachOneFinishes() => UiContext.Run(async () =>
    {
        var (setup, manager) = await LoadAsync(ThreePlaylists());
        using var _ = setup;
        var firstDownload = new TaskCompletionSource<IReadOnlyList<string>>();
        setup.YouTube.GetPlaylistVideoIdsAsync(IdA, Arg.Any<CancellationToken>()).Returns(firstDownload.Task);
        setup.OnYouTube(IdB, "b1");
        setup.OnYouTube(IdC, "c1");
        using var dialog = new PlaylistDialog(manager).ShowHidden();
        string Symbol(int row) => List(dialog).Items[row].SubItems[3].Text;

        dialog.Button("Update all").PerformClick();
        await Async.WaitUntilAsync(() => Status(dialog).Text.StartsWith("Updating 1/3"));

        // Beta and Unreachable have not been started yet, but already show that they are about to be updated.
        Assert.Equal(PlaylistDialog.LoadingIcon, Symbol(0)); // all playlists
        Assert.Equal(PlaylistDialog.LoadingIcon, Symbol(1));
        Assert.Equal(PlaylistDialog.LoadingIcon, Symbol(2));
        Assert.Equal(PlaylistDialog.LoadingIcon, Symbol(3));

        firstDownload.SetResult(new[] { "a1", "a2" });
        await Async.WaitUntilAsync(() => Status(dialog).Text.StartsWith("Updated 3"));

        Assert.Equal(string.Empty, Symbol(0));
        Assert.All(new[] { 1, 2, 3 }, row => Assert.Equal(PlaylistDialog.SuccessIcon, Symbol(row)));
    }, sta: true);

    [Fact]
    public void SeveralSelectedRows_OnlyAllowRemoveUpdateUpdateAllAndClose() => UiContext.Run(async () =>
    {
        var (setup, manager) = await LoadAsync(ThreePlaylists());
        using var _ = setup;
        using var dialog = new PlaylistDialog(manager).ShowHidden();
        Assert.True(List(dialog).MultiSelect);

        SelectRow(dialog, 1);
        List(dialog).Items[2].Selected = true; // like Ctrl+click: adds to the selection
        Assert.Equal(2, List(dialog).SelectedItems.Count);

        Assert.False(dialog.Button("Switch to playlist").Enabled);
        Assert.False(dialog.Button("Add...").Enabled);
        Assert.False(dialog.Button("Rename...").Enabled);
        Assert.True(dialog.Button("Remove").Enabled);
        Assert.True(dialog.Button("Update").Enabled);
        Assert.True(dialog.Button("Update all").Enabled);
        Assert.True(dialog.Button("Close").Enabled);

        List(dialog).Items[2].Selected = false; // back to one row: everything is available again
        Assert.True(dialog.Button("Switch to playlist").Enabled);
        Assert.True(dialog.Button("Add...").Enabled);
        Assert.True(dialog.Button("Rename...").Enabled);
    }, sta: true);

    [Fact]
    public void RemovingSeveralSelectedPlaylists_RemovesThemAll() => UiContext.Run(async () =>
    {
        var (setup, manager) = await LoadAsync(ThreePlaylists());
        using var _ = setup;
        using var dialog = new PlaylistDialog(manager).ShowHidden();
        SelectRow(dialog, 2);
        List(dialog).Items[3].Selected = true;
        string? question = null;
        Ui.AnswerNextMessageBox(DialogResult.Yes, text => question = text);

        dialog.Button("Remove").PerformClick();
        await Async.WaitUntilAsync(() => manager.Playlists.Count == 1);

        Assert.Contains("these 2 playlists", question);
        Assert.Equal("Alpha", manager.Playlists.Single().Title);
        Assert.Equal("Removed 2 playlists.", Status(dialog).Text);
    }, sta: true);

    [Fact]
    public void UpdatingSeveralSelectedPlaylists_UpdatesJustThose() => UiContext.Run(async () =>
    {
        var (setup, manager) = await LoadAsync(ThreePlaylists());
        using var _ = setup;
        setup.OnYouTube(IdA, "a1", "a2", "a3");
        setup.OnYouTube(IdB, "b1");
        using var dialog = new PlaylistDialog(manager).ShowHidden();
        var downloadsOfC = setup.YouTube.ReceivedCalls().Count(c => c.GetArguments().Contains(IdC));
        SelectRow(dialog, 1);
        List(dialog).Items[2].Selected = true;

        dialog.Button("Update").PerformClick();
        await Async.WaitUntilAsync(() => Status(dialog).Text.StartsWith("Updated 2"));

        Assert.Equal("Updated 2 playlist(s): +1 / −0 videos.", Status(dialog).Text);
        Assert.Equal(downloadsOfC, setup.YouTube.ReceivedCalls().Count(c => c.GetArguments().Contains(IdC)));
    }, sta: true);

    [Fact]
    public void UpdateAll_UpdatesEveryPlaylistAndSummarizes() => UiContext.Run(async () =>
    {
        // (Failures would open a MessageBox, which a test cannot close; the manager tests cover them.)
        var (setup, manager) = await LoadAsync(ThreePlaylists());
        using var _ = setup;
        setup.OnYouTube(IdA, "a1", "a2", "a3");
        setup.OnYouTube(IdB, "b2"); // b1 is gone (an empty answer would not count: it fails, see the manager tests)
        setup.OnYouTube(IdC, "c1", "c2");
        using var dialog = new PlaylistDialog(manager).ShowHidden();

        dialog.Button("Update all").PerformClick();
        await Async.WaitUntilAsync(() => Status(dialog).Text.StartsWith("Updated 3"));

        Assert.Equal("Updated 3 playlist(s): +4 / −1 videos.", Status(dialog).Text);
        Assert.Equal(SystemColors.GrayText, Status(dialog).ForeColor);
        Assert.Equal("2", List(dialog).Items[3].SubItems[1].Text);
    }, sta: true);

    [Fact]
    public void Rename_AsksForTheNewName() => UiContext.Run(async () =>
    {
        var (setup, manager) = await LoadAsync(ThreePlaylists());
        using var _ = setup;
        using var dialog = new PlaylistDialog(manager).ShowHidden();
        SelectRow(dialog, 2);
        Ui.AnswerNextDialog<TextPromptDialog>(prompt =>
        {
            Assert.Equal("Beta", prompt.All<TextBox>().Single().Text);
            prompt.All<TextBox>().Single().Text = "  Gamma ";
            prompt.Button("OK").PerformClick();
        });

        dialog.Button("Rename...").PerformClick();

        Assert.Equal("Gamma", manager.Playlists[1].Title);
        Assert.Equal("Renamed to \"Gamma\".", Status(dialog).Text);
        Assert.Equal("    Gamma", List(dialog).Items[2].SubItems[4].Text);
    }, sta: true);

    [Fact]
    public void Rename_Cancelled_ChangesNothing() => UiContext.Run(async () =>
    {
        var (setup, manager) = await LoadAsync(ThreePlaylists());
        using var _ = setup;
        using var dialog = new PlaylistDialog(manager).ShowHidden();
        SelectRow(dialog, 2);
        Ui.AnswerNextDialog<TextPromptDialog>(prompt => prompt.Button("Cancel").PerformClick());

        dialog.Button("Rename...").PerformClick();

        Assert.Equal("Beta", manager.Playlists[1].Title);
    }, sta: true);

    [Fact]
    public void TextPrompt_DisablesOkForEmptyInput() => UiContext.Run(async () =>
    {
        var (setup, manager) = await LoadAsync(ThreePlaylists());
        using var _ = setup;
        using var dialog = new PlaylistDialog(manager).ShowHidden();
        SelectRow(dialog, 1);
        var okEnabled = true;
        Ui.AnswerNextDialog<TextPromptDialog>(prompt =>
        {
            prompt.All<TextBox>().Single().Text = "   ";
            okEnabled = prompt.Button("OK").Enabled;
            prompt.Button("Cancel").PerformClick();
        });

        dialog.Button("Rename...").PerformClick();

        Assert.False(okEnabled);
    }, sta: true);

    [Fact]
    public void Add_OpensTheAddDialog_AndAddsThePlaylist() => UiContext.Run(async () =>
    {
        var (setup, manager) = await LoadAsync(ThreePlaylists());
        using var _ = setup;
        setup.YouTube.GetPlaylistTitleAsync("PLdddddddddddddddddddddddddddddddd", Arg.Any<CancellationToken>()).Returns("Delta on YouTube");
        setup.OnYouTube("PLdddddddddddddddddddddddddddddddd", "d1");
        using var dialog = new PlaylistDialog(manager).ShowHidden();
        Ui.AnswerNextDialog<AddPlaylistDialog>(add =>
        {
            add.All<TextBox>().First().Text = Url("PLdddddddddddddddddddddddddddddddd");
            var ok = add.Button("Add");
            var poll = new System.Windows.Forms.Timer { Interval = 20 };
            poll.Tick += (_, _) =>
            {
                if (!ok.Enabled)
                    return;
                poll.Dispose();
                ok.PerformClick();
            };
            poll.Start();
        });

        dialog.Button("Add...").PerformClick();
        await Async.WaitUntilAsync(() => Status(dialog).Text.StartsWith("Added"));

        Assert.Equal("Added \"Delta on YouTube\" with 1 videos.", Status(dialog).Text);
        Assert.Equal(5, List(dialog).Items.Count);
    }, sta: true);

    [Fact]
    public void KeyboardShortcuts_Rename() => UiContext.Run(async () =>
    {
        var (setup, manager) = await LoadAsync(ThreePlaylists());
        using var _ = setup;
        using var dialog = new PlaylistDialog(manager).ShowHidden();
        SelectRow(dialog, 2);
        var asked = false;
        Ui.AnswerNextDialog<TextPromptDialog>(prompt => { asked = true; prompt.Button("Cancel").PerformClick(); });

        typeof(Control).GetMethod("OnKeyDown", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(dialog, new object[] { new KeyEventArgs(Keys.F2) });

        Assert.True(asked);
    }, sta: true);

    [Fact]
    public void Remove_AsksForConfirmation_AndRemovesOnYes() => UiContext.Run(async () =>
    {
        var (setup, manager) = await LoadAsync(ThreePlaylists());
        using var _ = setup;
        using var dialog = new PlaylistDialog(manager).ShowHidden();
        SelectRow(dialog, 2);
        string? question = null;
        Ui.AnswerNextMessageBox(DialogResult.Yes, text => question = text);

        dialog.Button("Remove").PerformClick();

        Assert.Equal("Remove \"Beta\" from the list?", question);
        Assert.DoesNotContain(manager.Playlists, p => p.Key == IdB);
        Assert.Equal("Removed \"Beta\".", Status(dialog).Text);
        Assert.Equal(3, List(dialog).Items.Count);
    }, sta: true);

    [Fact]
    public void Remove_AnsweredWithNo_KeepsThePlaylist() => UiContext.Run(async () =>
    {
        var (setup, manager) = await LoadAsync(ThreePlaylists());
        using var _ = setup;
        using var dialog = new PlaylistDialog(manager).ShowHidden();
        SelectRow(dialog, 2);
        Ui.AnswerNextMessageBox(DialogResult.No);

        PressKey(dialog, Keys.Delete);

        Assert.Contains(manager.Playlists, p => p.Key == IdB);
    }, sta: true);

    [Fact]
    public void EnterKey_PlaysTheSelectedPlaylist() => UiContext.Run(async () =>
    {
        var (setup, manager) = await LoadAsync(ThreePlaylists());
        using var _ = setup;
        using var dialog = new PlaylistDialog(manager).ShowHidden();
        SelectRow(dialog, 0);

        PressKey(dialog, Keys.Enter);
        await Async.WaitUntilAsync(() => manager.SelectedKey == null);
    }, sta: true);

    [Fact]
    public void AFailingAction_IsShownInTheStatusLineAndAMessageBox() => UiContext.Run(async () =>
    {
        var (setup, manager) = await LoadAsync(ThreePlaylists());
        using var _ = setup;
        using var dialog = new PlaylistDialog(manager).ShowHidden();
        SelectRow(dialog, 3); // the unreachable playlist
        string? message = null;
        Ui.AnswerNextMessageBox(DialogResult.OK, text => message = text);

        dialog.Button("Switch to playlist").PerformClick();
        await Async.WaitUntilAsync(() => message != null && dialog.Button("Switch to playlist").Enabled, because: "the action finishes after the message box");

        Assert.Equal("no such playlist", message);
        Assert.Equal("no such playlist", Status(dialog).Text);
        Assert.Equal(Color.Firebrick, Status(dialog).ForeColor);
        Assert.Equal(IdA, manager.SelectedKey);
        Assert.True(dialog.Visible, "the dialog stays open");
    }, sta: true);

    [Fact]
    public void UpdateAll_WithFailures_ListsThemInAMessageBox() => UiContext.Run(async () =>
    {
        var (setup, manager) = await LoadAsync(ThreePlaylists());
        using var _ = setup;
        setup.OnYouTube(IdA, "a1", "a2", "a3");
        setup.OnYouTube(IdB, "b1");
        using var dialog = new PlaylistDialog(manager).ShowHidden();
        string? message = null;
        Ui.AnswerNextMessageBox(DialogResult.OK, text => message = text);

        dialog.Button("Update all").PerformClick();
        await Async.WaitUntilAsync(() => message != null);
        await Async.SettleAsync();

        Assert.Contains("Unreachable: no such playlist", message);
        Assert.Equal("Updated 2 playlist(s): +1 / −0 videos. 1 failed.", Status(dialog).Text);
        Assert.Equal(Color.Firebrick, Status(dialog).ForeColor);
    }, sta: true);

    [Fact]
    public void Add_Cancelled_ChangesNothing() => UiContext.Run(async () =>
    {
        var (setup, manager) = await LoadAsync(ThreePlaylists());
        using var _ = setup;
        using var dialog = new PlaylistDialog(manager).ShowHidden();
        Ui.AnswerNextDialog<AddPlaylistDialog>(add => add.Button("Cancel").PerformClick());

        dialog.Button("Add...").PerformClick();
        await Async.SettleAsync();

        Assert.Equal(3, manager.Playlists.Count);
        Assert.Equal("Select a playlist and click \"Switch to playlist\" (or press Enter) to play it.", Status(dialog).Text);
    }, sta: true);

    [Fact]
    public void Rename_ToTheSameName_ChangesNothing() => UiContext.Run(async () =>
    {
        var (setup, manager) = await LoadAsync(ThreePlaylists());
        using var _ = setup;
        using var dialog = new PlaylistDialog(manager).ShowHidden();
        SelectRow(dialog, 2);
        Ui.AnswerNextDialog<TextPromptDialog>(prompt => prompt.Button("OK").PerformClick());

        dialog.Button("Rename...").PerformClick();

        Assert.Equal("Select a playlist and click \"Switch to playlist\" (or press Enter) to play it.", Status(dialog).Text);
    }, sta: true);

    private static void PressKey(Control control, Keys key) =>
        typeof(Control).GetMethod("OnKeyDown", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(control, new object[] { new KeyEventArgs(key) });

    [Fact]
    public void ChangesFromOutside_RefreshTheList_UntilTheDialogIsClosed() => UiContext.Run(async () =>
    {
        var (setup, manager) = await LoadAsync(ThreePlaylists());
        using var _ = setup;
        var dialog = new PlaylistDialog(manager).ShowHidden();

        manager.Rename(IdB, "Renamed elsewhere");
        Assert.Equal("    Renamed elsewhere", List(dialog).Items[2].SubItems[4].Text);

        dialog.Close();
        manager.Rename(IdB, "After close"); // must not touch the disposed dialog
    }, sta: true);

    #region Automatic swap

    private static ToggleSwitch SwapSwitch(Form dialog) => dialog.All<ToggleSwitch>().Single();

    // The row reads "[switch] Swap playlists automatically every [days] days [hours] hours".
    private static NumericUpDown Days(Form dialog) => dialog.All<NumericUpDown>().ElementAt(0);

    private static NumericUpDown Hours(Form dialog) => dialog.All<NumericUpDown>().ElementAt(1);

    private static string SwapInfo(Form dialog) => dialog.All<Label>().Single(l => l.Name == "autoSwapInfo").Text;

    private static Rectangle ScreenBounds(Control control) => control.RectangleToScreen(control.ClientRectangle);

    [Fact]
    public void TheAutomaticSwap_IsBelowTheList_OffAtFirst_WithAWeekAsInterval() => UiContext.Run(async () =>
    {
        var (setup, manager) = await LoadAsync(ThreePlaylists());
        using var _ = setup;
        using var dialog = new PlaylistDialog(manager).ShowHidden();

        Assert.True(ScreenBounds(SwapSwitch(dialog)).Top >= ScreenBounds(List(dialog)).Bottom);
        Assert.True(ScreenBounds(SwapSwitch(dialog)).Bottom <= ScreenBounds(Status(dialog)).Top);
        Assert.Equal("Swap playlists automatically every", SwapSwitch(dialog).Text);
        Assert.True(SwapSwitch(dialog).Enabled);
        Assert.False(SwapSwitch(dialog).Checked);
        Assert.Equal(7, Days(dialog).Value);
        Assert.Equal(0, Hours(dialog).Value);
        Assert.Equal("Off: the playlist only changes when you switch it.", SwapInfo(dialog));
    }, sta: true);

    [Fact]
    public void WithASinglePlaylist_TheAutomaticSwapCannotBeTurnedOn() => UiContext.Run(async () =>
    {
        var (setup, manager) = await LoadAsync(new PlaylistSetup().WithPlaylist(IdA, "Alpha", selected: true).WithCache(IdA, "a1"));
        using var _ = setup;
        using var dialog = new PlaylistDialog(manager).ShowHidden();

        Assert.False(SwapSwitch(dialog).Enabled);
        Assert.False(Days(dialog).Enabled);
        Assert.False(Hours(dialog).Enabled);
        Assert.Equal("Add a second playlist to swap playlists automatically.", SwapInfo(dialog));
    }, sta: true);

    [Fact]
    public void TurningTheSwitchOn_SavesIt_AndTellsWhenAndToWhichPlaylistTheNextSwapGoes() => UiContext.Run(async () =>
    {
        var (setup, manager) = await LoadAsync(ThreePlaylists());
        using var _ = setup;
        using var dialog = new PlaylistDialog(manager).ShowHidden();

        SwapSwitch(dialog).Checked = true;

        Assert.True(manager.AutoSwapEnabled);
        Assert.Equal(TimeSpan.FromDays(7), manager.AutoSwapInterval);
        Assert.True(setup.SavedLibrary().AutoSwap.Enabled);
        var info = SwapInfo(dialog);
        Assert.StartsWith($"Next: \"Beta\" on {manager.NextSwapUtc!.Value.LocalDateTime:g}, after the video that is playing then", info);
        Assert.Contains($"counted from the last change, {manager.LastSwapUtc!.Value.LocalDateTime:g}", info);

        SwapSwitch(dialog).Checked = false;
        Assert.False(manager.AutoSwapEnabled);
        Assert.False(setup.SavedLibrary().AutoSwap.Enabled);
    }, sta: true);

    [Fact]
    public void ChangingTheDaysOrHours_SavesTheInterval() => UiContext.Run(async () =>
    {
        var (setup, manager) = await LoadAsync(ThreePlaylists());
        using var _ = setup;
        using var dialog = new PlaylistDialog(manager).ShowHidden();
        SwapSwitch(dialog).Checked = true;

        Days(dialog).Value = 2;
        Hours(dialog).Value = 5;

        Assert.Equal(TimeSpan.FromDays(2) + TimeSpan.FromHours(5), manager.AutoSwapInterval);
        Assert.Equal(53, setup.SavedLibrary().AutoSwap.IntervalHours);
        Assert.True(manager.AutoSwapEnabled);
    }, sta: true);

    [Fact]
    public void NoDaysAndNoHours_BecomeOneHour_TheShortestInterval() => UiContext.Run(async () =>
    {
        var (setup, manager) = await LoadAsync(ThreePlaylists());
        using var _ = setup;
        using var dialog = new PlaylistDialog(manager).ShowHidden();

        Days(dialog).Value = 0;

        Assert.Equal(1, Hours(dialog).Value);
        Assert.Equal(TimeSpan.FromHours(1), manager.AutoSwapInterval);
    }, sta: true);

    [Fact]
    public void ChangesFromElsewhere_ShowInTheSwapControls() => UiContext.Run(async () =>
    {
        var (setup, manager) = await LoadAsync(ThreePlaylists());
        using var _ = setup;
        using var dialog = new PlaylistDialog(manager).ShowHidden();

        manager.SetAutoSwap(true, TimeSpan.FromDays(3) + TimeSpan.FromHours(12));
        Assert.True(SwapSwitch(dialog).Checked);
        Assert.Equal(3, Days(dialog).Value);
        Assert.Equal(12, Hours(dialog).Value);

        await manager.SelectAsync(IdB); // e.g. !skipplaylist in the chat
        Assert.StartsWith("Next: \"Alpha\"", SwapInfo(dialog)); // the one after Beta could not be loaded, so it starts over

        manager.Remove(IdC);
        manager.Remove(IdB); // a single playlist is left: the swap is turned off
        Assert.False(SwapSwitch(dialog).Checked);
        Assert.False(SwapSwitch(dialog).Enabled);
    }, sta: true);

    [Fact]
    public void WhenSavingTheSwapFails_TheSwitchShowsTheSavedSettingAgain() => UiContext.Run(async () =>
    {
        var (setup, manager) = await LoadAsync(ThreePlaylists());
        using var _ = setup;
        using var dialog = new PlaylistDialog(manager).ShowHidden();
        var file = Path.Combine(setup.Folder, "playlists.json");
        File.SetAttributes(file, FileAttributes.ReadOnly);
        string? message = null;
        Ui.AnswerNextMessageBox(DialogResult.OK, text => message = text);
        try
        {
            SwapSwitch(dialog).Checked = true;
            await Async.WaitUntilAsync(() => message != null);

            Assert.False(manager.AutoSwapEnabled);
            Assert.False(SwapSwitch(dialog).Checked);
            Assert.Equal(Color.Firebrick, Status(dialog).ForeColor);
        }
        finally
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
    }, sta: true);

    [Theory]
    [InlineData(9f)]
    [InlineData(16f)]
    public void TheSwapControlsGrowWithTheFont(float fontSize) => UiContext.Run(async () =>
    {
        var (setup, manager) = await LoadAsync(ThreePlaylists());
        using var _ = setup;
        using var dialog = new PlaylistDialog(manager) { Font = new Font("Segoe UI", fontSize) };
        dialog.ShowHidden();
        await Async.SettleAsync();

        var toggle = SwapSwitch(dialog);
        var text = TextRenderer.MeasureText(toggle.Text, toggle.Font);
        Assert.True(toggle.Width >= text.Width + 2 * text.Height, $"the switch is {toggle.Width} px wide for {text.Width} px of text");
        Assert.True(toggle.Height >= text.Height);
        foreach (var box in new[] { Days(dialog), Hours(dialog) })
            Assert.True(box.Width >= TextRenderer.MeasureText("365", box.Font).Width + SystemInformation.VerticalScrollBarWidth);

        var row = ScreenBounds(Hours(dialog).Parent!);
        Assert.True(dialog.RectangleToScreen(dialog.ClientRectangle).Contains(row), "the swap row lies outside the dialog");
    }, sta: true);

    #endregion
}

public class AddPlaylistDialogTests
{
    private static async Task<(PlaylistSetup Setup, PlaylistManager Manager, AddPlaylistDialog Dialog)> OpenAsync()
    {
        var setup = new PlaylistSetup().WithPlaylist(IdA, "Alpha").WithCache(IdA, "a1");
        setup.YouTube.GetPlaylistTitleAsync(IdB, Arg.Any<CancellationToken>()).Returns("Beta on YouTube");
        setup.YouTube.GetPlaylistTitleAsync(IdC, Arg.Any<CancellationToken>()).ThrowsAsync(new HttpRequestException("This playlist is private."));
        var manager = setup.Create();
        await manager.InitializeAsync();
        return (setup, manager, new AddPlaylistDialog(manager).ShowHidden());
    }

    private static TextBox Link(Form dialog) => dialog.All<TextBox>().First();

    private static TextBox Name(Form dialog) => dialog.All<TextBox>().Last();

    private static Label Status(Form dialog) => dialog.All<Label>().Single(l => l.MaximumSize.Width == 460);

    [Fact]
    public void InitiallyAsksForALink_AndCannotBeConfirmed() => UiContext.Run(async () =>
    {
        var (setup, _, dialog) = await OpenAsync();
        using var __ = setup;
        using var ___ = dialog;

        Assert.Equal("Paste the link of a YouTube playlist.", Status(dialog).Text);
        Assert.False(dialog.Button("Add").Enabled);
        Assert.False(Name(dialog).Enabled);
    }, sta: true);

    [Fact]
    public void AValidLink_PrefillsTheNameFromYouTube() => UiContext.Run(async () =>
    {
        var (setup, _, dialog) = await OpenAsync();
        using var __ = setup;
        using var ___ = dialog;

        Link(dialog).Text = $"https://www.youtube.com/watch?v=x&list={IdB}";
        await Async.WaitUntilAsync(() => dialog.Button("Add").Enabled);

        Assert.Equal("Beta on YouTube", Name(dialog).Text);
        Assert.True(Name(dialog).Enabled);
        Assert.Contains("Found on YouTube", Status(dialog).Text);
        Assert.Equal("Beta on YouTube", dialog.PlaylistTitle);
        Assert.Equal(Url(IdB), dialog.PlaylistUrl);
    }, sta: true);

    [Fact]
    public void AnEditedName_IsKeptWhenTheLinkChanges() => UiContext.Run(async () =>
    {
        var (setup, _, dialog) = await OpenAsync();
        using var __ = setup;
        using var ___ = dialog;
        Link(dialog).Text = Url(IdB);
        await Async.WaitUntilAsync(() => dialog.Button("Add").Enabled);

        Name(dialog).Text = "  My own name ";
        Link(dialog).Text = Url(IdB) + "&x=1";
        await Async.WaitUntilAsync(() => dialog.Button("Add").Enabled);

        Assert.Equal("My own name", dialog.PlaylistTitle);
    }, sta: true);

    [Fact]
    public void AnEmptyName_CannotBeConfirmed() => UiContext.Run(async () =>
    {
        var (setup, _, dialog) = await OpenAsync();
        using var __ = setup;
        using var ___ = dialog;
        Link(dialog).Text = Url(IdB);
        await Async.WaitUntilAsync(() => dialog.Button("Add").Enabled);

        Name(dialog).Text = "  ";

        Assert.False(dialog.Button("Add").Enabled);
    }, sta: true);

    [Theory]
    [InlineData("https://example.com/not-youtube", "This is not a valid YouTube playlist link.")]
    [InlineData("ALREADY", "This playlist is already in the list.")]
    [InlineData("PRIVATE", "The playlist could not be found on YouTube: This playlist is private.")]
    public void ProblemsAreExplained(string link, string expected) => UiContext.Run(async () =>
    {
        var (setup, _, dialog) = await OpenAsync();
        using var __ = setup;
        using var ___ = dialog;

        Link(dialog).Text = link switch { "ALREADY" => Url(IdA), "PRIVATE" => Url(IdC), _ => link };
        await Async.WaitUntilAsync(() => Status(dialog).Text == expected);

        Assert.Equal(Color.Firebrick, Status(dialog).ForeColor);
        Assert.False(dialog.Button("Add").Enabled);
    }, sta: true);

    [Fact]
    public void ClearingTheLink_AsksForOneAgain() => UiContext.Run(async () =>
    {
        var (setup, _, dialog) = await OpenAsync();
        using var __ = setup;
        using var ___ = dialog;
        Link(dialog).Text = "x";
        await Async.WaitUntilAsync(() => Status(dialog).Text.StartsWith("This is not"));

        Link(dialog).Text = "";
        await Async.WaitUntilAsync(() => Status(dialog).Text == "Paste the link of a YouTube playlist.");
    }, sta: true);
}

public class TwitchLoginDialogTests
{
    private sealed class SignIn : IDisposable
    {
        private readonly TempDirectory _dir = new();

        public SignIn(string login)
        {
            Auth.StartDeviceFlowAsync(Arg.Any<CancellationToken>()).Returns(
                new DeviceCodeInfo("dev", "WXYZ-9876", "https://www.twitch.tv/activate?device-code=WXYZ-9876", TimeSpan.FromMinutes(30), TimeSpan.FromSeconds(1)));
            Auth.PollDeviceTokenAsync("dev", Arg.Any<CancellationToken>()).Returns(
                new DevicePollResult(DevicePollStatus.Success, new TokenResponse("acc", "ref", TimeSpan.FromHours(4), Array.Empty<string>())));
            Auth.ValidateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new TokenValidation(login, "1", TimeSpan.FromHours(4), Array.Empty<string>()));
            Manager = new TwitchTokenManager(Auth, new TwitchTokenStore(_dir.Path), timeProvider: Time);
        }

        public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 11, 12, 0, 0, 0, TimeSpan.Zero));

        public ITwitchAuthClient Auth { get; } = Substitute.For<ITwitchAuthClient>();

        public TwitchTokenManager Manager { get; }

        public void Dispose()
        {
            Manager.Dispose();
            _dir.Dispose();
        }
    }

    private static Label Status(Form dialog) => dialog.All<Label>().Last();

    private static TextBox Code(Form dialog) => dialog.All<TextBox>().Single();

    [Fact]
    public void ShowsTheCode_AndReportsTheSuccessfulSignIn() => UiContext.Run(async () =>
    {
        using var signIn = new SignIn("lobosbot");
        using var dialog = new TwitchLoginDialog(signIn.Manager, "LobosBot").ShowHidden();

        await Async.WaitUntilAsync(() => Code(dialog).Text == "WXYZ-9876");
        Assert.True(dialog.Button("Open activation page").Enabled);
        Assert.True(dialog.Button("Copy code").Enabled);
        Assert.StartsWith("Waiting for the confirmation on twitch.tv", Status(dialog).Text);

        signIn.Time.Advance(TimeSpan.FromSeconds(1));
        await Async.WaitUntilAsync(() => Status(dialog).Text.StartsWith("Signed in"));

        Assert.Equal("Signed in as \"lobosbot\". The token is renewed automatically from now on.", Status(dialog).Text);
        Assert.Equal("Close", dialog.All<Button>().Single(b => b.Text is "Close" or "Cancel").Text);
        Assert.False(dialog.Button("New code").Visible);
        Assert.Equal("lobosbot", signIn.Manager.Current!.Login);
    }, sta: true);

    [Fact]
    public void WarnsWhenSignedInWithAnotherAccountThanTheBot() => UiContext.Run(async () =>
    {
        using var signIn = new SignIn("myownaccount");
        using var dialog = new TwitchLoginDialog(signIn.Manager, "lobosbot").ShowHidden();
        await Async.WaitUntilAsync(() => Code(dialog).Text == "WXYZ-9876");

        signIn.Time.Advance(TimeSpan.FromSeconds(1));
        await Async.WaitUntilAsync(() => Status(dialog).Text.StartsWith("Signed in"));

        Assert.Contains("botUsername in the settings is \"lobosbot\"", Status(dialog).Text);
        Assert.Equal(Color.Firebrick, Status(dialog).ForeColor);
        Assert.True(dialog.Button("New code").Visible);
    }, sta: true);

    [Fact]
    public void AFailedSignIn_OffersANewCode() => UiContext.Run(async () =>
    {
        using var signIn = new SignIn("lobosbot");
        signIn.Auth.PollDeviceTokenAsync("dev", Arg.Any<CancellationToken>()).Returns(new DevicePollResult(DevicePollStatus.Denied));
        using var dialog = new TwitchLoginDialog(signIn.Manager, "").ShowHidden();
        await Async.WaitUntilAsync(() => Code(dialog).Text == "WXYZ-9876");

        signIn.Time.Advance(TimeSpan.FromSeconds(1));
        await Async.WaitUntilAsync(() => dialog.Button("New code").Visible);

        Assert.Equal("The authorization was declined on twitch.tv.", Status(dialog).Text);
        Assert.False(dialog.Button("Copy code").Enabled);

        // A new attempt requests a new code.
        signIn.Auth.PollDeviceTokenAsync("dev", Arg.Any<CancellationToken>()).Returns(
            new DevicePollResult(DevicePollStatus.Success, new TokenResponse("acc", "ref", TimeSpan.FromHours(4), Array.Empty<string>())));
        dialog.Button("New code").PerformClick();
        await Async.SettleAsync();
        signIn.Time.Advance(TimeSpan.FromSeconds(1));
        await Async.WaitUntilAsync(() => Status(dialog).Text.StartsWith("Signed in"));
        await signIn.Auth.Received(2).StartDeviceFlowAsync(Arg.Any<CancellationToken>());
    }, sta: true);

    [Fact]
    public void CopyCode_PutsTheCodeOnTheClipboard() => UiContext.Run(async () =>
    {
        using var signIn = new SignIn("lobosbot");
        using var dialog = new TwitchLoginDialog(signIn.Manager, "").ShowHidden();
        await Async.WaitUntilAsync(() => Code(dialog).Text == "WXYZ-9876");
        // Keep a copy of the user's clipboard and put it back afterwards, so running the tests does not disturb them.
        var saved = new DataObject();
        var current = Clipboard.GetDataObject();
        foreach (var format in current?.GetFormats(autoConvert: false) ?? Array.Empty<string>())
        {
            try
            {
                saved.SetData(format, current!.GetData(format, autoConvert: false));
            }
            catch (Exception)
            {
                // Some formats cannot be read back; skip them.
            }
        }

        try
        {
            dialog.Button("Copy code").PerformClick();

            Assert.Equal("WXYZ-9876", Clipboard.GetText());
        }
        finally
        {
            if (saved.GetFormats().Length > 0)
                Clipboard.SetDataObject(saved, copy: true);
            else
                Clipboard.Clear();
        }
    }, sta: true);

    [Fact]
    public void Closing_CancelsTheSignIn() => UiContext.Run(async () =>
    {
        using var signIn = new SignIn("lobosbot");
        signIn.Auth.PollDeviceTokenAsync("dev", Arg.Any<CancellationToken>()).Returns(new DevicePollResult(DevicePollStatus.Pending));
        var dialog = new TwitchLoginDialog(signIn.Manager, "").ShowHidden();
        await Async.WaitUntilAsync(() => Code(dialog).Text == "WXYZ-9876");

        dialog.Close();
        signIn.Time.Advance(TimeSpan.FromMinutes(1));
        await Async.SettleAsync();

        Assert.Null(signIn.Manager.Current);
        await signIn.Auth.DidNotReceiveWithAnyArgs().PollDeviceTokenAsync(default!, default);
    }, sta: true);
}
