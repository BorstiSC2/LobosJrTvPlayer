using Microsoft.Extensions.Time.Testing;
using YouTubeVideoPlayer.Tests.TestSupport;
using YouTubeVideoPlayer.Twitch;

namespace YouTubeVideoPlayer.Tests.Twitch;

public class ChatActivityTrackerTests
{
    private static readonly TimeSpan Bucket = TimeSpan.FromMinutes(5);

    private static (ChatActivityTracker Tracker, IChatChannel Chat, FakeTimeProvider Time) Create(TimeSpan? window = null)
    {
        var time = new FakeTimeProvider();
        var chat = Chat.Channel(out _);
        return (new ChatActivityTracker(chat, window ?? TimeSpan.FromMinutes(60), Bucket, time), chat, time);
    }

    private static void Chatters(IChatChannel chat, params string[] users)
    {
        foreach (var user in users)
            chat.Chatter(user);
    }

    [Fact]
    public void BeforeTheFirstBucketIsComplete_CountsTheChattersSoFar_EachUserOnce()
    {
        var (tracker, chat, time) = Create();

        Assert.Equal(0, tracker.AverageUniqueChatters);
        Chatters(chat, "a", "b", "a", "c", "a");
        time.Advance(Bucket - TimeSpan.FromSeconds(1));

        Assert.Equal(3, tracker.AverageUniqueChatters);
    }

    [Fact]
    public void AUserWithoutId_IsCountedByLoginName()
    {
        var (tracker, chat, _) = Create();

        chat.Chatter("a", userId: "");
        chat.Chatter("a", userId: "");
        chat.Chatter("b", userId: "");

        Assert.Equal(2, tracker.AverageUniqueChatters);
    }

    [Fact]
    public void TheAverage_IsTakenOverTheCompletedBuckets()
    {
        var (tracker, chat, time) = Create();

        Chatters(chat, "a", "b", "c", "d");
        time.Advance(Bucket);
        Chatters(chat, "a", "b", "a");
        Assert.Equal(4, tracker.AverageUniqueChatters); // the second bucket is not complete yet

        time.Advance(Bucket);
        Chatters(chat, "x", "y", "z", "w", "v", "u"); // the current bucket does not count

        Assert.Equal(3, tracker.AverageUniqueChatters);
    }

    [Fact]
    public void BucketsNobodyChattedIn_CountAsZero()
    {
        var (tracker, chat, time) = Create();

        Chatters(chat, "a", "b", "c", "d");
        time.Advance(Bucket * 4);

        Assert.Equal(1, tracker.AverageUniqueChatters);
    }

    [Fact]
    public void OldBuckets_DropOutOfTheWindow()
    {
        var (tracker, chat, time) = Create(window: TimeSpan.FromMinutes(10)); // two buckets

        Chatters(chat, "a", "b", "c", "d", "e", "f");
        time.Advance(Bucket);
        Chatters(chat, "a", "b");
        time.Advance(Bucket);
        Assert.Equal(4, tracker.AverageUniqueChatters);

        time.Advance(Bucket);
        Chatters(chat, "q"); // creates a new bucket, which prunes the old ones

        Assert.Equal(1, tracker.AverageUniqueChatters); // (2 + 0) / 2
    }

    [Fact]
    public void AfterDispose_MessagesAreNotCounted()
    {
        var (tracker, chat, _) = Create();

        tracker.Dispose();
        Chatters(chat, "a", "b");

        Assert.Equal(0, tracker.AverageUniqueChatters);
    }
}
