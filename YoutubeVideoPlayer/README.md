# YouTube Video Player

A lightweight Windows Forms application for playing random YouTube videos using **LibVLCSharp** and **YoutubeExplode**. The application streams videos directly from YouTube, supports fullscreen playback, audio controls, and automatically skips unavailable or broken videos.

---

## Features

- Random playback from a configurable video list
- Streams directly from YouTube
- Automatic retrieval of the highest available video and audio quality
- Automatic fallback to muxed streams when required
- Fullscreen mode
- Volume slider
- Keyboard volume controls
- Mute support
- Buffering indicator
- Automatic timeout when buffering takes too long
- Automatic skipping of unavailable or invalid videos
- Video title overlay
- Double-click to toggle fullscreen

---

## Requirements

- .NET Framework / .NET version matching the project
- VLC media libraries
- Internet connection

NuGet packages:

- LibVLCSharp
- LibVLCSharp.WinForms
- YoutubeExplode
- Newtonsoft.Json

---

## Configuration

The application loads its playlist from a file named:

```
videos.json
```

located in the application directory.

Example:

```json
[
  "dQw4w9WgXcQ",
  "aqz-KE-bpKQ",
  "kXYiU_JCYtU"
]
```

Both formats are supported:

```json
[
    "dQw4w9WgXcQ",
    "https://www.youtube.com/watch?v=dQw4w9WgXcQ"
]
```

If the file does not exist, the application automatically creates one containing a sample video.

---

## Playback Behaviour

When a video is selected:

1. A random entry from `videos.json` is chosen.
2. Video metadata is downloaded.
3. The best available stream is selected.
4. Playback starts immediately.
5. The video title is displayed briefly.

If a video cannot be played:

- it is removed from the current playlist
- the next random video is attempted automatically
- no error dialog is shown

If no playable videos remain, playback stops silently.

---

## Buffering Handling

While buffering:

- a centered loading indicator is shown
- an animated spinner is displayed

If buffering exceeds **30 seconds**:

- playback is cancelled
- the next random video starts automatically

---

## User Interface

### Video Area

- Displays the currently playing video
- Double-click toggles fullscreen

### Bottom Control Bar

Contains:

- Mute button
- Volume slider
- Next button
- Fullscreen button

### Overlays

#### Title Overlay

Displays the current video's title in the lower-left corner.

#### Volume Overlay

Displays the current volume whenever it changes.

#### Buffering Overlay

Displayed while media is buffering.

---

## Keyboard Shortcuts

| Key | Action |
|------|--------|
| F11 | Toggle fullscreen |
| Esc | Exit fullscreen |
| + | Increase volume |
| - | Decrease volume |
| M | Toggle mute |

---

## Fullscreen

Entering fullscreen:

- removes the window border
- maximizes the window
- hides the control panel
- keeps the window on top

Leaving fullscreen restores the previous window state.

---

## Error Handling

The player automatically handles:

- invalid video IDs
- unavailable videos
- removed YouTube videos
- playback errors
- stream resolution failures
- buffering timeouts

Whenever possible, playback simply continues with another random video.

---

## Resource Management

On shutdown the application cleanly disposes:

- LibVLC
- MediaPlayer
- Media
- YoutubeClient
- Timers

to prevent resource leaks.

---

## Project Structure

```
VideoPlayerForm
│
├── UI Initialization
│   ├── VideoView
│   ├── Control Buttons
│   ├── Volume Controls
│   ├── Labels
│   └── Timers
│
├── Playback
│   ├── LoadVideoListAsync()
│   ├── PlayNextAsync()
│   └── PlayVideoByIdAsync()
│
├── MediaPlayer Events
│   ├── EndReached
│   ├── Buffering
│   ├── Playing
│   └── EncounteredError
│
├── Fullscreen
│
├── Keyboard Handling
│
└── Cleanup
```

---

## Dependencies

- LibVLCSharp
- LibVLCSharp.WinForms
- YoutubeExplode
- Newtonsoft.Json

---

## Notes

The application is intended as a simple random YouTube video player and does not use the official YouTube API. Streams are resolved dynamically through YoutubeExplode and played using LibVLC.
