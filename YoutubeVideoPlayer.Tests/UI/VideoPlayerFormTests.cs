using System.Drawing;
using System.Reflection;
using LibVLCSharp.Shared;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using TwitchLib.Client.Events;
using TwitchLib.Client.Interfaces;
using TwitchLib.Communication.Events;
using YouTubeVideoPlayer.Playback;
using YouTubeVideoPlayer.Playlists;
using YouTubeVideoPlayer.Tests.TestSupport;
using YouTubeVideoPlayer.Twitch;
using YouTubeVideoPlayer.Twitch.Auth;
using YouTubeVideoPlayer.Twitch.Voting;
using YouTubeVideoPlayer.UI;
using YouTubeVideoPlayer.YouTube;
using static YouTubeVideoPlayer.Tests.TestSupport.PlaylistSetup;

namespace YouTubeVideoPlayer.Tests.UI;

public class VideoPlayerFormTests
{
    static VideoPlayerFormTests() => Core.Initialize(); // the VideoView needs the LibVLC natives

    /// <summary>Exposes the protected key handling of the form.</summary>
    private sealed class TestableForm : VideoPlayerForm
    {
        public TestableForm(PlaybackManager playback, PlaylistManager playlists, AppCoordinator coordinator,
            TwitchChatService? twitch, TwitchTokenManager? tokens, string botName, AutoVoteManager? autoVote = null)
            : base(playback, playlists, coordinator, null, twitch, tokens, botName, autoVote)
        {
        }

        public void Press(Keys key) => OnKeyDown(new KeyEventArgs(key));
    }

    /// <summary>The main form on top of real managers with substituted player, YouTube, Twitch client and clock.</summary>
    private sealed class App : IDisposable
    {
        public App(ITwitchClient? twitchClient = null, TwitchTokenManager? tokens = null, TwitchSettings? twitchSettings = null,
            PlaylistSetup? playlists = null)
        {
            Playlists = playlists ?? new PlaylistSetup().WithPlaylist(IdA, "A", selected: true).WithCache(IdA, "a1", "a2");
            Playlists.YouTube.ResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(call => PlaybackSetup.Resolved(call.ArgAt<string>(0)));
            PlaylistManager = Playlists.Create();
            Playback = new PlaybackManager(Playlists.YouTube, PlaylistManager, Player, Time);
            if (twitchClient != null)
            {
                Twitch = new TwitchChatService(
                    twitchSettings ?? new TwitchSettings { BotUsername = "lobosbot", OAuthToken = "oauth:x", Channel = "lobosjr" },
                    tokens, twitchClient);
            }

            if (Twitch != null)
            {
                Votes = new SkipVoteManager(Twitch, Playback, PlaylistManager, Playlists.YouTube, TimeSpan.FromMinutes(2), SkipVoteManager.OptionCount, Time);
                AutoVote = new AutoVoteManager(Twitch, Playback, Votes, Time);
            }

            Coordinator = new AppCoordinator(Playback, PlaylistManager, Twitch);
            Form = new TestableForm(Playback, PlaylistManager, Coordinator, Twitch, tokens, "lobosbot", AutoVote);
        }

        public PlaylistSetup Playlists { get; }

        public FakeTimeProvider Time { get; } = new();

        public IMediaPlayer Player { get; } = Substitute.For<IMediaPlayer>();

        public PlaylistManager PlaylistManager { get; }

        public PlaybackManager Playback { get; }

        public TwitchChatService? Twitch { get; }

        public SkipVoteManager? Votes { get; }

        public AutoVoteManager? AutoVote { get; }

        public CheckBox AutoVoteCheckBox => Form.All<CheckBox>().Single();

        public AppCoordinator Coordinator { get; }

        public TestableForm Form { get; }

        public Label TitleLabel => Form.All<Label>().Single(l => l.AutoEllipsis);

        public Label VolumeLabel => Form.All<Label>().Single(l => l.Size == new Size(100, 40));

        public Label BufferingLabel => Form.All<Label>().Single(l => l.Font.Size == 14);

        public Label TwitchLabel => Form.All<Label>().Single(l => l.Dock == DockStyle.Fill);

        public TrackBar Slider => Form.All<TrackBar>().Single();

        public Button MuteButton => Form.All<Button>().Single(b => b.Width == 40);

