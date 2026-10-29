# AmbiWin

Ambient light for Windows. AmbiWin turns the edges of your screen into a soft glow that takes its color from the
album art of whatever is playing and pulses with the beat.

## Features

- A glow around the screen edges that never blocks clicks, never takes focus and never appears in screenshots,
  recordings or screen shares (you can turn that off to record a demo).
- Colors from the playing track's album art (Spotify, Apple Music, YouTube in the browser, anything that shows
  in the Windows media controls), or a color you pick.
- **Ambilight mode:** each part of the glow takes the color of what's on screen next to it, smoothed so
  scene cuts fade instead of flashing. Works with music sync on top, and with fullscreen video
  (set *In fullscreen apps* to *Keep glowing*).
- Motion that follows the beat, or a steady glow. Adjustable size, brightness, softness and beat sensitivity,
  plus a delay for Bluetooth headphones.
- Steps aside for fullscreen games, video and slideshows; sleeps when the screen is locked or off;
  runs at reduced rate (or steady) on battery.
- One screen or all screens. Lives in the notification area; starts with Windows if you want.
- Light on resources: about 1% CPU while pulsing, nothing at all when there is nothing to draw.

## Install

1. Download the latest `AmbiWin-x.y.z-win-x64.zip` from [Releases](https://github.com/redwan2003-bot/AmbiWin/releases)
   (`win-arm64` for ARM laptops).
2. Extract it to a folder you keep, for example `Documents\AmbiWin`, and run `AmbiWin.exe`.
3. AmbiWin isn't code-signed yet, so Windows SmartScreen may say it "protected your PC". Choose
   **More info → Run anyway**. The source is all here if you'd rather build it yourself.

Click the AmbiWin icon in the notification area for settings. To uninstall, turn off *Start with Windows*,
quit AmbiWin, and delete its folder and `%LOCALAPPDATA%\AmbiWin`.

## Privacy

AmbiWin analyzes system audio live, on your PC, to sync the light. Nothing is recorded, saved or uploaded.
Track titles and artwork never leave your PC. There are no accounts, no analytics and no tracking.
AmbiWin makes no network requests at all.

## Requirements

Windows 11 (x64 or ARM64). Building needs the [.NET 10 SDK](https://dotnet.microsoft.com/download).

## Build from source

```
dotnet build AmbiWin.slnx -c Release
dotnet test tests/AmbiWin.Core.Tests
dotnet run --project src/AmbiWin.App
```

`AmbiWin.Core` and its tests build on any OS; the app itself targets Windows.

### Tuning the beat detector

`tools/BeatBench` runs the detector over WAV files and scores it against hand-tapped beats:

```
dotnet run --project tools/BeatBench -- path/to/clips --sensitivity 0.5
```

Put `song.txt` next to `song.wav` with one beat time in seconds per line (an Audacity label export works).
Only commit audio you have the right to publish, such as CC-licensed music with its license file.

## Project layout

| Project | Owns |
| --- | --- |
| `AmbiWin.Core` | Settings, beat detector, FFT, color math and palette extraction. Pure .NET, no dependencies. |
| `AmbiWin.Audio` | WASAPI loopback capture |
| `AmbiWin.Media` | Now-playing listener and artwork |
| `AmbiWin.Render` | Overlay windows, swap chains, glow shader |
| `AmbiWin.Platform` | Fullscreen detection |
| `AmbiWin.App` | Tray icon, settings window, power and lock events, start with Windows, wiring |

See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for the rules that keep this structure healthy.

## License

[MIT](LICENSE)
