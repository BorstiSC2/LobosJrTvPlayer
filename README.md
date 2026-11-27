# YouTube Video Player

A lightweight Windows Forms application for playing YouTube videos from your playlists (in random or playlist order) using **LibVLCSharp** and **YoutubeExplode** or **yt-dlp**. The application streams videos directly from YouTube, supports fullscreen and borderless playback, audio controls, automatically skips unavailable or broken videos, and can run a **Twitch chat bot** that lets viewers query the current video and its position and mods skip videos.

---

## Features

- Playback from YouTube playlists, per playlist in random order or one video after the other
- Playlist dialog to add, rename, remove, update and switch playlists
- Automatic playlist swap: after a set number of days and hours the next playlist (in list order, or a random one) takes over, at the end of the video that is playing then
- Playlists that play in order continue where they left off – after another playlist and after a restart
- Cached video lists per playlist – instant switching, YouTube is only contacted when needed
- Streams directly from YouTube
- Two ways to look videos up: the built-in YoutubeExplode or yt-dlp.exe, each falling back to the other when a lookup fails (see *YouTube backend*)
- Automatic selection of the best video and audio stream (up to 1440p, no HDR, easy-to-decode codecs first; for videos with several audio languages the English track is played, not an automatically dubbed translation)
- No gap between two videos: the next one is chosen and looked up while the current one plays
- Automatic fallback to muxed streams when required
- Fullscreen and borderless mode
- Volume slider, keyboard volume controls and mute
- Buffering indicator and buffering timeout
- Fail-safe watchdog for frozen playback
- Automatic skipping of unavailable or invalid videos
- "Now playing" card: a see-through pill with the video's title, position and a progress ring opens at the bottom left for 12 seconds when a video starts and every 10 minutes after that (and when someone types `!current` in chat); the rest of the time the picture stays clear. It shares its place with the status and vote ticker, one at a time: a vote that starts while the card is up appears once the card's time is over, and `!current` during a vote does not bring up the card
- Twitch chat bot with `!current`, `!forceskip`, `!reload`, `!playlist`, `!skipplaylist`, `!playlistshufflestatus`, `!toggleplaylistshuffle` and chat votes for the next video (`!forcevoteskip`, or automatically with the **Autovote** checkbox, which `!autovotestatus` and `!toggleautovote` also report and switch); `!vodcommands` and `!modvodcommands` list what the bot can do
- While a chat vote runs, a ticker at the bottom of the video scrolls the options with a live bar and count each, the leader highlighted, and shows "Vote" (or "Skip this video?"), the time left and what to type below them
- Viewers can ask the chat to skip a video with `!voteskip` (`!skip` / `!stay`); it needs a configurable share of the recent chatters, and a vote to stay blocks it for 30 minutes

---

## Requirements

