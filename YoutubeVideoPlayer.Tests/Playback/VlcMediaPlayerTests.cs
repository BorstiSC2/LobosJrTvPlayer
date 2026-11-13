using LibVLCSharp.Shared;
using YouTubeVideoPlayer.Playback;

namespace YouTubeVideoPlayer.Tests.Playback;

/// <summary>Runs against the real LibVLC natives that are copied to the test output; no network needed.</summary>
public class VlcMediaPlayerTests
{
    static VlcMediaPlayerTests() => Core.Initialize();

    [Fact]
    public void NewPlayer_IsIdle()
    {
        using var player = new VlcMediaPlayer();

        Assert.False(player.IsPlaying);
        Assert.Equal(nameof(VLCState.NothingSpecial), player.State);
        Assert.True(player.LengthMs <= 0);
        Assert.NotNull(player.MediaPlayer);
    }

    [Fact]
    public void MuteAndVolume_AreForwardedToLibVlc()
    {
        using var player = new VlcMediaPlayer();

        player.Mute = true;
        player.Volume = 30;

        Assert.Equal(player.MediaPlayer.Mute, player.Mute);
        Assert.Equal(player.MediaPlayer.Volume, player.Volume);
    }

    [Fact]
    public async Task Play_ThenStop_ReleasesTheMedia()
    {
        using var player = new VlcMediaPlayer();
        var file = Path.Combine(Path.GetTempPath(), "ytp-tests-missing-" + Guid.NewGuid().ToString("N") + ".mp4");

        player.Play(file, file + ".audio"); // a missing file only makes VLC report an error asynchronously
        player.Play(file, null);            // replaces the previous media
        player.Stop();
        player.Stop();                      // stopping twice is harmless
        await player.WhenIdleAsync();

        Assert.False(player.IsPlaying);
    }

    [Fact]
    public async Task AfterDispose_CommandsAreIgnored()
    {
        var player = new VlcMediaPlayer();

        player.Dispose();
        player.Dispose(); // idempotent
        player.Play(Path.Combine(Path.GetTempPath(), "missing.mp4"), null);
        player.Stop();
        await player.WhenIdleAsync();

        Assert.False(player.IsPlaying);
        Assert.Equal("Disposed", player.State);
        Assert.Equal(-1, player.TimeMs);
    }

    [Fact]
    public void Dispose_CanBeCalledAfterPlaying()
    {
        var player = new VlcMediaPlayer();
        player.Play(Path.Combine(Path.GetTempPath(), "missing.mp4"), null);

        player.Dispose();
    }

    [Fact]
    public void MissingFile_RaisesEncounteredErrorOrEndReached()
    {
        using var player = new VlcMediaPlayer();
        using var finished = new ManualResetEventSlim();
        player.EncounteredError += (_, _) => finished.Set();
        player.EndReached += (_, _) => finished.Set();

        player.Play(Path.Combine(Path.GetTempPath(), "ytp-tests-missing.mp4"), null);

        Assert.True(finished.Wait(TimeSpan.FromSeconds(10)));
    }
}
