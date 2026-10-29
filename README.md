# AmbiWin

Ambient light for Windows. AmbiWin turns the edges of your screen into a soft glow that takes its color from the
album art of whatever is playing and pulses with the beat.

> **Status: pre-alpha.** The core audio and color engine works and is tested; the on-screen overlay is next.

## What it will do

- A glow around the screen edges that never blocks clicks, never takes focus and never appears in screenshots,
  recordings or screen shares.
- Colors from the playing track's album art (Spotify, Apple Music, YouTube in the browser and more),
  or two colors you pick.
- Motion that follows the beat, or a steady idle glow.
- Hides itself in fullscreen games and video, and goes quiet on battery.

## Privacy

AmbiWin analyzes system audio live, on your PC, to sync the light. Nothing is recorded, saved or uploaded.
Track titles and artwork never leave your PC. There are no accounts, no analytics and no tracking.
The only network request is an optional check for updates.

## Requirements

Windows 11 (x64 or ARM64). Building needs the [.NET 10 SDK](https://dotnet.microsoft.com/download).

## Build

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
| `AmbiWin.Platform` | Fullscreen, power, lock and monitor events; start with Windows |
| `AmbiWin.App` | Tray icon, settings window, wiring |

See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) for the rules that keep this structure healthy.

## License

[MIT](LICENSE)
