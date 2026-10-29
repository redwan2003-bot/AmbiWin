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
| _week 1_ | Overlay approach: layered + DirectComposition, or the `UpdateLayeredWindow` fallback | _pending spike_ |
| _week 1_ | Native AOT: yes or no | _pending spike_ |
