# Architecture

## Rules

1. **Dependencies point inward.** Every project may use `AmbiWin.Core`; `AmbiWin.Core` uses nothing — no other project
   and no package. Its build fails if one is added.
2. **Threads meet only through snapshots.** The audio thread, media worker, UI thread and platform events each
   publish immutable values through `Latest<T>`. The render thread reads the newest each frame and never waits on
   a writer. Writers raise a `WakeSignal`, so a parked render thread costs 0% CPU.
3. **Testable logic lives in Core.** Anything that can run without Windows (detection, color, settings, easing)
   belongs in `AmbiWin.Core` with unit tests.
4. **No network except the update check.** No telemetry, no analytics, ever.

## Data flow

```
Audio thread   ──► Latest<AudioFeatures> ─┐
Media worker   ──► Latest<Palette>       ─┤
UI thread      ──► Latest<GlowSettings>  ─┼──► Render thread ──► overlay windows (one per monitor)
Platform events──► suspend flags         ─┘
```

## Decisions log

Record each decision with its date and reason. The week-1 spike decides the first two.

| Date | Decision | Why |
| --- | --- | --- |
| 2026-10-29 | .NET 10 LTS, Windows 11 minimum, MIT | .NET 8 support ends Nov 2026; Windows 10 is out of support |
| 2026-10-29 | Beat detector: 1.0 s flux history, 3× mean gate, bass-weighted per-bin flux | A 0.5 s history fired early onsets below ~120 BPM; see `BeatDetectorOptions` |
| 2026-10-29 | 3-day build: WPF settings window instead of WinUI 3; self-contained exe instead of Native AOT | Removes the Windows App SDK runtime and AOT trimming risk from a 3-day schedule |
| 2026-10-29 | Overlay: layered, click-through window + DirectComposition, 4 strip swap chains | Day-1 test on Windows 11 (build 26300, Intel UHD): visible, click-through, absent from screenshots |
| 2026-10-29 | Frame cap: draw on every Nth compositor tick, target 60 fps | 144 Hz uncapped cost ~8% CPU; capped: 71 fps at 0.8–1.2% process CPU |
| 2026-10-29 | Audio: NAudio 3 `WasapiRecorderBuilder` loopback, MMCSS "Audio" priority | `WasapiLoopbackCapture` is obsolete in NAudio 3 |
| 2026-10-29 | Fullscreen detection: poll the foreground window every 500 ms + `SHQueryUserNotificationState` | One cheap check catches F11, borderless games and exclusive D3D alike; event hooks miss some of these |
| 2026-10-29 | Display off, lock, sleep, AC/battery from a hidden window's push notifications | Nothing polls; resume reopens audio capture because the old stream can go silent |
| 2026-10-29 | Release: self-contained single-file exe in a zip per architecture, unsigned for v1.0 | No installer or runtime needed; signing (SignPath/Azure Trusted Signing) is a v1.1 item |
| 2026-10-29 | Ambilight: Desktop Duplication on a capture device per monitor's own GPU, GPU mip downscale, 1-sample-late readback, CPU smoothing in `ScreenColorFilter` | ~8 KB read back per sample; works on hybrid laptops; overlay always excluded from capture in this mode so it never samples itself. Measured 1.2% CPU (Intel UHD, 1080p) |
| 2026-10-30 | Mini player: WPF window on the DWM frame (WindowChrome) with accent-policy acrylic blur | DWM system backdrops go flat when a window is inactive, and a floating player is almost never active |
| 2026-10-30 | Taskbar wrap: the glow follows each monitor's work area; rebuilt on `SPI_SETWORKAREA` | Windows' equivalent of Lumn's notch handling; covers any taskbar position, size and auto-hide |
| 2026-10-30 | No lock screen glow or player | Windows gives apps no way onto the secure lock desktop (macOS has `canBecomeVisibleWithoutLogin`); Windows 11 shows its own lock screen media controls |
