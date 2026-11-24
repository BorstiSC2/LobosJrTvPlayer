using System.Drawing;
using Microsoft.Extensions.Time.Testing;
using YouTubeVideoPlayer.Tests.TestSupport;
using YouTubeVideoPlayer.UI;

namespace YouTubeVideoPlayer.Tests.UI;

public class AnimatedAvatarTests
{
    /// <summary>The pictures as the app gets them: copied next to the executable.</summary>
    internal static readonly string Folder = Path.Combine(AppContext.BaseDirectory, AvatarSet.FolderName);

    public static TheoryData<string> Files() =>
        new(Directory.GetFiles(Folder, "*.gif").Concat(Directory.GetFiles(Folder, "*.png")).Select(Path.GetFileName).OfType<string>());

    [Theory]
    [MemberData(nameof(Files))]
    public void EveryPicture_Loads_WithFramesAndUsableDelays(string file)
    {
        using var avatar = AnimatedAvatar.Load(Path.Combine(Folder, file));

        Assert.True(avatar.FrameCount >= 1);
        Assert.Equal(avatar.FrameCount, avatar.Delays.Count);
        Assert.All(avatar.Delays, delay => Assert.True(delay >= AnimatedAvatar.MinimumDelay));
        Assert.Equal(avatar.Delays.Aggregate(TimeSpan.Zero, (a, b) => a + b), avatar.Duration);
        Assert.False(avatar.Content.IsEmpty);
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void EveryFrame_IsRound_AtExactlyTheAskedSize(string file)
    {
        using var avatar = AnimatedAvatar.Load(Path.Combine(Folder, file), new AvatarLook()); // covering: the circle is full

        foreach (var diameter in new[] { 28, 40, 60, 80 })
        {
            for (var i = 0; i < avatar.FrameCount; i++)
            {
                var frame = avatar.Frame(diameter, i);
                Assert.Equal(new Size(diameter, diameter), frame.Size);
                Assert.Equal(0, frame.GetPixel(0, 0).A); // outside the circle
                Assert.Equal(0, frame.GetPixel(diameter - 1, diameter - 1).A);
            }
        }
    }

    [Fact]
    public void AFrameWithoutADelay_ShowsForTheDefaultTime_AsInABrowser()
    {
        using var waiting = AnimatedAvatar.Load(Path.Combine(Folder, "waiting.gif")); // one of its frames says 0 ms

        Assert.Contains(AnimatedAvatar.DefaultDelay, waiting.Delays);
        Assert.DoesNotContain(TimeSpan.Zero, waiting.Delays);
    }

    [Fact]
    public void ThePicture_IsPlacedByItsContent_NotByItsCanvas()
    {
        using var voteNow = AnimatedAvatar.Load(Path.Combine(Folder, "voteNow.gif")); // a small wolf on a 112 x 112 canvas

        Assert.True(voteNow.Content.Width < 112 || voteNow.Content.Height < 112);
        Assert.True(voteNow.Content.Height < voteNow.Content.Width); // the empty space above and below is not counted
    }

    [Fact]
    public void TheFrame_FollowsTheTime_AndLoops()
    {
        using var avatar = AnimatedAvatar.Load(Path.Combine(Folder, "current_1.gif"));
        Assert.True(avatar.IsAnimated);

        Assert.Equal(0, avatar.FrameIndex(TimeSpan.Zero));
        Assert.Equal(0, avatar.FrameIndex(avatar.Delays[0] - TimeSpan.FromMilliseconds(1)));
        Assert.Equal(1, avatar.FrameIndex(avatar.Delays[0]));
        Assert.Equal(avatar.FrameCount - 1, avatar.FrameIndex(avatar.Duration - TimeSpan.FromMilliseconds(1)));
        Assert.Equal(0, avatar.FrameIndex(avatar.Duration)); // round again
        Assert.Equal(1, avatar.FrameIndex(avatar.Duration * 3 + avatar.Delays[0]));
    }

    [Fact]
    public void AFrame_IsRenderedOncePerSize()
    {
        using var avatar = AnimatedAvatar.Load(Path.Combine(Folder, "current_3.gif"));

        Assert.Same(avatar.Frame(40, 2), avatar.Frame(40, 2));
        Assert.NotSame(avatar.Frame(40, 2), avatar.Frame(60, 2));
    }
}

public class AvatarSetTests
{
    [Fact]
    public void Load_FindsThePicturesOfEachOverlay()
    {
        using var avatars = AvatarSet.Load(AnimatedAvatarTests.Folder);

        Assert.Equal(new[] { "current_1", "current_2", "current_3", "current_4", "current_5" }, avatars.Current.Select(a => a.Name));
        Assert.Equal("voteNow", avatars.VoteNow?.Name);
        Assert.Equal("voteAboutToEnd", avatars.VoteAboutToEnd?.Name);
        Assert.Equal("waiting", avatars.Waiting?.Name);
        Assert.Equal("ok", avatars.Ok?.Name);
        Assert.True(avatars.Current.Single(a => a.Name == "current_2").Look.OwnFrame);
        Assert.Same(avatars.VoteAboutToEnd, avatars.ForVote(ending: true));
        Assert.Same(avatars.VoteNow, avatars.ForVote(ending: false));
    }