        /// <summary>Presses the Play button and waits until the video has started.</summary>
        public async Task PressPlayAsync()
        {
            Form.Button("Play").PerformClick();
            await Async.WaitUntilAsync(() => Playback.Current != null);
        }

        public async Task ShowAsync()
        {
            Form.ShowHidden();            // Load starts the coordinator
            await Async.SettleAsync();

            // The one-second delay before the first video only exists once the playlists are loaded, which can
            // take longer than one settle when the machine is busy. So advance the clock until a video has
            // started (bounded: an app without playable videos never gets one).
            var attempts = Playlists.Library.Playlists.Count > 0 ? 25 : 1;
            for (var i = 0; i < attempts && (i == 0 || (Playback.Current == null && !Playback.IsStandbyReady)); i++)
            {
                Time.Advance(PlaybackManager.DelayBeforeNextVideo);
                await Async.SettleAsync();
            }
        }

        public void Dispose()
        {
            Form.Dispose();
            AutoVote?.Dispose();
            Votes?.Dispose();
            Coordinator.Dispose();
            Playback.Dispose();
            Playlists.Dispose();
        }
    }

    #region Keyboard

    [Fact]
    public void M_TogglesMute_AndShowsIt() => UiContext.Run(() =>
    {
        using var app = new App();

        app.Form.Press(Keys.M);
        Assert.True(app.Playback.IsMuted);
        Assert.Equal("MUTE", app.VolumeLabel.Text);

        app.Form.Press(Keys.M);
        Assert.False(app.Playback.IsMuted);
        Assert.Equal("100%", app.VolumeLabel.Text);
    }, sta: true);

    [Theory]
    [InlineData(Keys.OemMinus)]
    [InlineData(Keys.Subtract)]
    public void Minus_LowersTheVolumeByFive(Keys key) => UiContext.Run(() =>
    {
        using var app = new App();

        app.Form.Press(key);

        Assert.Equal(95, app.Playback.Volume);
        Assert.Equal("Volume: 95%", app.VolumeLabel.Text);
        Assert.Equal(95, app.Slider.Value);
    }, sta: true);

    [Theory]
    [InlineData(Keys.Oemplus)]
    [InlineData(Keys.Add)]
    public void Plus_RaisesTheVolumeByFive(Keys key) => UiContext.Run(() =>
    {
        using var app = new App();
        app.Playback.Volume = 50;

        app.Form.Press(key);

        Assert.Equal(55, app.Playback.Volume);
        Assert.Equal("Volume: 55%", app.VolumeLabel.Text);
    }, sta: true);

    [Fact]
    public void F11_TogglesFullscreen_AndEscapeLeavesIt() => UiContext.Run(() =>
    {
        using var app = new App();

        app.Form.Press(Keys.F11);
        Assert.Equal(FormBorderStyle.None, app.Form.FormBorderStyle);
        Assert.True(app.Form.TopMost);

        app.Form.Press(Keys.Escape);
        Assert.NotEqual(FormBorderStyle.None, app.Form.FormBorderStyle);
        Assert.False(app.Form.TopMost);

        app.Form.Press(Keys.A); // other keys do nothing
        Assert.Equal(100, app.Playback.Volume);
    }, sta: true);

    #endregion

    #region Volume controls

    [Fact]
    public void Slider_ChangesTheVolume() => UiContext.Run(() =>
    {
        using var app = new App();

        app.Slider.Value = 40;

        Assert.Equal(40, app.Playback.Volume);
        Assert.Equal("\U0001F509", app.MuteButton.Text);
    }, sta: true);

    [Fact]
    public void VolumeChangesFromElsewhere_MoveTheSlider_AndUpdateTheMuteButton() => UiContext.Run(() =>
    {
        using var app = new App();
        Assert.Equal("\U0001F50A", app.MuteButton.Text);

        app.Playback.Volume = 70;
        Assert.Equal(70, app.Slider.Value);

        app.Playback.Volume = 0;
        Assert.Equal("\U0001F507", app.MuteButton.Text);

        app.Playback.Volume = 60;
        app.Playback.IsMuted = true;
        Assert.Equal("\U0001F507", app.MuteButton.Text);
        Assert.False(app.Slider.Enabled);

        app.Playback.IsMuted = false;
        Assert.True(app.Slider.Enabled);
        Assert.Equal("\U0001F50A", app.MuteButton.Text);
    }, sta: true);

