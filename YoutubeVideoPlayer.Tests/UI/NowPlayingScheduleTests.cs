using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using YouTubeVideoPlayer.Playback;
using YouTubeVideoPlayer.Tests.TestSupport;
using YouTubeVideoPlayer.UI;

namespace YouTubeVideoPlayer.Tests.UI;

public class NowPlayingScheduleTests
{
    private sealed class Setup : IDisposable
    {
        public Setup()
        {
            Playback.Current.Returns(new NowPlaying("v1", "Video", TimeSpan.FromHours(1)));
            Playback.IsPlaying.Returns(true);
            Schedule = new NowPlayingSchedule(Playback, Time);
            Schedule.Changed += (_, __) => Changes++;
        }

        public FakeTimeProvider Time { get; } = new();

        public IPlaybackControl Playback { get; } = Substitute.For<IPlaybackControl>();

        public NowPlayingSchedule Schedule { get; }

        public int Changes { get; private set; }

        public void StartVideo(string id = "v1") =>
            Playback.NowPlayingChanged += Raise.Event<EventHandler<NowPlaying>>(Playback, new NowPlaying(id, "Video " + id, null));

        public async Task AdvanceAsync(TimeSpan time)
        {
            Time.Advance(time);
            await Async.SettleAsync();
        }

        public void Dispose() => Schedule.Dispose();
    }

    [Fact]
    public void AVideoStart_ShowsTheCard_ForItsTime() => UiContext.Run(async () =>
    {
        using var s = new Setup();
        Assert.False(s.Schedule.IsShown);

        s.StartVideo();
        Assert.True(s.Schedule.IsShown);
        Assert.Equal(1, s.Changes);

        await s.AdvanceAsync(NowPlayingSchedule.ShowDuration - TimeSpan.FromSeconds(1));
        Assert.True(s.Schedule.IsShown);

        await s.AdvanceAsync(TimeSpan.FromSeconds(1));
        Assert.False(s.Schedule.IsShown);
        Assert.Equal(2, s.Changes);
    });

    [Fact]
    public void WhileAVideoPlays_TheCardComesBackEveryInterval() => UiContext.Run(async () =>
    {
        using var s = new Setup();
        s.StartVideo();
        await s.AdvanceAsync(NowPlayingSchedule.ShowDuration);

        await s.AdvanceAsync(NowPlayingSchedule.Interval - NowPlayingSchedule.ShowDuration);
        Assert.True(s.Schedule.IsShown);

        await s.AdvanceAsync(NowPlayingSchedule.ShowDuration); // the hide timer was started by the tick
        Assert.False(s.Schedule.IsShown);

        await s.AdvanceAsync(NowPlayingSchedule.Interval - NowPlayingSchedule.ShowDuration);
        Assert.True(s.Schedule.IsShown);
    });

    [Fact]
    public void AnotherVideo_StartsTheIntervalAgain() => UiContext.Run(async () =>
    {
        using var s = new Setup();
        s.StartVideo("v1");
        await s.AdvanceAsync(TimeSpan.FromMinutes(6));

        s.StartVideo("v2");
        await s.AdvanceAsync(TimeSpan.FromMinutes(6)); // 12 min after the first start, 6 after the second
        Assert.False(s.Schedule.IsShown);

        await s.AdvanceAsync(TimeSpan.FromMinutes(4));
        Assert.True(s.Schedule.IsShown);
    });

    [Fact]
    public void Show_ExtendsTheTime_AndPushesTheNextRegularAppearanceBack() => UiContext.Run(async () =>
    {
        using var s = new Setup();
        s.StartVideo();
        await s.AdvanceAsync(TimeSpan.FromSeconds(10));

        s.Schedule.Show(); // !current while it is up
        await s.AdvanceAsync(TimeSpan.FromSeconds(10));
        Assert.True(s.Schedule.IsShown); // 20 s after the start, 10 s after the request
        Assert.Equal(1, s.Changes);

        await s.AdvanceAsync(NowPlayingSchedule.ShowDuration);
        Assert.False(s.Schedule.IsShown);

        // 10 minutes after the request, not after the start of the video.
        await s.AdvanceAsync(NowPlayingSchedule.Interval - TimeSpan.FromSeconds(10) - NowPlayingSchedule.ShowDuration - TimeSpan.FromSeconds(1));
        Assert.False(s.Schedule.IsShown);
        await s.AdvanceAsync(TimeSpan.FromSeconds(1));
        Assert.True(s.Schedule.IsShown);
    });

    [Theory]
    [InlineData("paused")]
    [InlineData("standby")]
    [InlineData("hold")]
    [InlineData("nothing")]
    public void TheRegularAppearance_IsSkipped_WhileNoVideoPlays(string state) => UiContext.Run(async () =>
    {
        using var s = new Setup();
        s.StartVideo();
        await s.AdvanceAsync(NowPlayingSchedule.ShowDuration);
        switch (state)
        {
            case "paused": s.Playback.IsPlaying.Returns(false); break;
            case "standby": s.Playback.IsStandby.Returns(true); break;
            case "hold": s.Playback.IsWaitingForHold.Returns(true); break;
            default: s.Playback.Current.Returns((NowPlaying?)null); break;
        }

        await s.AdvanceAsync(NowPlayingSchedule.Interval);

        Assert.False(s.Schedule.IsShown);
        Assert.Equal(state == "paused", s.Schedule.CanShow);
    });

    [Fact]
    public void WhileBlocked_NothingNewIsDue_AndACardThatIsUpKeepsItsTimeWithoutBeingExtended() => UiContext.Run(async () =>
    {
        using var s = new Setup();
        s.StartVideo();
        await s.AdvanceAsync(TimeSpan.FromSeconds(10));

        s.Schedule.Blocked = true; // a vote starts
        s.Schedule.Show();         // !current
        await s.AdvanceAsync(TimeSpan.FromSeconds(2));
        Assert.False(s.Schedule.IsShown); // gone after its 12 s, not extended

        s.StartVideo("v2");
        await s.AdvanceAsync(NowPlayingSchedule.Interval);
        Assert.False(s.Schedule.IsShown); // a new video and the regular appearance are dropped too

        s.Schedule.Blocked = false;
        Assert.False(s.Schedule.IsShown); // not postponed
        s.Schedule.Show();
        Assert.True(s.Schedule.IsShown);
    });

    [Fact]
    public void Dispose_StopsEverything() => UiContext.Run(async () =>
    {
        var s = new Setup();
        s.StartVideo();
        s.Dispose();

        await s.AdvanceAsync(NowPlayingSchedule.Interval * 2);
        s.StartVideo("v2");
        s.Schedule.Show();

        Assert.Equal(1, s.Changes);
    });
}
