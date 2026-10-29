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