    [Fact]
    public void MuteButton_TogglesMute() => UiContext.Run(async () =>
    {
        using var app = new App();
        await app.ShowAsync();

        app.MuteButton.PerformClick();

        Assert.True(app.Playback.IsMuted);
    }, sta: true);

    #endregion

    #region Overlays

    [Fact]
    public void Startup_StandsBy_AndThePlayButtonStartsTheFirstVideo() => UiContext.Run(async () =>
    {
        using var app = new App();

        await app.ShowAsync();

        Assert.True(app.Playback.IsStandbyReady);
        Assert.Null(app.Playback.Current);
        Assert.Equal("Ready - press Play to start", app.TitleLabel.Text);
        Assert.True(app.Form.Button("Play").Enabled);
        Assert.True(app.TitleLabel.Visible);
        app.Player.DidNotReceiveWithAnyArgs().Play(default!, default);

        await app.PressPlayAsync();

        Assert.StartsWith("Title a", app.TitleLabel.Text);
        Assert.Equal("Pause", app.Form.All<Button>().Single(b => b.Text == "Pause").Text);
    }, sta: true);

    [Fact]
    public void ThePlayButton_IsDisabledAndNoReadyTitleShown_UntilTheFirstVideoIsLoaded() => UiContext.Run(async () =>
    {
        using var app = new App();
        var lookup = new TaskCompletionSource<ResolvedVideo>();
        app.Playlists.YouTube.ResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(lookup.Task);

        Assert.False(app.Form.Button("Play").Enabled); // nothing loaded at all yet

        await app.ShowAsync();
        await Async.WaitUntilAsync(() => app.Playback.IsStandby);

        Assert.False(app.Playback.IsStandbyReady);
        Assert.False(app.Form.Button("Play").Enabled);
        Assert.DoesNotContain("Ready", app.TitleLabel.Text);

        lookup.SetResult(PlaybackSetup.Resolved("a1"));
        await Async.WaitUntilAsync(() => app.Playback.IsStandbyReady);

        Assert.True(app.Form.Button("Play").Enabled);
        Assert.Equal("Ready - press Play to start", app.TitleLabel.Text);
    }, sta: true);

    [Fact]
    public void ThePlayPauseButton_PausesAndResumesTheVideo() => UiContext.Run(async () =>
    {
        using var app = new App();
        await app.ShowAsync();
        await app.PressPlayAsync();

        app.Form.Button("Pause").PerformClick();
        Assert.True(app.Playback.IsPaused);
        app.Player.Received(1).Pause();

        app.Form.Button("Play").PerformClick();
        Assert.False(app.Playback.IsPaused);
        app.Player.Received(1).Resume();
        Assert.Single(app.Form.All<Button>(), b => b.Text == "Pause");
    }, sta: true);

    [Fact]
    public void Buffering_ShowsAnAnimatedIndicator_UntilItIsDone() => UiContext.Run(async () =>
    {
        using var app = new App();
        await app.ShowAsync();

        app.Player.Buffering += Raise.Event<EventHandler<float>>(app.Player, 10f);
        await Async.SettleAsync();
        Assert.True(app.BufferingLabel.Visible);
        Assert.StartsWith("Buffering", app.BufferingLabel.Text);
        await Async.WaitUntilAsync(() => app.BufferingLabel.Text != "Buffering...", because: "the spinner animates");

        app.Player.Buffering += Raise.Event<EventHandler<float>>(app.Player, 100f);
        await Async.SettleAsync();
        Assert.False(app.BufferingLabel.Visible);
    }, sta: true);

    [Fact]
    public void VolumeOverlay_DisappearsAfterTwoSeconds() => UiContext.Run(async () =>
    {
        using var app = new App();
        await app.ShowAsync();

        app.Form.Press(Keys.M);
        Assert.True(app.VolumeLabel.Visible);

        await Async.WaitUntilAsync(() => !app.VolumeLabel.Visible, timeoutMs: 5000);
    }, sta: true);

    #endregion

    #region Buttons

    [Fact]
    public void NextButton_SkipsImmediately() => UiContext.Run(async () =>
    {
        using var app = new App();
        await app.ShowAsync();
        app.Player.ClearReceivedCalls();

        app.Form.Button("Next").PerformClick();

        app.Player.Received(1).Stop();
    }, sta: true);

