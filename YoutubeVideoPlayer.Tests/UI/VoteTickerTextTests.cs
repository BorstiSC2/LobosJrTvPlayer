using YouTubeVideoPlayer.Twitch.Voting;
using YouTubeVideoPlayer.UI;

namespace YouTubeVideoPlayer.Tests.UI;

public class VoteTickerTextTests
{
    [Fact]
    public void NumberedOptions_WithCountsAndTimeLeft()
    {
        var display = new VoteDisplay(
            "VOTE NOW for the next video!",
            new[]
            {
                new VoteDisplayOption("1) Title A", "!vote 1", 4),
                new VoteDisplayOption("2) Title B", "!vote 2", 1),
                new VoteDisplayOption("3) Title C", "!vote 3", 0)
            },
            null,
            TimeSpan.FromSeconds(80));

        Assert.Equal(
            "VOTE NOW for the next video!   1) Title A (4)  ·  2) Title B (1)  ·  3) Title C (0)   —   type !vote 1-3   ·   1:20 left",
            VoteTickerText.Format(display));
    }

    [Fact]
    public void WhileTheOptionsArePosted_NoTimeIsShown()
    {
        var display = new VoteDisplay(
            "VOTE NOW!", new[] { new VoteDisplayOption("1) A", "!vote 1", 0), new VoteDisplayOption("2) B", "!vote 2", 0) }, null, null);

        Assert.Equal("VOTE NOW!   1) A (0)  ·  2) B (0)   —   type !vote 1-2", VoteTickerText.Format(display));
    }

    [Fact]
    public void SkipAndStay_WithTheNote()
    {
        var display = new VoteDisplay(
            "VOTE NOW: skip \"Boring video\"?",
            new[] { new VoteDisplayOption("Skip", "!skip", 3), new VoteDisplayOption("Stay", "!stay", 1) },
            "needs 4 !skip votes",
            TimeSpan.FromSeconds(45));

        Assert.Equal(
            "VOTE NOW: skip \"Boring video\"?   Skip (3)  ·  Stay (1)   —   type !skip / !stay   ·   needs 4 !skip votes   ·   0:45 left",
            VoteTickerText.Format(display));
    }
}
