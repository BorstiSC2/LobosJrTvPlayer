using System.Drawing;
using System.Reflection;
using LibVLCSharp.Shared;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using TwitchLib.Client.Events;
using TwitchLib.Client.Interfaces;
using TwitchLib.Communication.Events;
using YouTubeVideoPlayer.Infrastructure;
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
            TwitchChatService? twitch, TwitchTokenManager? tokens, string botName, AutoVoteManager? autoVote = null,
            IReadOnlyList<IVoteDisplay>? voteDisplays = null, NowPlayingSchedule? nowPlaying = null)
            : base(playback, playlists, coordinator, null, twitch, tokens, botName, autoVote, voteDisplays, nowPlaying)
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
                    twitchSettings ?? new TwitchSettings { Channel = "lobosjr", SignIn = { BotLogin = "lobosbot", OAuthToken = "oauth:x" } },
                    tokens, twitchClient);
            }

            if (Twitch != null)
            {
                Votes = new SkipVoteManager(Twitch, Playback, PlaylistManager, Playlists.YouTube, TimeSpan.FromMinutes(2), SkipVoteManager.OptionCount, Time);
                VoteSkip = new VoteSkipManager(
                    Twitch, Playback, Votes, () => 1, TimeSpan.FromSeconds(60), TimeSpan.FromMinutes(30), 75, Time);
                AutoVote = new AutoVoteManager(Twitch, Playback, Votes, Time, isOtherVoteRunning: () => VoteSkip.IsRunning);
            }

            Coordinator = new AppCoordinator(Playback, PlaylistManager, Twitch);
            NowPlaying = new NowPlayingSchedule(Playback, Time);
            Form = new TestableForm(
                Playback, PlaylistManager, Coordinator, Twitch, tokens, "lobosbot", AutoVote,
                Votes == null ? null : new IVoteDisplay[] { VoteSkip!, Votes }, NowPlaying);
        }

        public PlaylistSetup Playlists { get; }

        public FakeTimeProvider Time { get; } = new();

        public IMediaPlayer Player { get; } = Substitute.For<IMediaPlayer>();

        public PlaylistManager PlaylistManager { get; }

        public PlaybackManager Playback { get; }

        public TwitchChatService? Twitch { get; }

        public SkipVoteManager? Votes { get; }

        public AutoVoteManager? AutoVote { get; }

        public VoteSkipManager? VoteSkip { get; }

        public CheckBox AutoVoteCheckBox => Form.All<CheckBox>().Single();

        public AppCoordinator Coordinator { get; }

        public TestableForm Form { get; }

        public TitleStrip TitleStrip => Form.All<TitleStrip>().Single();

        public NowPlayingSchedule NowPlaying { get; }

        public NowPlayingCard Card => Form.All<NowPlayingCard>().Single();

        /// <summary>What the vote ticker shows, as one line; <c>null</c> without a ticker.</summary>
        public string? Ticker => TitleStrip.Ticker?.PlainText;

        public Label VolumeLabel => Form.All<Label>().Single(l => l.Size == new Size(100, 40));

        public OverlayPill BufferingLabel => Form.All<OverlayPill>().Single(l => l.Name == "buffering");

        public OverlayPill WaitingLabel => Form.All<OverlayPill>().Single(l => l.Name == "waiting");

        public OverlayPill ResumeLabel => Form.All<OverlayPill>().Single(l => l.Name == "resume");

        public Label TwitchLabel => Form.All<Label>().Single(l => l.Dock == DockStyle.Fill);

        public TrackBar Slider => Form.All<TrackBar>().Single();

        public Button MuteButton => Form.All<Button>().Single(b => b.Width == 40);

        public int PlayedVideoCount() => Player.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IMediaPlayer.Play));

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
            NowPlaying.Dispose();
            AutoVote?.Dispose();
            VoteSkip?.Dispose();
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

    #region Window size (16:9 video)

    /// <summary>Somewhere no screen is, so the tests' windows never show up.</summary>
    private static readonly Rectangle OffScreenWorkArea = new(-20000, -20000, 1920, 1040);

    private static LibVLCSharp.WinForms.VideoView VideoView(App app) => app.Form.All<LibVLCSharp.WinForms.VideoView>().Single();

    private static void AssertVideoIs16To9(App app)
    {
        var video = VideoView(app).ClientSize;
        Assert.Equal(VideoWindowGeometry.VideoHeightFor(video.Width), video.Height);
    }

    [Fact]
    public void ASizeSetFromElsewhere_KeepsTheWidth_AndTheVideoAt16To9() => UiContext.Run(async () =>
    {
        using var app = new App();
        await app.ShowAsync();

        app.Form.Size = new Size(1000, 1000);

        Assert.Equal(1000, app.Form.Width);
        AssertVideoIs16To9(app);
    }, sta: true);

    [Fact]
    public void DraggingAnEdge_ScalesTheWindow_SoTheVideoStays16To9() => UiContext.Run(async () =>
    {
        using var app = new App();
        await app.ShowAsync();
        app.Form.Size = new Size(1000, 1000);
        var before = app.Form.Bounds;
        var rect = new WindowMessages.RECT { Left = before.Left, Top = before.Top, Right = before.Right + 320, Bottom = before.Bottom };

        WindowMessages.SendSizing(app.Form.Handle, VideoWindowGeometry.EdgeBottomRight, ref rect);

        var extra = app.Form.AspectLock.Extra;
        var videoWidth = rect.Right - rect.Left - extra.Width;
        Assert.Equal(1320 - extra.Width, videoWidth);
        Assert.Equal(VideoWindowGeometry.VideoHeightFor(videoWidth), rect.Bottom - rect.Top - extra.Height);
        Assert.Equal(before.Location, new Point(rect.Left, rect.Top));
    }, sta: true);

    [Fact]
    public void Maximize_IsTheLargest16To9Window_AndPressingItAgainRestoresTheWindow() => UiContext.Run(async () =>
    {
        using var app = new App();
        await app.ShowAsync();
        app.Form.AspectLock.WorkArea = () => OffScreenWorkArea;
        app.Form.Size = new Size(1000, 1000);
        var before = app.Form.Bounds;

        WindowMessages.SendMaximizeCommand(app.Form.Handle);

        Assert.Equal(FormWindowState.Normal, app.Form.WindowState);
        AssertVideoIs16To9(app);
        Assert.True(app.Form.Width > before.Width);
        var outer = Rectangle.Inflate(OffScreenWorkArea, 32, 32); // the invisible resize border may lie outside
        Assert.True(outer.Contains(app.Form.Bounds));

        WindowMessages.SendMaximizeCommand(app.Form.Handle);

        Assert.Equal(before, app.Form.Bounds);
    }, sta: true);

    [Fact]
    public void AMaximizeThatIsNotTheMaximizeCommand_BecomesTheLargest16To9Window() => UiContext.Run(async () =>
    {
        using var app = new App();
        await app.ShowAsync();
        app.Form.AspectLock.WorkArea = () => OffScreenWorkArea;

        app.Form.WindowState = FormWindowState.Maximized; // like dragging the window to the top edge
        await Async.WaitUntilAsync(() => app.Form.WindowState == FormWindowState.Normal);

        AssertVideoIs16To9(app);
        Assert.True(Rectangle.Inflate(OffScreenWorkArea, 32, 32).Contains(app.Form.Bounds));
    }, sta: true);

    [Fact]
    public void Fullscreen_FillsTheMonitor_AndLeavingItBringsBackThe16To9Window() => UiContext.Run(async () =>
    {
        using var app = new App();
        await app.ShowAsync();
        app.Form.Size = new Size(1000, 1000);
        var before = app.Form.Bounds;

        app.Form.Press(Keys.F11);
        await Async.SettleAsync();

        Assert.Equal(FormWindowState.Maximized, app.Form.WindowState); // not turned into a 16:9 window

        app.Form.Press(Keys.Escape);

        Assert.Equal(before, app.Form.Bounds);
        AssertVideoIs16To9(app);
    }, sta: true);

    [Fact]
    public void Borderless_IsTheVideoOnly_AndStays16To9() => UiContext.Run(async () =>
    {
        using var app = new App();
        await app.ShowAsync();
        app.Form.Size = new Size(1000, 1000);

        app.Form.Button("Borderless").PerformClick();
        app.Form.Width = 800;

        Assert.Equal(VideoWindowGeometry.VideoHeightFor(800), app.Form.Height);
        AssertVideoIs16To9(app);
    }, sta: true);

    private static class WindowMessages
    {
        private const int WM_SYSCOMMAND = 0x0112;
        private const int WM_SIZING = 0x0214;
        private const int SC_MAXIMIZE = 0xF030;

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        public static void SendSizing(IntPtr hwnd, int edge, ref RECT rect) => SendMessage(hwnd, WM_SIZING, (IntPtr)edge, ref rect);

        public static void SendMaximizeCommand(IntPtr hwnd) => SendMessage(hwnd, WM_SYSCOMMAND, (IntPtr)SC_MAXIMIZE, IntPtr.Zero);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, ref RECT lParam);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
    }

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
        Assert.Equal("Ready - press Play to start", app.TitleStrip.Title);
        Assert.True(app.Form.Button("Play").Enabled);
        Assert.True(app.TitleStrip.Visible);
        app.Player.DidNotReceiveWithAnyArgs().Play(default!, default);

        await app.PressPlayAsync();

        Assert.Equal(string.Empty, app.TitleStrip.Title); // the title is on the card now
        Assert.StartsWith("Title a", app.Card.Content!.Title);
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
        Assert.DoesNotContain("Ready", app.TitleStrip.Title);

        lookup.SetResult(PlaybackSetup.Resolved("a1"));
        await Async.WaitUntilAsync(() => app.Playback.IsStandbyReady);

        Assert.True(app.Form.Button("Play").Enabled);
        Assert.Equal("Ready - press Play to start", app.TitleStrip.Title);
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
        Assert.StartsWith("Buffering", app.BufferingLabel.ShownText);
        await Async.WaitUntilAsync(() => app.BufferingLabel.ShownText != "Buffering ...", because: "the dots animate");

        app.Player.Buffering += Raise.Event<EventHandler<float>>(app.Player, 100f);
        await Async.SettleAsync();
        Assert.False(app.BufferingLabel.Visible);
    }, sta: true);

    [Fact]
    public void WhileABrokenStreamIsResumed_ALabelSaysSo_InsteadOfTheBufferingLabel() => UiContext.Run(async () =>
    {
        using var app = new App();
        await app.ShowAsync();
        await app.PressPlayAsync();
        app.Player.IsPlaying.Returns(true);
        app.Player.TimeMs.Returns(61_000L);
        app.Time.Advance(PlaybackManager.FailSafeInterval); // the watchdog sees the position
        await Async.SettleAsync();
        Assert.False(app.ResumeLabel.Visible);

        // The stream breaks off 1:01 into the 3-minute video; the resumed one buffers and seeks (position 0).
        app.Player.TimeMs.Returns(0L);
        app.Player.EndReached += Raise.Event();
        await Async.WaitUntilAsync(() => app.Playback.IsResuming);
        app.Player.Buffering += Raise.Event<EventHandler<float>>(app.Player, 10f);
        await Async.SettleAsync();

        Assert.True(app.ResumeLabel.Visible);
        Assert.Equal("Connection lost - resuming at 0:58", app.ResumeLabel.Text); // the dots after it animate
        Assert.False(app.BufferingLabel.Visible); // same place in the middle: the resume label says enough
        await Async.WaitUntilAsync(() => app.ResumeLabel.ShownText != "Connection lost - resuming at 0:58 ...", because: "the dots animate");

        // It plays on at its start point: the label goes, and the buffering that is still going on shows.
        var position = 58_000L;
        app.Player.TimeMs.Returns(_ => position += 500);
        for (var check = 0; check < 2; check++)
        {
            app.Time.Advance(PlaybackManager.ResumeCheckInterval);
            await Async.SettleAsync();
        }

        Assert.False(app.Playback.IsResuming);
        Assert.False(app.ResumeLabel.Visible);
        Assert.True(app.BufferingLabel.Visible);
    }, sta: true);

    [Fact]
    public void WhileTheNextVideoWaitsForAVote_ALabelCountsDownTheTimeLeft() => UiContext.Run(async () =>
    {
        using var app = new App();
        await app.ShowAsync();
        await app.PressPlayAsync();
        var hold = app.Playback.HoldNextVideo();
        hold.Until = app.Time.GetUtcNow() + TimeSpan.FromMinutes(2);

        app.Player.EndReached += Raise.Event();
        await Async.WaitUntilAsync(() => app.Playback.IsWaitingForHold);

        Assert.True(app.WaitingLabel.Visible);
        Assert.Equal("Waiting for vote to finish... 2:00", app.WaitingLabel.Text);
        Assert.Equal(string.Empty, app.TitleStrip.Title); // the finished video's title is gone with its picture
        Assert.False(app.Form.Button("Play").Enabled);

        app.Time.Advance(TimeSpan.FromSeconds(65));
        await Async.WaitUntilAsync(() => app.WaitingLabel.Text == "Waiting for vote to finish... 0:55",
            because: "the label is updated every second");

        hold.Release();
        await Async.SettleAsync();
        app.Time.Advance(PlaybackManager.DelayBeforeNextVideo); // in case the next video was not prepared
        await Async.WaitUntilAsync(() => app.PlayedVideoCount() == 2);

        Assert.False(app.WaitingLabel.Visible);
        Assert.True(app.Form.Button("Pause").Enabled);
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
        using var app = new App(Chat.Client());
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
    public void WhenTheAutovoteIsSwitchedThroughChat_TheCheckboxFollows() => UiContext.Run(() =>
    {
        using var app = new App(Chat.Client());

        app.AutoVote!.Enabled = true;
        Assert.True(app.AutoVoteCheckBox.Checked);

        app.AutoVote.Enabled = false;
        Assert.False(app.AutoVoteCheckBox.Checked);
    }, sta: true);

    [Fact]
    public void TheCheckboxSitsInTheControlBar_NextToTheButtons() => UiContext.Run(async () =>
    {
        using var app = new App(Chat.Client());
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

    #region Vote ticker

    private static App VoteApp()
    {
        var playlists = new PlaylistSetup().WithPlaylist(IdA, "A", selected: true).WithCache(IdA, "a1", "a2", "a3", "a4", "a5");
        playlists.YouTube.GetVideoAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => new YouTubeVideoDetails("Option " + call.ArgAt<string>(0), null));
        return new App(Chat.Client(), playlists: playlists);
    }

    [Fact]
    public void WhileAVoteRuns_TheStripScrollsItsOptions_InsteadOfTheCard() => UiContext.Run(async () =>
    {
        using var app = VoteApp();
        await app.ShowAsync();
        await app.PressPlayAsync();
        var title = app.Playback.Current!.Title;
        Assert.Null(app.Ticker);
        Assert.True(app.Card.Showing);

        await app.Votes!.StartAsync("mod");
        await Async.WaitUntilAsync(() => app.Ticker != null);

        Assert.StartsWith("Vote  |  ", app.Ticker);
        Assert.Contains("1 Option ", app.Ticker);
        Assert.Contains("(0)", app.Ticker);
        Assert.EndsWith("!vote 1–3", app.Ticker);
        Assert.False(app.Card.Showing); // the ticker has its place

        app.Votes.Vote(Chat.Command("!vote 2", user: "viewer"));
        Assert.Contains("(1)", app.Ticker);
        Assert.True(app.TitleStrip.Ticker!.Items[1].Leading);

        app.Votes.Cancel(Chat.Command("!cancelvote", user: "mod"));

        Assert.Null(app.Ticker);
        Assert.Equal(title, app.Card.Content!.Title);
        Assert.True(app.Card.Showing); // still within its time after the start of the video
    }, sta: true);

    [Fact]
    public void TheTickerShowsTheTimeLeft_OnceTheCountdownRuns() => UiContext.Run(async () =>
    {
        using var app = VoteApp();
        await app.ShowAsync();
        await app.PressPlayAsync();

        await app.Votes!.StartAsync("mod");
        await Async.WaitUntilAsync(() => app.Ticker != null);
        Assert.Null(app.TitleStrip.Ticker!.Time);

        for (var i = 0; i < SkipVoteManager.OptionCount; i++)
        {
            await Async.SettleAsync();
            app.Time.Advance(SkipVoteManager.MessageDelay);
        }

        await Async.WaitUntilAsync(() => app.TitleStrip.Ticker!.Time == "2:00");
        app.Time.Advance(TimeSpan.FromSeconds(30));
        await Async.WaitUntilAsync(() => app.TitleStrip.Ticker!.Time == "1:30", because: "the ticker is updated every second");
    }, sta: true);

    [Fact]
    public void AVoteSkip_ShowsSkipAndStayInTheTicker() => UiContext.Run(async () =>
    {
        using var app = VoteApp();
        await app.ShowAsync();
        await app.PressPlayAsync();

        Assert.Null(app.VoteSkip!.Start(Chat.Command("!voteskip")));

        Assert.StartsWith("Skip this video? 1:00  |  \"Title a", app.Ticker);
        Assert.Contains("!skip (1 of 1)  ·  !stay (0)", app.Ticker);
        Assert.EndsWith("Type !skip or !stay", app.Ticker);

        app.VoteSkip.Vote(Chat.Command("!stay", user: "other"), skip: false);
        Assert.Contains("!skip (1 of 1)  ·  !stay (1)", app.Ticker);

        app.VoteSkip.Cancel(Chat.Command("!cancelvote", user: "mod"));
        Assert.Null(app.Ticker);
    }, sta: true);

    [Fact]
    public void ANewVideoDuringAVote_UpdatesTheCardBehindTheTicker() => UiContext.Run(async () =>
    {
        using var app = VoteApp();
        await app.ShowAsync();
        await app.PressPlayAsync();
        var first = app.Playback.Current!;

        app.Votes!.StartAfterCurrentAsync(TimeSpan.FromMinutes(2)).FireAndForget("test vote");
        await Async.WaitUntilAsync(() => app.Ticker != null);
        Assert.EndsWith("!vote 1–3 for the video after this one", app.Ticker);

        app.Playback.SkipAsync().FireAndForget("test skip");
        await Async.SettleAsync();
        app.Time.Advance(PlaybackManager.DelayBeforeNextVideo); // in case the next video was not prepared
        await Async.WaitUntilAsync(() => !ReferenceEquals(app.Playback.Current, first) && app.Playback.Current != null);

        Assert.NotNull(app.Ticker);
        Assert.Equal(app.Playback.Current!.Title, app.Card.Content!.Title);
        Assert.False(app.Card.Showing);
    }, sta: true);

    [Fact]
    public void TheCard_ShowsTheVideoWhenItStarts_AndSlidesOutAfterItsTime() => UiContext.Run(async () =>
    {
        using var app = new App();
        await app.ShowAsync();
        Assert.False(app.Card.Showing); // standing by: the strip says "Ready"

        await app.PressPlayAsync();

        Assert.True(app.Card.Showing);
        Assert.Equal(app.Playback.Current!.Title, app.Card.Content!.Title);
        Assert.Equal(string.Empty, app.TitleStrip.Title);

        app.Time.Advance(NowPlayingSchedule.ShowDuration);
        await Async.WaitUntilAsync(() => !app.Card.Showing);

        app.NowPlaying.Show(); // what !current does
        Assert.True(app.Card.Showing);
    }, sta: true);

    [Fact]
    public void TheCard_FollowsThePosition_WhileItIsUp() => UiContext.Run(async () =>
    {
        using var app = new App();
        await app.ShowAsync();
        await app.PressPlayAsync();
        var ms = 0L;
        app.Player.TimeMs.Returns(_ => ms);
        app.Player.LengthMs.Returns(180_000);

        ms = 65_000;

        await Async.WaitUntilAsync(() => app.Card.Content!.Position == TimeSpan.FromSeconds(65), because: "the card is updated every second");
        Assert.Contains("1:05 / 3:00", app.Card.PlainText);
    }, sta: true);

    [Fact]
    public void WhileTheNextVideoWaitsForAVote_TheCardIsNotShown() => UiContext.Run(async () =>
    {
        using var app = new App();
        await app.ShowAsync();
        await app.PressPlayAsync();
        Assert.True(app.Card.Showing);

        _ = app.Playback.HoldNextVideo();
        app.Player.EndReached += Raise.Event();
        await Async.WaitUntilAsync(() => app.Playback.IsWaitingForHold);

        Assert.False(app.Card.Showing);
        app.NowPlaying.Show();
        Assert.False(app.Card.Showing);
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
        var client = Chat.Client();
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
        using var app = new App(Chat.Client(), tokens, new TwitchSettings { Channel = "lobosjr", SignIn = { ClientId = "cid" } });

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
        Assert.Equal(string.Empty, app.TitleStrip.Title);
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
