using YouTubeVideoPlayer.Twitch.Voting;
using YouTubeVideoPlayer.UI;

namespace YouTubeVideoPlayer.Tests.UI;

public class VoteTickerTests
{
    private static VoteDisplay ThreeOptions(VoteKind kind, TimeSpan? left, params int[] votes) => new(
        kind,
        votes.Select((v, i) => new VoteDisplayOption($"{i + 1}", "Title " + (char)('A' + i), $"!vote {i + 1}", v)).ToList(),
        left);

    [Fact]
    public void AVoteForTheNextVideo_HasTheVoteBadge_TheTime_AndTheOptionsWithTheirShare()
    {
        var ticker = VoteTicker.From(ThreeOptions(VoteKind.NextVideo, TimeSpan.FromSeconds(80), 4, 1, 0));

        Assert.Equal(OverlayStyle.VoteGlyph, ticker.Icon);
        Assert.Equal("Vote", ticker.Label);
        Assert.Equal("1:20", ticker.Time);
        Assert.Null(ticker.Lead);
        Assert.Equal(
            new[]
            {
                new TickerItem("1", "Title A", 0.8, "4", true),
                new TickerItem("2", "Title B", 0.2, "1", false),
                new TickerItem("3", "Title C", 0.0, "0", false)
            },
            ticker.Items);
        Assert.Equal("!vote 1–3", ticker.Hint);
        Assert.Equal("Vote 1:20  |  1 Title A (4)  ·  2 Title B (1)  ·  3 Title C (0)  ·  !vote 1–3", ticker.PlainText);
    }

    [Fact]
    public void WithoutVotes_NoOptionLeads_AndATieHighlightsAllLeaders()
    {
        Assert.DoesNotContain(VoteTicker.From(ThreeOptions(VoteKind.NextVideo, null, 0, 0, 0)).Items, i => i.Leading);
        Assert.Equal(
            new[] { true, true, false },
            VoteTicker.From(ThreeOptions(VoteKind.NextVideo, null, 2, 2, 1)).Items.Select(i => i.Leading));
    }

    [Fact]
    public void WhileTheOptionsArePosted_NoTimeIsShown() =>
        Assert.Null(VoteTicker.From(ThreeOptions(VoteKind.NextVideo, null, 0, 0, 0)).Time);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheTicker_IsEnding_WhenTheVoteSaysSo(bool ending)
    {
        var display = ThreeOptions(VoteKind.NextVideo, TimeSpan.FromSeconds(30), 1, 0, 0) with { Ending = ending };

        Assert.Equal(ending, VoteTicker.From(display).Ending);
    }

    [Fact]
    public void AVote_IsComplete_OnceItsOptionsAreKnown_EvenBeforeItsTime()
    {
        Assert.False(VoteTicker.IsComplete(new VoteDisplay(VoteKind.NextVideo, Array.Empty<VoteDisplayOption>(), null)));
        Assert.True(VoteTicker.IsComplete(ThreeOptions(VoteKind.NextVideo, null, 0, 0, 0)));
        Assert.True(VoteTicker.IsComplete(ThreeOptions(VoteKind.VideoAfterCurrent, TimeSpan.FromMinutes(1), 0, 0, 0)));
    }

    [Fact]
    public void TheAutovote_SaysItIsAboutTheVideoAfterThisOne() =>
        Assert.Equal("!vote 1–3 for the video after this one", VoteTicker.From(ThreeOptions(VoteKind.VideoAfterCurrent, null, 0, 0, 0)).Hint);

    [Fact]
    public void ASkipVote_ShowsTheVideo_AndMeasuresBothSidesAgainstTheVotesNeeded()
    {
        var display = new VoteDisplay(
            VoteKind.Skip,
            new[] { new VoteDisplayOption(null, "!skip", "!skip", 3), new VoteDisplayOption(null, "!stay", "!stay", 1) },
            TimeSpan.FromSeconds(45),
            "Boring video",
            4);

        var ticker = VoteTicker.From(display);

        Assert.Equal(OverlayStyle.SkipGlyph, ticker.Icon);
        Assert.Equal("Skip this video?", ticker.Label);
        Assert.Equal("0:45", ticker.Time);
        Assert.Equal("\"Boring video\"", ticker.Lead);
        Assert.Equal(
            new[] { new TickerItem(null, "!skip", 0.75, "3 of 4", true), new TickerItem(null, "!stay", 0.25, "1", false) },
            ticker.Items);
        Assert.Equal("Type !skip or !stay", ticker.Hint);
    }

    [Fact]
    public void ASkipVoteBeyondItsGoal_HasAFullBar()
    {
        var display = new VoteDisplay(
            VoteKind.Skip,
            new[] { new VoteDisplayOption(null, "!skip", "!skip", 6), new VoteDisplayOption(null, "!stay", "!stay", 6) },
            null,
            "Video",
            4);

        var items = VoteTicker.From(display).Items;

        Assert.All(items, i => Assert.Equal(1.0, i.Share));
        Assert.All(items, i => Assert.False(i.Leading)); // a tie
    }
}