- .NET 10 SDK (Windows)
- Internet connection
- Optional: [yt-dlp](https://github.com/yt-dlp/yt-dlp/releases) and [Deno](https://github.com/denoland/deno/releases), for the yt-dlp backend (see *YouTube backend*)

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
| `appsettings.json` | Which backend looks videos up on YouTube (see *YouTube backend*). Created with the defaults on first start |

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

- The video that is playing when the time is up is **not interrupted**. Half a minute before an **Autovote** would start (7 minutes 10 seconds before the end with the default `autoVote.leadTimeSeconds`), the next playlist is selected, so the video after it comes from the next playlist – and so do the options of the Autovote. A video that is shorter than that is followed by the next playlist straight away. While a vote about the next video runs or its winner is waiting, the swap waits for the end of the winner's video.
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
| `!current` | everyone | Replies `Now playing: "<title>" – 03:21 / 10:45 (07:24 left) – https://www.youtube.com/watch?v=<id>` (only the position if the length is unknown), and brings up the "now playing" card on stream |
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
| `!autovotestatus` | everyone | Tells whether Autovote is on (a vote for the next video starts by itself 400 seconds, or `autoVote.leadTimeSeconds`, before a video ends) |
| `!toggleautovote` | privileged | Switches Autovote on or off (like the checkbox in the control bar, which follows) and posts the new value |
| `!vodcommands` | everyone | Lists the commands everyone may use. Built from the registered commands, so new commands appear automatically |
| `!modvodcommands` | privileged | Lists the commands only moderators, the broadcaster and privileged users may use (also built automatically) |
| `!token` | privileged | Tells how long the bot's current access token stays valid (the token itself is never posted) |
| `!voteskip` | everyone | Asks the chat whether the current video is skipped: a 1-minute vote with `!skip` and `!stay` (see [Vote to skip](#vote-to-skip)) |
| `!skip` / `!stay` | everyone | Ballots in a running `!voteskip` vote (one per user; voting again replaces it). They do nothing otherwise |
| `!voteinfo` | everyone | Tells whether a vote is running and repeats it: the options and the time left, or the `!skip` / `!stay` standing (cooldown 30 seconds, `commandCooldownSeconds.voteInfo`) |
| `!forcevoteskip` | privileged | Starts a vote: the bot posts 3 videos from the current playlist (random ones, or the next episodes if it plays in order); 2 minutes after the last option the winner plays |
| `!vote <n>` | everyone | Votes for option *n* of the running vote (one vote per user; voting again replaces it) |
| `!cancelvote` | privileged | Cancels the running vote (a `!voteskip` vote too); the current video keeps playing |

**Privileged** means moderators, the broadcaster and the users listed in `privilegedUsers` (see below). Everybody else is ignored silently for these commands.

`!current`, `!forceskip`, `!reload`, `!play`, `!pause`, `!shufflestatus`, `!toggleshuffle`, `!playlist`, `!skipplaylist`, `!playlistshufflestatus`, `!toggleplaylistshuffle`, `!autovotestatus`, `!toggleautovote`, `!vodcommands`, `!modvodcommands`, `!voteskip` and `!token` have a short cooldown to stay within Twitch's chat rate limits (`commandCooldownSeconds` in `twitchsettings.json`, see below; `!forceskip`, `!skipplaylist`, `!reload`, `!play` and `!pause` share one). `!vodcommands` does not list the ballots `!vote`, `!skip` and `!stay`; the votes name them.

The bot also announces itself: it posts a message when it first connects, another one when it is back after a lost connection, and a goodbye when the app closes while it is connected. Renewing the access token (a brief reconnect every few hours) stays silent.

### Skip votes

- The bot first posts a message with the rules, then one message per option (`1) <title> – vote with !vote 1`), two seconds apart. Three videos are offered: random ones from a shuffled playlist, or the next three episodes in order (starting over after the last one). A playlist with fewer other videos offers only those, and at least two are needed.
- Voting is open from the very first message, but the countdown only starts once the last option has been posted.
- While the vote runs, the current video keeps playing. Halfway through, the bot posts the current standings.
- When the time is up, the option with the most votes plays. A tie is broken randomly, and if nobody voted, one of the options is picked randomly. The bot announces the result in chat.
- The options never include the video that is currently playing. Unavailable videos are replaced automatically.
- Only one vote can run at a time.
- If the winner cannot be played (YouTube does not deliver it, or it stops by itself within its first 30 seconds), the next regular video plays, and the chat votes again right away: a shorter vote (45 seconds, `revote.durationSeconds`) that says which video failed and leaves it out. Its winner plays at once. If that winner fails too, there is one more revote; after that the regular video stays. There is no revote while another vote runs or the chat is not connected.
- When the current video ends while the winner is still being loaded, the player waits for the winner instead of starting another video.

### Vote to skip

Any viewer can ask the chat to skip the current video with `!voteskip`:

- The bot announces a vote of 2 minutes (`voteSkip.durationSeconds`): type `!skip` or `!stay`. The viewer who started it counts as `!skip`. Every user has one ballot; voting again replaces it. Halfway through, the bot posts the standing.
- Skipping needs **more `!skip` than `!stay`** and **at least 65 % (`voteSkip.requiredPercent`) of the recent chatters** as `!skip` votes, rounded up and at least one. The announcement says how many that is. "Recent chatters" is a rolling average: the bot counts the different users who wrote anything in chat per 5 minutes (`chatterAverage.bucketMinutes`) and averages these counts over the last 60 minutes (`chatterAverage.windowMinutes`). Periods nobody wrote in count as zero; the time before the app started does not count. The number is taken when the vote starts.
- If skipping wins, the regular vote for the next video (the 3 options, as with `!forcevoteskip`) starts at once; it runs 2 minutes (`voteSkip.nextVideoVoteDurationSeconds`). If that vote cannot start (too few videos), the video is skipped directly.
- If the chat stays (or `!skip` won without enough votes, or it is a tie), the video keeps playing and `!voteskip` cannot be used for 30 minutes (`voteSkip.cooldownMinutes`). The bot says when it can be used again.
- A vote that is interrupted has no result and starts no cooldown: when the video changes meanwhile, or a moderator ends it with `!cancelvote`.
- Only one vote runs at a time: `!voteskip` is refused while a vote for the next video runs, `!forcevoteskip` while a `!voteskip` vote runs, and the Autovote waits until it is over. Nothing plays (the player waits for the first Play or for a vote): no `!voteskip`.

### Autovote

The **Autovote** checkbox in the control bar (enabled when the Twitch bot is on) starts a vote automatically 400 seconds (6:40) before the current video ends (`autoVote.leadTimeSeconds` in `twitchsettings.json` changes that). Chat can ask for its state with `!autovotestatus` and moderators can switch it with `!toggleautovote`; the checkbox follows such a change.

- The vote is about the video that plays **after** the current one. The rules message says so ("Vote for the video that plays AFTER the current one ...") and so does the result ("It plays after the current video.").
- The winner is queued and starts when the current video ends; the current video is not interrupted. If the video is skipped before it ends, the queued winner plays next.
- The vote is shortened if needed so that it is over (with a safety margin) before the video ends; the time it takes to look up the options counts too. Videos without a known length (live streams) get no vote.
- A video too short for a 2-minute vote (or for the configured vote length, if that is shorter) still gets a vote of that length, and the next video waits for it: when the short video is over, the screen goes black with "Waiting for vote to finish... 1:23" counting down every second, and the winner starts as soon as the vote ends. The rules message says so ("The next video starts when voting has ended."). **Next** or `!forceskip` starts the next video at once (the winner then plays after it), and `!cancelvote` ends the wait.
- There is one vote per video, and nothing happens while the bot is not connected to the chat or another vote is running. Switching playlists drops a queued winner.
- Unchecking the box only prevents new votes; a vote that is already running finishes. The box starts unchecked on every start.

### Main channel alert

With `mainChannel` set, the bot checks once a minute through the Twitch API whether the main channel is live; it stays in the VOD chat and never joins, reads or writes the main channel's chat.

- When the main channel goes live, the bot posts `ALERT LIVE ON MAIN ALERT – twitch.tv/<main channel>` in the VOD chat, once per stream.
- A main channel that is live already when the app starts gets no alert; only going live while the app runs counts.
- A stream that drops and comes back within 10 minutes is the same stream (no second alert); after a longer break it is a new one.
- While the bot is not connected, the alert waits and follows once it is back (if the main channel is still live).
- The check uses the bot's own sign-in (no extra permission is needed); with a manual token, Twitch is asked once which application the token belongs to.

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
  "version": 2,
  "enabled": true,
  "vodChannel": "your_vod_channel",
  "mainChannel": "your_main_channel",
  "signIn": {
    "clientId": "your_application_client_id",
    "botLogin": "your_bot_account",
    "oauthToken": ""
  },
  "privilegedUsers": [
    "your_login_name",
    "123456789"
  ],
  "commandCooldownSeconds": {
    "playbackControl": 3,
    "current": 5,
    "playlist": 5,
    "voteSkip": 5,
    "voteInfo": 30,
    "shuffleStatus": 3,
    "toggleShuffle": 3,
    "playlistShuffleStatus": 3,
    "togglePlaylistShuffle": 3,
    "autoVoteStatus": 5,
    "toggleAutoVote": 5,
    "token": 5,
    "vodCommands": 5,
    "modVodCommands": 5,
    "commandHelp": 5
  },
  "forceVoteSkip": {
    "durationSeconds": 120
  },
  "autoVote": {
    "leadTimeSeconds": 400,
    "durationSeconds": 300
  },
  "voteSkip": {
    "durationSeconds": 120,
    "requiredPercent": 65,
    "cooldownMinutes": 30,
    "nextVideoVoteDurationSeconds": 120
  },
  "revote": {
    "durationSeconds": 45
  },
  "chatterAverage": {
    "windowMinutes": 60,
    "bucketMinutes": 5
  }
}
```

- `version`: the layout of the file. Leave it as it is: a file of an older layout (without `version`, as in app version 2.2 and earlier) is converted automatically when the app starts, and the original is kept next to it as `twitchsettings.v1.json.bak`. The conversion keeps your values, except the Autovote and `!voteskip` timings, which are set to the new defaults (400 s lead time and 300 s for the Autovote; 2 minutes, 65 % and 2 minutes for `!voteskip` and the vote after it). Settings the file does not have yet are added with their default values, so the file always lists every setting.
- `vodChannel`: the channel the bot lives in (the VOD channel): the only chat it joins, reads and writes.
- `mainChannel`: optional; the streamer's main channel. The bot does not join it: once a minute it asks Twitch whether it is live, and when it goes live it posts `ALERT LIVE ON MAIN ALERT – twitch.tv/<main channel>` in the VOD chat. Empty: no check.
- `signIn.clientId`: the Client ID from step 2.
- `signIn.botLogin`: optional; if set, the sign-in warns when you log in with a different account.
- `signIn.oauthToken`: leave empty (only for the manual token, see below).
- `privilegedUsers`: login names or numeric Twitch user IDs that may use the privileged commands without being a moderator, e.g. your own account for testing. User IDs keep working if an account is renamed.
- `commandCooldownSeconds`: the minimum time between two uses of each command, 0 to 3600 seconds (0 turns it off), named after the command (`voteInfo` is `!voteinfo`, and so on). Every command has its own, except:
  - `playbackControl` (default 3): one cooldown **shared** by `!forceskip`, `!skipplaylist`, `!reload`, `!play` and `!pause`. After one of them, none of them runs until it is over, so two moderators cannot skip two videos (or skip and pause) at the same moment.
  - `voteInfo`: at least 30 seconds.
  - `voteSkip` only limits how often the bot answers `!voteskip`; how long `!voteskip` is blocked after the chat chose to stay is `voteSkip.cooldownMinutes`.
  - `!forcevoteskip`, `!cancelvote` and the ballots `!vote`, `!skip` and `!stay` have no cooldown.
- `forceVoteSkip.durationSeconds`: 15 to 1800 (default 120); how long a vote about the next video runs that a moderator starts with `!forcevoteskip`, counted from the moment the last option has been posted.
- `autoVote.leadTimeSeconds`: 60 to 3600 (default 400); how long before the end of a video the Autovote starts its vote. The automatic playlist swap happens 30 seconds earlier.
- `autoVote.durationSeconds`: 15 to 1800 (default 300); how long the Autovote's vote runs (counted the same way). It is shortened so that it is over 10 seconds before the video ends; with less than 2 minutes left it runs 2 minutes (or this length, if shorter), and the next video waits for it.
- `voteSkip.durationSeconds`: 15 to 600 (default 120); how long a `!voteskip` vote runs.
- `voteSkip.requiredPercent`: 1 to 100 (default 65); the share of the recent chatters that has to vote `!skip`.
- `voteSkip.cooldownMinutes`: 1 to 1440 (default 30); how long `!voteskip` is blocked after the chat chose to stay.
- `voteSkip.nextVideoVoteDurationSeconds`: 15 to 1800 (default 120); how long the vote about the next video runs that starts when `!skip` won (counted the same way).
- `revote.durationSeconds`: 15 to 600 (default 45); how long the vote runs that repeats a vote (any of the three) whose winner could not be played (counted the same way).
- `chatterAverage.windowMinutes`: 5 to 1440 (default 60); how far back the average of recent chatters reaches.
- `chatterAverage.bucketMinutes`: 1 to 60 (default 5), at most the window; the period in which the different chatters are counted.

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

Leave `signIn.clientId` empty and set `signIn.botLogin` and `signIn.oauthToken` instead. Get the token while logged in as the bot account by opening `https://id.twitch.tv/oauth2/authorize?response_type=token&client_id=YOUR_CLIENT_ID&redirect_uri=http://localhost:3000&scope=chat:read+chat:edit`, clicking **Authorize** and copying the `access_token` value from the address bar (the page itself shows an error, which is expected). Enter it as `oauth:<token>`. Such tokens expire after about 60 days and have to be replaced by hand.

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

## YouTube backend: YoutubeExplode or yt-dlp

Videos, their streams and playlists are looked up on YouTube by one of two backends:

- **YoutubeExplode** (default): a library inside the app. Nothing to install, but when YouTube changes something it can break (e.g. `Failed to extract the video author.` while loading a playlist) until the app is built with a fixed version.
- **yt-dlp**: the program `yt-dlp.exe`, run for every lookup. It is usually fixed within days of a YouTube change, and you update it yourself without rebuilding the app. A lookup takes about 3.5 seconds, and YouTube only serves the streams a few seconds after it (about 6 seconds; the app waits for that before it plays them). This does not show between two videos, because the next video is looked up while the current one plays; a vote winner, a resume or a playlist switch starts about 10 seconds after the request instead of about 3. Downloading the video list of a playlist with 1,250 videos takes about 9 seconds.

With **fallback** on (the default), a lookup that fails with one backend is tried once more with the other: a video, a playlist, or a playlist that comes back without any video. A video lookup gives the first backend 25 seconds before the second is asked. With YoutubeExplode chosen, putting `yt-dlp.exe` next to the app is enough to give it a fallback.

### Settings (`appsettings.json`)

The file is next to the executable and is created with every setting on the first start. Changes take effect on the **next start**.

```json
{
  "version": 1,
  "youtube": {
    "backend": "youtubeExplode",
    "fallback": true,
    "ytDlp": { "path": "yt-dlp.exe", "extraArguments": [], "checkForUpdates": true }
  }
}
```

| Setting | Meaning |
|---------|---------|
| `youtube.backend` | `youtubeExplode` or `ytDlp` (any case). Anything else means YoutubeExplode |
| `youtube.fallback` | Try a failed lookup once more with the other backend (if it is available) |
| `youtube.ytDlp.path` | Where `yt-dlp.exe` is: absolute, or relative to the app folder; a bare file name is also looked for on `PATH` |
| `youtube.ytDlp.extraArguments` | Extra arguments for every yt-dlp run, one per entry, e.g. `["--cookies-from-browser", "firefox"]` or `["--js-runtimes", "node"]` |
| `youtube.ytDlp.checkForUpdates` | Say at startup when a newer yt-dlp has been released (see below) |

If `ytDlp` is chosen but `yt-dlp.exe` is not found, a message box says so at startup and YoutubeExplode is used.

### Setting up yt-dlp

1. Download `yt-dlp.exe` from the [yt-dlp releases](https://github.com/yt-dlp/yt-dlp/releases) and put it next to `YouTubeVideoPlayer.exe`.
2. Since late 2025 yt-dlp needs a JavaScript runtime for YouTube. Download Deno (`deno-x86_64-pc-windows-msvc.zip` from the [Deno releases](https://github.com/denoland/deno/releases)) and put `deno.exe` next to `yt-dlp.exe` (or anywhere on `PATH`). Without it yt-dlp finds fewer streams, and its warning `No supported JavaScript runtime could be found` appears once in `errorlog.txt`.
3. Set `"backend": "ytDlp"` in `appsettings.json` and restart the app.

### Updating yt-dlp

The app never updates yt-dlp itself. When yt-dlp is in use (chosen, or as the fallback) and `checkForUpdates` is on, the app asks GitHub at startup for the latest yt-dlp release. If it is newer than the installed one, a message box says so once the window is open; **OK** just closes it, and the app keeps using the installed version. To update, run this in the app folder (the folder must be writable), or download the new `yt-dlp.exe`:

```powershell
.\yt-dlp.exe -U
```

### "Sign in to confirm you're not a bot"

YouTube sometimes refuses to answer a connection it takes for a bot – often a VPN, a shared or server address, or one that made many requests in a short time. Then **every** lookup fails, with both backends: yt-dlp says `Sign in to confirm you’re not a bot`, YoutubeExplode only `Video '…' is not available` (it reports YouTube's refusal like that), and the app keeps trying the next video, so nothing plays. Waiting sometimes helps, and turning off a VPN often does. The reliable fix is to let yt-dlp use the cookies of a browser that is signed in to YouTube, in `appsettings.json`:

```json
"ytDlp": { "path": "yt-dlp.exe", "extraArguments": ["--cookies-from-browser", "firefox"], "checkForUpdates": true }
```

- **Firefox** works best: on Windows, yt-dlp often cannot read the cookies of Chrome and Edge.
- Or export a `cookies.txt` (Netscape format) with a browser extension, put it next to `yt-dlp.exe` and use `["--cookies", "cookies.txt"]`. yt-dlp's wiki recommends exporting from a private window and closing it afterwards, so YouTube does not replace the cookies.
- Use a **second YouTube account**, not your main one: YouTube can restrict accounts it sees used by tools like this.
- Set `"backend": "ytDlp"`: only yt-dlp uses the cookies; YoutubeExplode keeps failing while the connection is refused.

### Signing in from the app (prototype)

The app can sign in to YouTube in a window of its own and hand the sign-in to yt-dlp. While yt-dlp is in use, the control bar has a **YouTube sign-in** button that opens it, and it opens by itself the first time YouTube asks a lookup for a sign-in (the bot check above). The player keeps running meanwhile. Without the player (no Twitch bot either), it opens with:

```powershell
.\YouTubeVideoPlayer.exe --youtube-signin
```

Sign in with the Google account yt-dlp should use – a spare one is best. When you are back on YouTube, the window says **Signed in** and saves the sign-in to `%LOCALAPPDATA%\YouTubeVideoPlayer\youtube-cookies.txt`; yt-dlp uses it from its next lookup on, also in a player that is already running. **Test with yt-dlp** looks up a video with it, **Sign out** deletes it.

- The window is a plain Microsoft Edge WebView2. Google may refuse to sign in there (`This browser or app may not be secure`); then use the Firefox cookies described above instead. Finding that out is what the prototype is for.
- The file signs in to that Google account. It is kept in your Windows profile, not in the app folder, so it is not copied along with the app.
- Cookies of your own in `ytDlp.extraArguments` (`--cookies`, `--cookies-from-browser`) win over the saved sign-in.

### In the log

`errorlog.txt` names the backend at startup (`YouTube backend: yt-dlp (C:\...\yt-dlp.exe), falling back to YoutubeExplode.`), the backend that found the streams of every video (`Streams for '<id>' (yt-dlp): ...`), and every fallback (`YoutubeExplode failed for playlist '<id>' (...); trying yt-dlp.`).

---

## User Interface

- **Video area** – double-click toggles fullscreen.
- **Control bar** – mute button, volume slider, Twitch status, Autovote checkbox, Playlists, Next, Fullscreen, Borderless.
- **Overlays** – all in one look, see-through pills with an orange icon circle: "now playing" card, status and vote ticker (lower left), volume (upper right), buffering, reconnecting and waiting (center).

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
├── YouTube/                YoutubeExplodeService, YtDlp/ (yt-dlp.exe backend, update check), the fallback between them, stream selection
├── Playlists/              PlaylistManager, playlists.json store, per-playlist cache, PlaylistSwapScheduler (automatic swap)
├── Twitch/                 TwitchChatService, ChatCommandManager, Commands/, Voting/, Auth/ (sign-in)
├── UI/                     Main form (view only), window modes, dialogs
└── Infrastructure/         Logger, JSON file helpers, appsettings.json, UI-thread timer

YoutubeVideoPlayer.Tests/   xUnit + NSubstitute tests
```

---

## Tests

```bash
dotnet test YoutubeVideoPlayer.Tests/YoutubeVideoPlayer.Tests.csproj
```

The tests run offline. YouTube, VLC, Twitch and the clock are replaced by substitutes, so even timeouts of minutes or hours run instantly. A few UI tests briefly open transparent, off-screen windows. Tests against the real YouTube are skipped by default; to run them, set `YTP_INTEGRATION_TESTS=1` (PowerShell: `$env:YTP_INTEGRATION_TESTS = "1"`). The yt-dlp ones also need `yt-dlp.exe` on `PATH` or in `YTP_YTDLP_PATH`.

---

## Notes

The application does not use the official YouTube API. Streams and playlists are resolved dynamically through YoutubeExplode or yt-dlp and played using LibVLC.