    [Fact]
    public void FullscreenAndBorderlessButtons_SwitchTheWindowMode() => UiContext.Run(async () =>
    {
        using var app = new App();
        await app.ShowAsync();

        app.Form.Button("Borderless").PerformClick();
        Assert.Equal(FormBorderStyle.None, app.Form.FormBorderStyle);
        app.Form.Press(Keys.Escape);

        app.Form.Button("Fullscreen").PerformClick();
        Assert.True(app.Form.TopMost);
    }, sta: true);

    [Fact]
    public void PlaylistsButton_OpensThePlaylistDialog() => UiContext.Run(async () =>
    {
        using var app = new App();
        await app.ShowAsync();
        var opened = false;
        Ui.AnswerNextDialog<PlaylistDialog>(dialog =>
        {
            opened = true;
            dialog.Close();
        });

        app.Form.Button("Playlists").PerformClick();

        Assert.True(opened);
    }, sta: true);

    #endregion

    #region Autovote

    [Fact]
    public void WithoutTwitch_TheAutovoteCheckboxIsThereButDisabled() => UiContext.Run(() =>
    {
        using var app = new App();

        Assert.Equal("Autovote", app.AutoVoteCheckBox.Text);
        Assert.False(app.AutoVoteCheckBox.Enabled);
        Assert.False(app.AutoVoteCheckBox.Checked);
    }, sta: true);

    [Fact]
    public void WithTwitch_TheCheckboxSwitchesTheAutovoteOnAndOff() => UiContext.Run(() =>
    {
        using var app = new App(Substitute.For<ITwitchClient>());
        var checkBox = app.AutoVoteCheckBox;

        Assert.True(checkBox.Enabled);
        Assert.False(checkBox.Checked);
        Assert.False(app.AutoVote!.Enabled); // off until the user asks for it

        checkBox.Checked = true;
        Assert.True(app.AutoVote.Enabled);

        checkBox.Checked = false;
        Assert.False(app.AutoVote.Enabled);
    }, sta: true);

    [Fact]
    public void TheCheckboxSitsInTheControlBar_NextToTheButtons() => UiContext.Run(async () =>
    {
        using var app = new App(Substitute.For<ITwitchClient>());
        app.Form.ShowHidden();
        await Async.SettleAsync();

        var checkBox = app.AutoVoteCheckBox;
        var playlists = app.Form.Button("Playlists");

        Assert.Same(playlists.Parent, checkBox.Parent);
        Assert.True(checkBox.Right <= playlists.Left, "the checkbox must not overlap the buttons");
        Assert.True(checkBox.Width >= TextRenderer.MeasureText(checkBox.Text, checkBox.Font).Width);
        Assert.True(checkBox.Visible);
    }, sta: true);

    #endregion

    #region Twitch status

    [Fact]
    public void WithoutTwitch_TheStatusSaysOff() => UiContext.Run(() =>
    {
        using var app = new App();

        Assert.Equal("Twitch: off", app.TwitchLabel.Text);
        Assert.NotEqual(Cursors.Hand, app.TwitchLabel.Cursor);
    }, sta: true);

    [Fact]
    public void TheStatusFollowsTheConnection() => UiContext.Run(async () =>
    {
        var client = Substitute.For<ITwitchClient>();
        using var app = new App(client);

        await app.Twitch!.StartAsync();
        Assert.Equal("Twitch: connecting to #lobosjr...", app.TwitchLabel.Text);

        client.OnJoinedChannel += Raise.Event<AsyncEventHandler<OnJoinedChannelArgs>>(client, new OnJoinedChannelArgs("lobosbot", "lobosjr"));
        await Async.SettleAsync();
        Assert.Equal("Twitch: connected to #lobosjr as lobosbot", app.TwitchLabel.Text);

        client.OnDisconnected += Raise.Event<AsyncEventHandler<OnDisconnectedArgs>>(client, new OnDisconnectedArgs("lobosbot"));
        await Async.SettleAsync();
        Assert.Equal("Twitch: disconnected", app.TwitchLabel.Text);

        client.OnIncorrectLogin += Raise.Event<AsyncEventHandler<OnIncorrectLoginArgs>>(client,
            new OnIncorrectLoginArgs(new TwitchLib.Client.Exceptions.ErrorLoggingInException("failed", "lobosbot")));
        await Async.SettleAsync();
        Assert.Equal("Twitch: login failed (check the token)", app.TwitchLabel.Text);
    }, sta: true);

