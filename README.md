# YouTube Video Player

A lightweight Windows Forms application for playing YouTube videos from your playlists (in random or playlist order) using **LibVLCSharp** and **YoutubeExplode**. The application streams videos directly from YouTube, supports fullscreen and borderless playback, audio controls, automatically skips unavailable or broken videos, and can run a **Twitch chat bot** that lets viewers query the current video and its position and mods skip videos.

---

## Features

- Playback from YouTube playlists, per playlist in random order or one video after the other
- Playlist dialog to add, rename, remove, update and switch playlists
- Automatic playlist swap: after a set number of days and hours the next playlist (in list order, or a random one) takes over, at the end of the video that is playing then
- Playlists that play in order continue where they left off – after another playlist and after a restart
- Cached video lists per playlist – instant switching, YouTube is only contacted when needed
- Streams directly from YouTube
- Automatic selection of the best video and audio stream (up to 1440p, no HDR, easy-to-decode codecs first; for videos with several audio languages the English track is played, not an automatically dubbed translation)
- No gap between two videos: the next one is chosen and looked up while the current one plays
- Automatic fallback to muxed streams when required
- Fullscreen and borderless mode
- Volume slider, keyboard volume controls and mute
- Buffering indicator and buffering timeout
- Fail-safe watchdog for frozen playback
- Automatic skipping of unavailable or invalid videos
- Video title overlay
- Twitch chat bot with `!current`, `!forceskip`, `!reload`, `!playlist`, `!skipplaylist`, `!playlistshufflestatus`, `!toggleplaylistshuffle` and chat votes for the next video (`!forcevoteskip`, or automatically with the **Autovote** checkbox, which `!autovotestatus` and `!toggleautovote` also report and switch); `!vodcommands` and `!modvodcommands` list what the bot can do
- While a chat vote runs, a ticker at the bottom of the video shows an orange "Vote" (or "Skip this video?") badge and the time left, and scrolls the options with a live bar and count each, the leader highlighted (it cross-fades with the video title, which has the same look, as do the buffering, reconnecting and waiting labels)
- Viewers can ask the chat to skip a video with `!voteskip` (`!skip` / `!stay`); it needs a configurable share of the recent chatters, and a vote to stay blocks it for 30 minutes

---

## Requirements

- .NET 10 SDK (Windows)
- Internet connection

NuGet packages:

- LibVLCSharp, LibVLCSharp.WinForms, VideoLAN.LibVLC.Windows
- YoutubeExplode
- Newtonsoft.Json
- TwitchLib.Client

---

## Playlists

Click **Playlists** in the control bar to open the playlist dialog.

| Action | What it does |
|--------|--------------|
| Switch to playlist (green button, Enter) | Plays the selected playlist (or **All playlists** combined) and closes the dialog |
| Add... | Paste a YouTube playlist link. The name is fetched from YouTube and pre-filled; you can change it before adding |
| Rename... (F2) | Changes the name shown in the list |
| Remove (Del) | Removes the playlist and its cached video list |
| Update | Downloads the playlist's video list from YouTube again and shows how many videos were added/removed |
| Update all | Updates every playlist one after another; the video keeps playing meanwhile. All playlists show ⟳ until their own update is done |
| Select several rows (Shift+click, Ctrl+click) | Only Remove, Update, Update all and Close stay enabled; Remove and Update then apply to all selected playlists |
| Random order (checkbox in the list; a double-click on a row toggles it too, it does not open the playlist) | Switches the playlist between random order and playlist order; saved with the playlist |
| Swap playlists automatically every *n* days *n* hours (switch below the list) | Turns the automatic playlist swap on or off and sets its interval (see below). Needs at least two playlists |
| Pick the next playlist at random (switch below it) | The next playlist (of the automatic swap and of `!skipplaylist`) is a random other one instead of the next one in the list |

Links to a single video that contain `&list=...` are accepted as well.

### Files

All files live next to the executable.

| File | Content |
|------|---------|
| `playlists.json` | The playlists (title, link, order), the selected one and the automatic swap (on/off, interval, last change). Created from `playlists.default.json` on first start |
| `PlaylistCache/<playlist id>.json` | The cached video IDs of one playlist |

Example `playlists.json`:

```json
{
  "selectedPlaylistUrl": "https://www.youtube.com/playlist?list=PL...",
  "playlists": [
    { "title": "Music", "url": "https://www.youtube.com/playlist?list=PL...", "shuffle": true },
    { "title": "Lets Play", "url": "https://www.youtube.com/playlist?list=PL...", "shuffle": false,
      "lastVideoId": "<video id>", "lastVideoIndex": 11, "lastVideoFinished": true }
  ],
  "autoSwap": { "enabled": true, "intervalHours": 168, "randomOrder": false, "lastSwapUtc": "2026-11-13T18:00:00+00:00" }
}
```

`selectedPlaylistUrl: null` plays all playlists combined.

### Automatic playlist swap

Below the list in the **Playlists** dialog, the switch **Swap playlists automatically every [7] days [0] hours** lets the playlists take turns: when the interval is up, the next playlist of the list takes over (after the last one the first; after **All playlists** the first one). With **Pick the next playlist at random** it is a random other playlist instead (never the one that is playing). Playlists that could not be loaded are skipped, and **All playlists** is never swapped to. The line below the switches says when the next swap is due and which playlist comes next ("a random playlist" if there is more than one to pick from).

- The video that is playing when the time is up is **not interrupted**. Half a minute before an **Autovote** would start (5½ minutes before the end with the default `autoVoteLeadTimeSeconds`), the next playlist is selected, so the video after it comes from the next playlist – and so do the options of the Autovote. A video that is shorter than that is followed by the next playlist straight away. While a vote about the next video runs or its winner is waiting, the swap waits for the end of the winner's video.
- While the app is paused before the first **Play**, the next playlist takes over at once and its first video is prepared.
- The interval counts from the **last change**: an automatic swap, a switch by hand (**Switch to playlist**, `!skipplaylist`), or a change of the setting (turning it on or off, another interval). Switching the random order does not start it over, since it does not change when the swap happens; chat can ask for it with `!playlistshufflestatus` and moderators can switch it with `!toggleplaylistshuffle`. The setting and the time of the last change are saved in `playlists.json`, so a restart does not reset the interval; a swap that fell due while the app was closed happens right after the next start.
- It needs at least two playlists; removing playlists until only one is left turns it off.
- When the first video of the new playlist starts and the Twitch bot is connected, the bot posts `New Playlist has begun: <name>` – after an automatic swap as well as after a switch by hand or `!skipplaylist`.

### Random or in order

Every playlist has its own order, saved as `shuffle` in `playlists.json` (playlists without the value are shuffled, as before):

- **Random order** (default): the next video is picked at random, never the one that just ended.
- **In order**: the videos play one after the other as in the YouTube playlist, and start over after the last one. **Next** and `!forceskip` only go to the next video, and so does the end of a video. The playlist **continues where it left off** when it plays again – after another playlist, or after a restart of the app: with the video after the one that last played to its end, or, if that video was cut off (another playlist was selected, the app was closed), with the same video again from its start. The first time, it starts with the first video. Where it is, is saved with the playlist in `playlists.json`; if an update removed that video, the playlist continues at the same position.

Change it in the **Playlists** dialog (the **Random order** column of the list has a checkbox for each playlist: checked = random, unchecked = in order) or with the chat commands `!shufflestatus` / `!toggleshuffle`. **All playlists** combined always plays in random order, because there is no common order.

### Loading behaviour

- On startup the selected playlist is loaded first. The app opens **paused**: the first video is already looked up, and the **Play** button in the control bar starts it without delay (the same button pauses and continues the video later). It is read from its cache; only a playlist that has never been loaded is downloaded from YouTube.
- All other playlists are loaded in the background one after another, so switching later is instant.
- While a video plays, the next one is chosen and looked up in advance, so the change between two videos takes well under a second instead of several seconds of black screen. The choice is kept, so the video that was prepared is the one that plays.
- Video lists are only downloaded again when you click **Update** / **Update all**. An update always fetches the complete list; if it fails, or if YouTube answers with no videos at all, the previous list stays in use.

---

## Twitch bot

The bot joins a Twitch channel's chat and reacts to these commands:

| Command | Who | What it does |
|---------|-----|--------------|
| `!current` | everyone | Replies `Now playing: "<title>" – 03:21 / 10:45 (07:24 left) – https://www.youtube.com/watch?v=<id>` (only the position if the length is unknown) |
| `!forceskip` | privileged | Skips to the next video at once, without a vote (the next one in order, or a random one if the playlist is shuffled) |
| `!reload` | privileged | Gets fresh stream links for the current video and continues it 3 seconds before where it is (a safety measure when picture or sound misbehave; the video shows "Reloading - continuing at 50:28" meanwhile) |
| `!pause` | privileged | Pauses the current video |
| `!play` | privileged | Continues a paused video, or starts the first video while the player still waits for the first Play |
| `!shufflestatus` | everyone | Tells whether the current playlist is shuffled (random order) or plays in playlist order |
| `!toggleshuffle` | privileged | Switches the current playlist between random and playlist order, saves it and posts the new status |
| `!playlist` | everyone | Replies `Current playlist: "<name>" (shuffled)` or `(in playlist order)` |
| `!skipplaylist` | privileged | Switches to the next playlist now (the current video is skipped); the bot then announces `New Playlist has begun: <name>` |
| `!playlistshufflestatus` | everyone | Tells whether the next playlist is picked at random or the playlists take turns in the order of the list |
| `!toggleplaylistshuffle` | privileged | Switches between a random next playlist and the order of the list (like the switch in the playlist dialog) and posts the new value |
| `!autovotestatus` | everyone | Tells whether Autovote is on (a vote for the next video starts by itself 5 minutes, or `autoVoteLeadTimeSeconds`, before a video ends) |
| `!toggleautovote` | privileged | Switches Autovote on or off (like the checkbox in the control bar, which follows) and posts the new value |
| `!vodcommands` | everyone | Lists the commands everyone may use. Built from the registered commands, so new commands appear automatically |
| `!modvodcommands` | privileged | Lists the commands only moderators, the broadcaster and privileged users may use (also built automatically) |
| `!token` | privileged | Tells how long the bot's current access token stays valid (the token itself is never posted) |
| `!voteskip` | everyone | Asks the chat whether the current video is skipped: a 1-minute vote with `!skip` and `!stay` (see [Vote to skip](#vote-to-skip)) |
| `!skip` / `!stay` | everyone | Ballots in a running `!voteskip` vote (one per user; voting again replaces it). They do nothing otherwise |
| `!voteinfo` | everyone | Tells whether a vote is running and repeats it: the options and the time left, or the `!skip` / `!stay` standing (cooldown 30 seconds, `voteInfoCooldownSeconds`) |
| `!forcevoteskip` | privileged | Starts a vote: the bot posts 3 videos from the current playlist (random ones, or the next episodes if it plays in order); 2 minutes after the last option the winner plays |
| `!vote <n>` | everyone | Votes for option *n* of the running vote (one vote per user; voting again replaces it) |
| `!cancelvote` | privileged | Cancels the running vote (a `!voteskip` vote too); the current video keeps playing |

**Privileged** means moderators, the broadcaster and the users listed in `privilegedUsers` (see below). Everybody else is ignored silently for these commands.

`!current`, `!forceskip`, `!reload`, `!play`, `!pause`, `!shufflestatus`, `!toggleshuffle`, `!playlist`, `!skipplaylist`, `!playlistshufflestatus`, `!toggleplaylistshuffle`, `!autovotestatus`, `!toggleautovote`, `!vodcommands`, `!modvodcommands`, `!voteskip` and `!token` have a short cooldown to stay within Twitch's chat rate limits (the `…CooldownSeconds` settings in `twitchsettings.json`, see below). `!vodcommands` does not list the ballots `!vote`, `!skip` and `!stay`; the votes name them.

The bot also announces itself: it posts a message when it first connects, another one when it is back after a lost connection, and a goodbye when the app closes while it is connected. Renewing the access token (a brief reconnect every few hours) stays silent.

### Skip votes

- The bot first posts a message with the rules, then one message per option (`1) <title> – vote with !vote 1`), two seconds apart. Three videos are offered: random ones from a shuffled playlist, or the next three episodes in order (starting over after the last one). A playlist with fewer other videos offers only those, and at least two are needed.
- Voting is open from the very first message, but the countdown only starts once the last option has been posted.
- While the vote runs, the current video keeps playing. Halfway through, the bot posts the current standings.
- When the time is up, the option with the most votes plays. A tie is broken randomly, and if nobody voted, one of the options is picked randomly. The bot announces the result in chat.
- The options never include the video that is currently playing. Unavailable videos are replaced automatically.
- Only one vote can run at a time.

### Vote to skip

Any viewer can ask the chat to skip the current video with `!voteskip`:

- The bot announces a vote of 1 minute (`voteSkipDurationSeconds`): type `!skip` or `!stay`. The viewer who started it counts as `!skip`. Every user has one ballot; voting again replaces it. Halfway through, the bot posts the standing.
- Skipping needs **more `!skip` than `!stay`** and **at least 75 % (`voteSkipPercent`) of the recent chatters** as `!skip` votes, rounded up and at least one. The announcement says how many that is. "Recent chatters" is a rolling average: the bot counts the different users who wrote anything in chat per 5 minutes (`chatterAverageBucketMinutes`) and averages these counts over the last 60 minutes (`chatterAverageWindowMinutes`). Periods nobody wrote in count as zero; the time before the app started does not count. The number is taken when the vote starts.
- If skipping wins, the regular vote for the next video (the 3 options, as with `!forcevoteskip`) starts at once. If that vote cannot start (too few videos), the video is skipped directly.
- If the chat stays (or `!skip` won without enough votes, or it is a tie), the video keeps playing and `!voteskip` cannot be used for 30 minutes (`voteSkipCooldownMinutes`). The bot says when it can be used again.
- A vote that is interrupted has no result and starts no cooldown: when the video changes meanwhile, or a moderator ends it with `!cancelvote`.
- Only one vote runs at a time: `!voteskip` is refused while a vote for the next video runs, `!forcevoteskip` while a `!voteskip` vote runs, and the Autovote waits until it is over. Nothing plays (the player waits for the first Play or for a vote): no `!voteskip`.

### Autovote

The **Autovote** checkbox in the control bar (enabled when the Twitch bot is on) starts a vote automatically 5 minutes before the current video ends (`autoVoteLeadTimeSeconds` in `twitchsettings.json` changes that). Chat can ask for its state with `!autovotestatus` and moderators can switch it with `!toggleautovote`; the checkbox follows such a change.

- The vote is about the video that plays **after** the current one. The rules message says so ("Vote for the video that plays AFTER the current one ...") and so does the result ("It plays after the current video.").
- The winner is queued and starts when the current video ends; the current video is not interrupted. If the video is skipped before it ends, the queued winner plays next.
- The vote is shortened if needed so that it is over (with a safety margin) before the video ends; the time it takes to look up the options counts too. Videos without a known length (live streams) get no vote.
- A video too short for a 2-minute vote (or for the configured vote length, if that is shorter) still gets a vote of that length, and the next video waits for it: when the short video is over, the screen goes black with "Waiting for vote to finish... 1:23" counting down every second, and the winner starts as soon as the vote ends. The rules message says so ("The next video starts when voting has ended."). **Next** or `!forceskip` starts the next video at once (the winner then plays after it), and `!cancelvote` ends the wait.
- There is one vote per video, and nothing happens while the bot is not connected to the chat or another vote is running. Switching playlists drops a queued winner.
- Unchecking the box only prevents new votes; a vote that is already running finishes. The box starts unchecked on every start.

### Setup

#### 1. Create the bot account

Sign up for a separate Twitch account (log out of your main account or use a private browser window). Its name is what appears in chat. Some channels only let accounts with a verified email or phone number chat.

#### 2. Register an application (one-time)

Twitch only issues tokens to registered applications. Any account can register it; the account needs two-factor authentication enabled.

1. Open the [Twitch developer console](https://dev.twitch.tv/console) → **Applications** → **Register Your Application**.
2. Name: anything; OAuth Redirect URL: `http://localhost:3000`; Category: **Chat Bot**; Client Type: **Public**.
3. Copy the **Client ID**.

#### 3. Configure the app

Copy `YoutubeVideoPlayer/twitchsettings.example.json` to `YoutubeVideoPlayer/twitchsettings.json` and fill it in:

```json
{
  "enabled": true,
  "clientId": "your_application_client_id",
  "botUsername": "your_bot_account",
  "oAuthToken": "",
  "channel": "your_channel",
  "otherCommandsCooldownSeconds": 5,
  "currentCooldownSeconds": 5,
  "skipCooldownSeconds": 3,
  "shuffleCooldownSeconds": 3,
  "skipVoteDurationSeconds": 120,
  "autoVoteLeadTimeSeconds": 300,
  "voteSkipDurationSeconds": 60,
  "voteSkipPercent": 75,
  "voteSkipCooldownMinutes": 30,
  "chatterAverageWindowMinutes": 60,
  "chatterAverageBucketMinutes": 5,
  "voteInfoCooldownSeconds": 30,
  "privilegedUsers": [
    "your_login_name",
    "123456789"
  ]
}
```

- `clientId`: the Client ID from step 2.
- `channel`: the channel to join.
- `botUsername`: optional; if set, the sign-in warns when you log in with a different account.
- `oAuthToken`: leave empty (only for the manual token, see below).
- `privilegedUsers`: login names or numeric Twitch user IDs that may use the privileged commands without being a moderator, e.g. your own account for testing. User IDs keep working if an account is renamed.
- `currentCooldownSeconds` (default 5): the cooldown of `!current`, `!playlist` and `!voteskip`.
- `skipCooldownSeconds` (default 3): the cooldown of `!forceskip`, `!reload`, `!play`, `!pause` and `!skipplaylist`.
- `shuffleCooldownSeconds` (default 3): the cooldown of `!shufflestatus`, `!toggleshuffle`, `!playlistshufflestatus` and `!toggleplaylistshuffle`.
- `otherCommandsCooldownSeconds` (default 5): the cooldown of each command without a setting of its own (`!autovotestatus`, `!toggleautovote`, `!token`, `!vodcommands`, `!modvodcommands`). It replaces `timestampCooldownSeconds`, which is no longer read: rename it in an existing `twitchsettings.json`, otherwise the default applies.
- `skipVoteDurationSeconds`: 15 to 1800; counted from the moment the last option has been posted.
- `autoVoteLeadTimeSeconds`: 60 to 3600 (default 300); how long before the end of a video the Autovote starts its vote. The automatic playlist swap happens 30 seconds earlier.
- `voteSkipDurationSeconds`: 15 to 600 (default 60); how long a `!voteskip` vote runs.
- `voteSkipPercent`: 1 to 100 (default 75); the share of the recent chatters that has to vote `!skip`.
- `voteSkipCooldownMinutes`: 1 to 1440 (default 30); how long `!voteskip` is blocked after the chat chose to stay.
- `chatterAverageWindowMinutes`: 5 to 1440 (default 60); how far back the average of recent chatters reaches.
- `chatterAverageBucketMinutes`: 1 to 60 (default 5), at most the window; the period in which the different chatters are counted.
- `voteInfoCooldownSeconds`: 30 to 3600 (default 30); the minimum time between two `!voteinfo` replies.

`twitchsettings.json` is ignored by git and copied to the output directory on build. Without it (or with `"enabled": false`) the bot is off and the player works as usual.

#### 4. Sign the bot in (once)

1. Start the app. The control bar shows **"Twitch: not signed in – click here to sign in"**. Click it.
2. Click **Open activation page**. In the browser, **log in to Twitch with the bot account** (not your own), check that the code matches and click **Authorize**.
3. The dialog shows "Signed in as ..." and the bot joins the chat.

From then on the token is renewed automatically (every few hours, plus an hourly check that Twitch requires). The sign-in is stored unencrypted in `twitchtoken.json` next to the executable, so the app folder can be moved to another computer. Keep that file private: it lets anyone chat as the bot. It can be revoked on twitch.tv under Settings → Connections.

You only have to sign in again if the app has not run for more than 30 days, if the authorization was revoked, or after switching accounts. In these cases the control bar shows "Twitch: not signed in" or "Twitch: login failed" – click it to sign in again.

**Several computers:** each renewal makes the previous refresh token invalid. If copies of the app run on several computers at the same time, sign in on each computer separately (delete a copied `twitchtoken.json` first). Moving the app to another computer works without a new sign-in, as long as the current `twitchtoken.json` moves along (an older copy left behind will need a new sign-in). If several computers share the same folder (e.g. a synced folder), the app picks up the token that another instance renewed.

#### 5. Make the bot a moderator (recommended)

Type `/mod <botname>` in your chat. The bot does not need it for its commands (permissions are checked for the *sender*), but as a moderator it may send more messages and is not affected by slow mode, follower-only mode or chat verification.

#### Alternative: manual token (no automatic renewal)

Leave `clientId` empty and set `botUsername` and `oAuthToken` instead. Get the token while logged in as the bot account by opening `https://id.twitch.tv/oauth2/authorize?response_type=token&client_id=YOUR_CLIENT_ID&redirect_uri=http://localhost:3000&scope=chat:read+chat:edit`, clicking **Authorize** and copying the `access_token` value from the address bar (the page itself shows an error, which is expected). Enter it as `oauth:<token>`. Such tokens expire after about 60 days and have to be replaced by hand.

---

## Playback Behaviour

When a video is selected:

1. The next video is chosen: a random one, or the next one in playlist order (see *Random or in order*). A queued vote winner goes first.
2. Its metadata and streams are looked up. Normally this already happened while the previous video was playing, so it costs no time.
3. The streams are selected (see below).
4. Playback starts.
5. The video title is displayed in the lower-left corner.

If a video cannot be played, the next one is attempted automatically, without an error dialog.

### Stream selection

- **Video:** the tallest picture up to 1440p (4K and above are too heavy to decode), then the codec that is easiest to decode (H.264, then VP9, then 8-bit AV1), then the highest bitrate. HDR streams are only used if nothing else exists, because VLC 3 shows them with washed-out colours.
- **Audio:** a video with several audio languages plays its English track (the original one if there are several). Without an English track the video's original track plays, instead of an automatically dubbed translation. Videos with a single track play with the highest bitrate.
- Videos without separate video and audio streams fall back to the best combined stream.
- The chosen codec, resolution and audio track of every video are written to `errorlog.txt`.

### Buffering and fail-safe

- While buffering, a centered, animated indicator is shown.
- If buffering exceeds **30 seconds**, the next video starts.
- Every **20 seconds** a watchdog checks that the player is playing and the position advances. If two checks in a row fail (e.g. a frozen stream after a disconnect), the next video starts.

---

## User Interface

- **Video area** – double-click toggles fullscreen.
- **Control bar** – mute button, volume slider, Twitch status, Autovote checkbox, Playlists, Next, Fullscreen, Borderless.
- **Overlays** – title (lower left), volume (upper right), buffering (center).

### Keyboard Shortcuts

| Key | Action |
|------|--------|
| F11 | Toggle fullscreen |
| Esc | Exit fullscreen / borderless |
| + | Increase volume |
| - | Decrease volume |
| M | Toggle mute |

### Fullscreen and borderless

- **Fullscreen** removes the border, maximizes the window, hides the control bar and keeps the window on top.
- **Borderless** shrinks the window to the video area without border and control bar – useful as a capture source.

Leaving either mode restores the previous window state.

---

## Logging

Errors and playback events (including the chosen streams of every video) are written to `errorlog.txt` next to the executable. The log is cleared on every start and stops growing at 5 MB.

---

## Project Structure

```
YoutubeVideoPlayer/
├── Program.cs              Composition root: creates and wires the managers
├── AppCoordinator.cs       Startup sequence; starts/switches playback when the playlist pool changes
├── Playback/               PlaybackManager (buffering timeout, fail-safe), NextVideoPrefetcher (no gap between videos), VlcMediaPlayer (LibVLC)
├── YouTube/                YouTubeService (YoutubeExplode), stream selection (video quality, audio language)
├── Playlists/              PlaylistManager, playlists.json store, per-playlist cache, PlaylistSwapScheduler (automatic swap)
├── Twitch/                 TwitchChatService, ChatCommandManager, Commands/, Voting/, Auth/ (sign-in)
├── UI/                     Main form (view only), window modes, dialogs
└── Infrastructure/         Logger, JSON file helpers, UI-thread timer

YoutubeVideoPlayer.Tests/   xUnit + NSubstitute tests
```

---

## Tests

```bash
dotnet test YoutubeVideoPlayer.Tests/YoutubeVideoPlayer.Tests.csproj
```

The tests run offline. YouTube, VLC, Twitch and the clock are replaced by substitutes, so even timeouts of minutes or hours run instantly. A few UI tests briefly open transparent, off-screen windows. Tests against the real YouTube are skipped by default; to run them, set `YTP_INTEGRATION_TESTS=1` (PowerShell: `$env:YTP_INTEGRATION_TESTS = "1"`).

---

## Notes

The application does not use the official YouTube API. Streams and playlists are resolved dynamically through YoutubeExplode and played using LibVLC.
