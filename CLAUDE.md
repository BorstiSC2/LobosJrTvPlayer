# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Overview

A Windows Forms app (.NET 10, `net10.0-windows`) that plays videos from YouTube playlists in a loop, in random or playlist order (per playlist).
- YoutubeExplode resolves the streams and playlist contents; the official YouTube API is not used.
- LibVLCSharp plays the streams.
- An optional Twitch chat bot (TwitchLib.Client 4.x) answers `!current`, `!timestamp`, `!skip`, `!shufflestatus`, `!toggleshuffle` and `!token` (remaining token validity, never the token), and runs chat votes for the next video (`!skipvote`, `!vote n`, `!cancelvote`, and the Autovote option).

## Commands

```bash
dotnet build YoutubeVideoPlayer.slnx -c Debug
```

```bash
dotnet run --project YoutubeVideoPlayer/YoutubeVideoPlayer.csproj
```

```bash
dotnet test YoutubeVideoPlayer.Tests/YoutubeVideoPlayer.Tests.csproj
```

```bash
dotnet test YoutubeVideoPlayer.Tests/YoutubeVideoPlayer.Tests.csproj --filter "FullyQualifiedName~PlaybackManagerTests"
```

- The solution is `.slnx` (XML solution format) and needs a recent SDK. At solution level, all configurations map to the `x64` project platform.
- `<Nullable>` is enabled and the build is currently warning-free. Keep it that way.
- There is no linter.

## Tests

`YoutubeVideoPlayer.Tests` uses **xUnit** for assertions and **NSubstitute** for mocks; `FakeTimeProvider` (Microsoft.Extensions.TimeProvider.Testing) controls time. The app project grants `InternalsVisibleTo` to it (used for `Logger.LogFilePath`).

- **Seams for external systems:** `IYouTubeService` (YoutubeExplode), `IMediaPlayer` (LibVLC), `ITwitchClient` (TwitchLib), `ITwitchAuthClient` (Twitch OAuth) and `TimeProvider`. New code that talks to the outside world or waits on time should go through these, so it stays testable.
- **Threading:** manager tests run inside `TestSupport/UiContext.Run(...)`, a single-threaded `SynchronizationContext` that mirrors the UI-thread invariant. Inside it, use `Async.SettleAsync()` / `Async.WaitUntilAsync(...)` to let posted work run, and `FakeTimeProvider.Advance` to fire timers and `Task.Delay`s. A delay that is only scheduled after an earlier one completes needs a second `Advance`.
- **UI tests** use `UiContext.Run(..., sta: true)`. There, posted callbacks go through the Win32 message queue (like `WindowsFormsSynchronizationContext`), so they also run inside modal dialogs.
  - Forms are shown transparent and off-screen with `Ui.ShowHidden()`.
  - Modal forms are answered with `Ui.AnswerNextDialog<T>`, and `MessageBox`es with `Ui.AnswerNextMessageBox`. Both use window messages, never keystrokes or focus, so the user can keep working while the tests run.
  - A ListView only keeps a selection once its window exists, so `ShowHidden()` dialogs before selecting rows.
  - A watchdog closes the test thread's windows after the timeout (30 s), so a hanging test fails instead of leaving a dialog open. Unhandled exceptions in WinForms callbacks fail the test (set in a module initializer).
  - Code that would open a browser (`TwitchLoginDialog.OpenBrowser`) is intentionally not tested.
  - Don't rely on one `Async.SettleAsync()` for work that runs on the thread pool (reconnects, file reads): on a busy machine it is not enough and the test becomes flaky. Wait for the effect with `Async.WaitUntilAsync(...)`.
  - One big `FakeTimeProvider.Advance` fires several ticks of a periodic timer back to back, and they all see the same fake clock. A substitute that the watchdog reads (`IMediaPlayer.TimeMs`) must return a different value on every call, or the video looks frozen.
  - `PlaybackSetup`'s video source hands out the ids from `Queue(...)` on every request, and `NextVideoPrefetcher` asks it 3 s (fake time) after a video started: queued ids are taken earlier than the change itself. `Chat.Channel(...)` reports `IsConnected` as true.
- **Logger tests** redirect the static log file and run in the non-parallel `Logger` collection.
- **Integration tests** (`[IntegrationFact]`: real YouTube) are skipped unless `YTP_INTEGRATION_TESTS=1` is set. `VlcMediaPlayerTests` use the real LibVLC natives but need no network.

## Architecture

`Program.cs` is the composition root. It installs a `WindowsFormsSynchronizationContext` **before** creating the managers, because they capture `SynchronizationContext.Current` in their constructors. It then wires the managers and disposes them after `Application.Run`. The Twitch disconnect runs on the thread pool there, because the message loop is already gone.