    [Fact]
    public void WithAppSignIn_TheStatusInvitesToSignIn_AndOpensTheSignInDialog() => UiContext.Run(async () =>
    {
        using var dir = new TempDirectory();
        var auth = Substitute.For<ITwitchAuthClient>();
        auth.StartDeviceFlowAsync(Arg.Any<CancellationToken>()).Returns(new TaskCompletionSource<DeviceCodeInfo>().Task);
        using var tokens = new TwitchTokenManager(auth, new TwitchTokenStore(dir.Path));
        using var app = new App(Substitute.For<ITwitchClient>(), tokens, new TwitchSettings { ClientId = "cid", Channel = "lobosjr" });

        await app.Twitch!.StartAsync();

        Assert.Equal("Twitch: not signed in – click here to sign in", app.TwitchLabel.Text);
        Assert.Equal(Color.Firebrick, app.TwitchLabel.ForeColor);
        Assert.Equal(Cursors.Hand, app.TwitchLabel.Cursor);

        var opened = false;
        Ui.AnswerNextDialog<TwitchLoginDialog>(dialog =>
        {
            opened = true;
            dialog.Close();
        });
        typeof(Control).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(app.TwitchLabel, new object[] { EventArgs.Empty });
        Assert.True(opened);
    }, sta: true);

    #endregion

    #region Nothing to play

    [Fact]
    public void WithoutPlaylists_ExplainsItAndOpensThePlaylistDialog() => UiContext.Run(async () =>
    {
        using var app = new App(playlists: new PlaylistSetup());
        string? message = null;
        var dialogOpened = false;
        Ui.AnswerNextMessageBox(DialogResult.OK, text => message = text);
        Ui.AnswerNextDialog<PlaylistDialog>(dialog => { dialogOpened = true; dialog.Close(); });

        await app.ShowAsync();
        await Async.WaitUntilAsync(() => dialogOpened);

        Assert.Equal("There are no playlists yet. Add a YouTube playlist to start playing.", message);
        Assert.Equal(string.Empty, app.TitleLabel.Text);
    }, sta: true);

    [Fact]
    public void WhenNoPlaylistCanBeLoaded_SuggestsCheckingTheConnection() => UiContext.Run(async () =>
    {
        using var app = new App(playlists: new PlaylistSetup().WithPlaylist(IdA, "Unreachable"));
        string? message = null;
        Ui.AnswerNextMessageBox(DialogResult.OK, text => message = text);
        Ui.AnswerNextDialog<PlaylistDialog>(dialog => dialog.Close());

        await app.ShowAsync();
        await Async.WaitUntilAsync(() => message != null);

        Assert.StartsWith("None of the playlists could be loaded.", message);
    }, sta: true);

    [Fact]
    public void WhileThePlaylistDialogIsOpen_NoVideosIsNotAnnouncedTwice() => UiContext.Run(async () =>
    {
        using var app = new App(playlists: new PlaylistSetup());
        var messages = 0;
        Ui.AnswerNextMessageBox(DialogResult.OK, _ => messages++);
        Ui.AnswerNextDialog<PlaylistDialog>(dialog =>
        {
            // The pool runs empty again while the dialog is open: no second message box.
            app.Playback.SkipAsync();
            var close = new System.Windows.Forms.Timer { Interval = 300 };
            close.Tick += (_, _) => { close.Dispose(); dialog.Close(); };
            close.Start();
        });

        await app.ShowAsync();
        await Async.WaitUntilAsync(() => !Application.OpenForms.OfType<PlaylistDialog>().Any(f => !f.InvokeRequired) && messages > 0);

        Assert.Equal(1, messages);
    }, sta: true);

    #endregion

    [Fact]
    public void AfterClosing_ManagerEventsNoLongerReachTheForm() => UiContext.Run(async () =>
    {
        using var app = new App();
        await app.ShowAsync();

        app.Form.Close();
        app.Playback.Volume = 10; // would touch disposed controls if still subscribed

        Assert.True(app.Form.IsDisposed);
    }, sta: true);
}