    [Fact]
    public void NextCurrent_IsRandom_ButNeverTheSameTwiceInARow()
    {
        using var avatars = AvatarSet.Load(AnimatedAvatarTests.Folder, new Random(7));

        var picks = Enumerable.Range(0, 200).Select(_ => avatars.NextCurrent()!.Name).ToList();

        Assert.All(picks.Zip(picks.Skip(1)), pair => Assert.NotEqual(pair.First, pair.Second));
        Assert.Equal(5, picks.Distinct().Count());
    }

    [Fact]
    public void OkPng_IsReadAsAStillPicture_AndIsOptional()
    {
        using var dir = new TempDirectory();
        Assert.Null(AvatarSet.Load(dir.Path).Ok);

        using (var ok = new Bitmap(64, 48))
        using (var g = Graphics.FromImage(ok))
        {
            g.Clear(Color.Transparent);
            g.FillEllipse(Brushes.Green, 8, 4, 48, 40);
            ok.Save(dir.File("ok.png"), System.Drawing.Imaging.ImageFormat.Png);
        }

        using var avatars = AvatarSet.Load(dir.Path);

        Assert.Equal("ok", avatars.Ok?.Name);
        Assert.False(avatars.Ok!.IsAnimated);
        Assert.Equal(new Size(40, 40), avatars.Ok.Frame(40, 0).Size);
    }

    [Fact]
    public void WithoutTheFolder_ThereAreNoAvatars_AndTheIconsStay()
    {
        using var dir = new TempDirectory();

        var avatars = AvatarSet.Load(Path.Combine(dir.Path, "missing"));

        Assert.Empty(avatars.Current);
        Assert.Null(avatars.NextCurrent());
        Assert.Null(avatars.ForVote(ending: true));
        Assert.Null(avatars.Waiting);
    }

    [Fact]
    public void ABrokenPicture_IsLeftOut_TheOthersStay()
    {
        using var dir = new TempDirectory();
        File.Copy(Path.Combine(AnimatedAvatarTests.Folder, "current_1.gif"), dir.File("current_1.gif"));
        File.WriteAllText(dir.File("current_2.gif"), "not a picture");
        File.WriteAllText(dir.File("waiting.gif"), "not a picture either");

        using var avatars = AvatarSet.Load(dir.Path);

        Assert.Equal("current_1", Assert.Single(avatars.Current).Name);
        Assert.Same(avatars.Current[0], avatars.NextCurrent()); // only one: it may come again
        Assert.Null(avatars.Waiting);
        Assert.Null(avatars.VoteNow);
    }
}

public class AvatarAnimationTests
{
    [Fact]
    public void TheAvatar_PlaysOnlyWhileTheOverlayIsVisible_AndMovesOnWithTheClock() => UiContext.Run(() =>
    {
        using var avatars = AvatarSet.Load(AnimatedAvatarTests.Folder);
        var time = new FakeTimeProvider();
        using var form = new Form();
        var pill = new OverlayPill(OverlayStyle.BufferingGlyph, timeProvider: time) { Text = "Buffering" };
        form.Controls.Add(pill);
        form.ShowHidden();

        pill.Avatar = avatars.Waiting;
        Assert.False(pill.IsAvatarClockRunning); // hidden

        pill.Visible = true;
        Assert.True(pill.IsAvatarClockRunning);
        Assert.Equal(0, pill.ShownAvatarFrame);

        time.Advance(avatars.Waiting!.Delays[0]);
        pill.TickAvatar();
        Assert.Equal(1, pill.ShownAvatarFrame);

        pill.Visible = false;
        Assert.False(pill.IsAvatarClockRunning);
    }, sta: true);

    [Fact]
    public void AStillPicture_NeedsNoClock() => UiContext.Run(() =>
    {
        using var dir = new TempDirectory();
        using (var still = new Bitmap(32, 32))
            still.Save(dir.File("waiting.gif"), System.Drawing.Imaging.ImageFormat.Gif);
        using var avatars = AvatarSet.Load(dir.Path);
        using var form = new Form();
        var pill = new OverlayPill(OverlayStyle.BufferingGlyph) { Text = "Buffering" };
        form.Controls.Add(pill);
        form.ShowHidden();

        pill.Avatar = avatars.Waiting;
        pill.Visible = true;

        Assert.False(avatars.Waiting!.IsAnimated);
        Assert.False(pill.IsAvatarClockRunning);
    }, sta: true);

    [Fact]
    public void TheCard_TakesANewPicture_EachTimeItOpens_NotOnEveryNewPosition() => UiContext.Run(() =>
    {
        using var avatars = AvatarSet.Load(AnimatedAvatarTests.Folder);
        var time = new FakeTimeProvider();
        using var form = new Form { Width = 800, Height = 400 };
        var picks = 0;
        var card = new NowPlayingCard(time, layered: true) { NextAvatar = () => { picks++; return avatars.NextCurrent(); } };
        form.Controls.Add(card);
        form.ShowHidden();
        card.AvailableWidth = 600;
        card.BottomLeft = new Point(10, 300);
        var video = new NowPlayingCardContent("Some video", TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(5));
        card.Content = video;

        void Run()
        {
            for (var i = 0; i < 100; i++)
            {
                time.Advance(TimeSpan.FromMilliseconds(10));
                card.Advance();
            }
        }

        card.Showing = true;
        Run();
        var first = card.Avatar;
        Assert.NotNull(first);
        card.Content = video with { Position = TimeSpan.FromSeconds(11) };
        Assert.Equal(1, picks);

        card.Showing = false;
        Run();
        card.Showing = true;
        Run();

        Assert.Equal(2, picks);
        Assert.NotSame(first, card.Avatar);
    }, sta: true);
}