**Threading invariant:** all manager state lives on the UI thread.
- LibVLC events, `ContextTimer` ticks (`Infrastructure/`) and TwitchLib callbacks are `Post`ed onto the UI context.
- Playlist downloads and cache reads run in `Task.Run`, and their results are applied after `await` on the UI thread.
- Don't touch manager state from background threads.

**Managers:**
- `Playback/PlaybackManager`: drives an `IMediaPlayer` (`VlcMediaPlayer` owns LibVLC, `MediaPlayer` and `Media`; `Program` passes its `MediaPlayer` to the form's `VideoView`) and resolves videos through `IYouTubeService` (`YouTube/YouTubeService` on YoutubeExplode).
  - `PlayNextAsync` is the single entry point for advancing. It cancels and awaits the previous attempt through `_playNextCancellationTokenSource` / `_playNextTask`, using `ReferenceEquals` guards. Keep this pattern.
  - Streams (`YouTube/StreamSelector`): a video-only stream plus an audio stream via the VLC option `:input-slave=` (audio: an English track first, the video's original one if there are several; else the original track, whatever its language; else the highest bitrate. YouTube offers one track per language, and the automatically dubbed ones often have a higher bitrate than the original, so bitrate alone played translations), with a fallback to the best muxed stream. The video is the tallest picture up to `StreamSelector.MaxHeight` (1440p; 4K and up are too heavy to decode), then the codec that is easiest to decode (H.264, then VP9, then 8-bit AV1), then the highest bitrate. YouTube only offers H.264 up to 1080p, so 1440p is VP9. HDR streams (VP9 profile 2 = `vp9.2`, 10-bit AV1) are only used if nothing else exists, because VLC 3 cannot tone-map them. `YouTubeService` logs the chosen codec, resolution and audio track (language) per video to `errorlog.txt`.
  - `VlcMediaPlayer` starts LibVLC with `--no-drop-late-frames --no-skip-frames` (after a slow start VLC dropped the first, late keyframe, and the frames depending on it showed as a flat grey picture with motion outlines until the next keyframe) and `--no-video-title-show` (the stream URL would be shown as the title). Every media gets `:http-reconnect=true` (a dropped connection must not end a video early).
  - The loop is serialized: after every `await` it looks again for a registered operation, so overlapping callers cannot both start one. Only the operation's own cancellation ends it; an `OperationCanceledException` from an HTTP timeout is a normal failure (`when (token.IsCancellationRequested)`).
  - `NoVideosAvailable` is raised after the operation is unregistered, because its handlers show modal dialogs.
  - Player events are tagged with a media generation when they are raised and dropped if a newer video started meanwhile.
  - `VlcMediaPlayer` runs `Play`/`Stop`/`Dispose` one after the other on the thread pool (libvlc 3 `Stop` can deadlock on the UI thread) and reports a failed start via `EncounteredError`. It turns off the video window's own key/mouse handling.
  - Watchdogs: a buffering timeout (30 s), and a fail-safe check every 20 s that skips after **two** failed checks in a row (not playing, or the position isn't moving). It ignores ticks while an operation is resolving a video and stops when there are no videos.
  - **No gap between videos:** `Playback/NextVideoPrefetcher` picks the next video 3 s after a video started and resolves its streams, because a YouTube lookup takes 2-3 s (measured) and would otherwise be a black screen at every change. The pick is kept (a shuffled playlist would pick again), a queued video wins, and `PlayNextInternalAsync` takes the result with `Take(after, queued)`: no lookup and no 1 s pause (`DelayBeforeNextVideo` is a back-off for the normal path only). It is dropped on `IVideoSource.SequenceChanged` (another playlist, an update, a changed order), `QueueNextVideo`/`ClearQueuedVideo`, a failed lookup, and when the stream URLs (`expire` parameter, about 6 h) run out; a very long video is then looked up at its end as before. Measured with real YouTube and LibVLC: skip to playing 3.2-3.9 s without, 0.4 s with a prepared video.
  - It gets videos through `IVideoSource` (implemented by `PlaylistManager`) and passes the video that is playing (`Current`), so an ordered playlist continues after it. A video that cannot be played moves that reference forward, so the retry does not pick it again. If every video fails, the retry loop continues indefinitely with a 1 s delay.
  - **Opens paused (standby):** `AppCoordinator` calls `PlaybackManager.EnterStandby()` for the first start instead of `PlayNextAsync` (later starts, e.g. after the pool ran empty, play at once). Standby reuses the prefetcher (`NextVideoPrefetcher.StartFirst()`, `after == null`, no 3 s delay): the first video is picked and resolved, so the Play button (`PlayNextAsync`, also a skip) starts it without lookup or wait. `IsStandbyReady` turns true once the first video is looked up (or the lookup failed, so Play is never blocked); until then the Play button is disabled and the title says "Loading first video..." instead of "Ready". `IsStandby`/`IsStandbyReady`/`IsPaused` changes raise `StateChanged`; the control bar's Play/Pause button toggles standby/play → pause ↔ resume. A playlist switch still plays at once. If the preparation failed or its URLs expired, Play falls back to the normal lookup.
- `Playlists/PlaylistManager`:
  - Each playlist has a `Shuffle` flag in `playlists.json` (default `true`, also for entries without the value). Shuffled: `TryGetNextVideoId` picks a random video (not the current one); otherwise the next one in the playlist's order, looping after the last (no/unknown current video: the first). `GetNextVideoIds` returns random videos or the following ones in order, never the current one. All playlists combined (`SelectedKey == null`) always shuffle and `CanChangeShuffle` is false. `IShuffleControl` (implemented by `PlaylistManager`) is what `!shufflestatus` / `!toggleshuffle` use; a failed save restores the old value.
  - `IVideoSource.SequenceChanged` is raised when the pool is rebuilt (selection, update, removal) and when the order of the selected playlist changes; `PlaybackManager` drops its prepared video on it.
  - `playlists.json` holds the titles, links, order (`Shuffle`) and the selected playlist. It is created from the committed seed `playlists.default.json` and then only the runtime copy next to the exe is edited.
  - Video IDs are cached per playlist in `PlaylistCache/<listId>.json`, keyed by the YouTube list ID. YouTube is contacted only when there is no cache yet or on an explicit `UpdateAsync` (always a full re-download; the old cache is kept if it fails).
  - Startup: the selected playlist is loaded first, the rest load sequentially in the background.
  - `_loadTasks` deduplicates concurrent loads of the same playlist.
  - `PoolChanged.SelectionChanged` tells the coordinator whether to skip the current video.
- `AppCoordinator`: startup sequence. It starts playback once the pool is non-empty, and skips on a selection change (after dropping a queued video, which belongs to the old selection). It raises `NoVideosAvailable` only when nothing is loading anymore.
- `Twitch/TwitchChatService` wraps TwitchLib and maps its messages to `ChatCommandContext`, so the commands don't depend on TwitchLib.
- `Twitch/ChatCommandManager` routes commands from an `IChatChannel` to `IChatCommand` implementations in `Twitch/Commands/`.
  - Permission checks: `RequiresModerator` allows moderators, the broadcaster and `TwitchSettings.PrivilegedUsers` (login names or user IDs). Anyone else is ignored silently.
  - The per-command cooldown is shared by all users; a cooldown of zero disables it (and its logging, so `!vote` doesn't flood the log).
  - To add a command, implement `IChatCommand` and register it in `Program.cs`.
- `Twitch/Voting/SkipVoteManager`: one vote at a time.
  - Options (`SkipVoteManager.OptionCount` = 3, not configurable) are IDs from `IVideoSource.GetNextVideoIds` (never the current video; random if shuffled, otherwise the next episodes in order), with titles fetched from YouTube in parallel; unavailable videos are replaced from spare candidates.
  - The announcement is a rules message followed by one message per option, `MessageDelay` (2 s) apart. Voting is open as soon as the options are known; the countdown only starts after the last option message.
  - Votes are keyed by user ID, and a later vote replaces the earlier one.
  - The countdown uses `Task.Delay` on the UI context, with a halfway reminder for votes of 60 s or more.
  - `StartAfterCurrentAsync(duration)` starts a vote about the video *after* the current one (rules and result messages say so); its winner goes to `IPlaybackControl.QueueNextVideo` instead of playing at once.
  - The winner of a regular vote (a random pick on a tie or with no votes) is played through `IPlaybackControl.PlayVideoAsync`, which shares `PlaybackManager`'s cancel-previous-operation guard and falls back to the next video on failure.
- `Twitch/Voting/AutoVoteManager` (the "Autovote" checkbox in the control bar): a periodic `ContextTimer` check; 5 minutes before the end of the current video it starts an after-current vote, once per `NowPlaying` instance, shortened to end `SafetyMargin` before the video does. It does nothing without a known length, while a vote runs, or while `IChatChannel.IsConnected` is false.
- `PlaybackManager.QueueNextVideo` queues one video that `PlayNextInternalAsync` plays before the regular next one. It is only dropped once started or when it cannot be played (a cancelled attempt keeps it); `PlayVideoAsync` replaces it and `AppCoordinator` clears it when the playlist selection changes.
- `UI/VideoPlayerForm` is a view only. All controls are created in code, not through the designer. Fullscreen and borderless logic live in `UI/WindowModeController`.
- `UI/PlaylistDialog`: the first column of the list is a check box column ("Random order", saved per playlist; the "All playlists" row is always checked and cannot be changed). A `ListView` also reports the items it inserts as `ItemChecked` (first unchecked, then checked), so the handler only acts on a state that differs from the saved order and is still there after the notification, and applies it after the event because saving rebuilds the list. The window size and column widths are fixed estimates (no runtime measuring): the playlist title, the column that varies most in length, is the last one, and the status column only shows a symbol (⟳ loading, ✔ loaded, ✖ failed; the reason is only in `errorlog.txt`). The list is multi-select (Shift/Ctrl+click): with several rows only Remove, Update, Update all and Close are enabled, and Remove/Update act on all selected playlists (the "All playlists" row is ignored). Updating several playlists (and "Update all") marks all of them as loading (`_pending`) at the start and gives each its symbol when its own update is done. The dialog is styled by hand: grey form, white list with taller rows (1 x 30 `SmallImageList`) and the Explorer visual style (`SetWindowTheme`), flat bordered buttons. All dialogs show the app icon (`UI/AppIcon`, the `LobosJrTvTwitchIcon.ico` embedded as "app.ico", also the exe's `ApplicationIcon`). "Update all" runs while the video keeps playing: the download happens on the thread pool and the result replaces the playlist's video list in one step on the UI thread, so nothing reads a half-changed list (an update drops a prepared next video; `SequenceChanged` prepares a new one). Double-clicking a row does not play it (the ListView toggles the check box itself); playing is the green "Switch to playlist" button (second from the bottom, above Close) or Enter. Buttons and the button column size themselves from the font (`AutoSize`, `MinimumSize`); fixed pixel heights cut the text off on scaled displays.

**Config and runtime files** (all next to the exe):
- `twitchsettings.json` (gitignored; template in `twitchsettings.example.json`). If it is missing or incomplete, the bot is off.
- `twitchtoken.json` (gitignored): the bot's access and refresh token, created by the in-app sign-in.
- `errorlog.txt` (`Infrastructure/Logger`): cleared on every start, and writes stop once it reaches 5 MB. `Log(message, exception)` writes nothing if `exception` is null.

**Twitch sign-in** (`Twitch/Auth/`), used when `clientId` is set in `twitchsettings.json`:
- `TwitchAuthClient` (behind `ITwitchAuthClient`) talks to `id.twitch.tv/oauth2` as a **public client** (no client secret). It handles the device code flow, refresh and validation.
- `TwitchTokenManager`:
  - It refreshes the token 10 minutes before it expires (access tokens last 4 h) and validates it hourly, which Twitch requires.
  - Refresh tokens are single use, so `Adopt` **saves the new token before using it**.
  - Before refreshing it re-reads `twitchtoken.json`, in case another instance sharing the folder already refreshed.
  - A rejected refresh raises `SignInRequired`; network errors retry after 1 minute. Timers are scheduled by the path that validated or refreshed the token, so a pending retry is not overwritten.
- `TwitchTokenStore` keeps the token **unencrypted** in `twitchtoken.json` next to the exe (gitignored). This is deliberate, so the app folder can be moved between computers.
- `TwitchChatService`: TwitchLib only accepts new credentials while disconnected (`SetConnectionCredentials` throws otherwise). So every `TokenChanged` causes a short disconnect, credential swap and reconnect.
- A failed IRC login triggers one refresh-and-retry before the state becomes `LoginFailed`.
- Chat announcements: the first change to `Connected` posts `ConnectedMessage`, later ones (a lost connection that came back) `ReconnectedMessage`, except the reconnect of a credential swap (`_quietReconnect`), which would otherwise announce itself every ~4 h. A disconnect cannot be announced (nothing can be sent), only the goodbye on close.
- `Twitch/Commands/TokenCommand` (`!token`, moderators) reads `ITokenLifetime.RemainingValidity` (implemented by `TwitchTokenManager`); with a manual token (`null`) it says the time is unknown. `DisposeAsync` posts `GoodbyeMessage` while still connected and waits 500 ms so TwitchLib's send queue can flush; `Program` gives the whole disconnect 3 s.
- `UI/TwitchLoginDialog` runs the device flow. It opens via the clickable Twitch status label in the control bar.

Without `clientId`, the manual `oAuthToken` + `botUsername` from the settings are used, with no renewal.

`<None Remove="PlaylistDownload/**" />` in the csproj excludes an old, local yt-dlp export folder from the project; the app doesn't use it.

## Conventions

- Bump `<AssemblyVersion>` / `<FileVersion>` / `<InformationalVersion>` (the "product version" in the file properties) in the csproj when releasing, and tag the release with the version (`1.0.5`, `2.0.0`, ...).
- Errors during playback are skipped silently and only logged. Message boxes appear only for initialization failures, an empty video pool and failed playlist-dialog actions.
