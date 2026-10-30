using System.Diagnostics;
using System.Numerics;
using AmbiWin.Core.Audio;
using AmbiWin.Core.Color;
using AmbiWin.Core.Diagnostics;
using AmbiWin.Core.Motion;
using AmbiWin.Core.Settings;
using AmbiWin.Core.Threading;
using SharpGen.Runtime;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DXGI;

namespace AmbiWin.Render;

/// <summary>
/// Owns the render thread: overlay windows (one per enabled monitor), the GPU device, the
/// DirectComposition tree and the frame loop. Reads the newest settings, palette and audio snapshots
/// each frame and never waits on a writer. Parks (0% CPU) when there is nothing new to draw.
/// </summary>
public sealed class GlowRenderer : IDisposable
{
    private const float PaletteFadeSeconds = 0.8f;
    private const double TargetFps = 60; // a soft glow gains nothing from 144 fps; this halves GPU and CPU work
    private const int DxgiErrorDeviceRemoved = unchecked((int)0x887A0005);
    private const int DxgiErrorDeviceReset = unchecked((int)0x887A0007);

    private readonly Latest<GlowSettings> _settings;
    private readonly Latest<Palette> _palette;
    private readonly Latest<AudioFeatures> _audio;
    private readonly WakeSignal _wake;
    private readonly Thread _thread;
    private volatile bool _stop;
    private int _suspendReasons; // SuspendReason flags; glow hidden while any is set
    private readonly Queue<(double Time, AudioFeatures Features)> _delayedAudio = new();
    private volatile string? _fullscreenDevice; // monitor covered by a fullscreen app, if any
    private volatile bool _onBattery;

    // GPU state (render thread only)
    private IDXGIFactory2? _factory;
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDCompositionDevice? _dcomp;
    private ID3D11VertexShader? _vs;
    private ID3D11PixelShader? _ps;
    private ID3D11Buffer? _constants;
    private readonly List<MonitorOverlay> _overlays = [];

    // Animation state (render thread only)
    private OnePole _level;
    private OnePole _pulse;
    private float _phase;
    private ID3D11SamplerState? _linearSampler;
    private float _screenMix;           // eased 0..1 toward the Ambilight mode
    private Palette _shownPalette;
    private Palette _fadeFrom;
    private Palette _fadeTo;
    private float _fadeElapsed = PaletteFadeSeconds;
    private GlowSettings? _appliedSettings;

    public GlowRenderer(Latest<GlowSettings> settings, Latest<Palette> palette, Latest<AudioFeatures> audio, WakeSignal wake)
    {
        _settings = settings;
        _palette = palette;
        _audio = audio;
        _wake = wake;
        _shownPalette = _fadeFrom = _fadeTo = palette.Value;
        _thread = new Thread(Run) { Name = "AmbiWin.Render", IsBackground = true };
    }

    /// <summary>Frames drawn since start (for diagnostics and tests on the PC).</summary>
    public long FramesRendered { get; private set; }

    public void Start() => _thread.Start();

    /// <summary>Hides the glow while <paramref name="reason"/> holds; the glow returns when no reason remains.</summary>
    public void SetSuspended(SuspendReason reason, bool on)
    {
        int bit = (int)reason;
        int current, next;
        do
        {
            current = Volatile.Read(ref _suspendReasons);
            next = on ? current | bit : current & ~bit;
        } while (Interlocked.CompareExchange(ref _suspendReasons, next, current) != current);
        if (next != current) Log.Info($"Glow {(next == 0 ? "resumed" : "suspended")} ({(SuspendReason)next}).");
        _wake.Set();
    }

    public SuspendReason Suspended => (SuspendReason)Volatile.Read(ref _suspendReasons);

    /// <summary>Monitor (device name) a fullscreen app covers, or null. Hidden per the HideInFullscreen setting.</summary>
    public void SetFullscreenMonitor(string? deviceName)
    {
        _fullscreenDevice = deviceName;
        _wake.Set();
    }

    /// <summary>Running on battery: applies the BatteryMode setting (30 fps, or steady glow).</summary>
    public void SetOnBattery(bool onBattery)
    {
        if (_onBattery == onBattery) return;
        _onBattery = onBattery;
        Log.Info(onBattery ? "On battery." : "On AC power.");
        _wake.Set();
    }

    public void Dispose()
    {
        _stop = true;
        _wake.Set();
        if (_thread.IsAlive) _thread.Join(TimeSpan.FromSeconds(2));
    }

    private void Run()
    {
        Log.Info("Render thread started.");
        try
        {
            CreateDevice();
            BuildOverlays(_settings.Value);
            Loop();
        }
        catch (Exception ex)
        {
            Log.Error("Render thread failed", ex);
        }
        finally
        {
            DisposeOverlays();
            DisposeDevice();
            Log.Info("Render thread stopped.");
        }
    }

    private void Loop()
    {
        var clock = Stopwatch.StartNew();
        double last = clock.Elapsed.TotalSeconds;
        double statsAt = last + 10;
        long framesAtStats = 0;
        double busySeconds = 0;
        double lastFrameAt = 0;
        double lastFlushAt = last;
        double tickSeconds = 1.0 / 60; // refined from measured compositor ticks
        int fastFlushes = 0;
        double targetFps = TargetFps;

        while (!_stop)
        {
            if (!OverlayWindow.PumpMessages()) break;

            if (OverlayWindow.DisplayChanged)
            {
                OverlayWindow.DisplayChanged = false;
                Log.Info("Display or taskbar layout changed; rebuilding overlays.");
                DisposeOverlays();
                BuildOverlays(_settings.Value);
            }

            double now = clock.Elapsed.TotalSeconds;
            float dt = (float)Math.Clamp(now - last, 0, 0.1);
            last = now;

            var settings = _settings.Value;
            bool dirty = ApplySettings(settings);

            // Per-monitor visibility: global switches, then the fullscreen rule.
            bool globallyShown = settings.Enabled && Volatile.Read(ref _suspendReasons) == 0;
            string? fullscreen = _fullscreenDevice;
            bool show = false;
            foreach (var o in _overlays)
            {
                bool hiddenByFullscreen = fullscreen is not null && settings.HideInFullscreen switch
                {
                    FullscreenHide.AllMonitors => true,
                    FullscreenHide.ThatMonitor => string.Equals(o.Window.Monitor.DeviceName, fullscreen, StringComparison.OrdinalIgnoreCase),
                    _ => false,
                };
                bool visible = globallyShown && !hiddenByFullscreen;
                if (visible != o.Visible)
                {
                    o.Window.Show(visible);
                    o.Visible = visible;
                    if (visible) dirty = true; // redraw what came back
                }
                show |= visible;
            }

            bool onBattery = _onBattery;
            targetFps = onBattery && settings.BatteryMode == BatteryMode.Reduced ? 30 : TargetFps;

            if (!show)
            {
                _wake.Wait(TimeSpan.FromMilliseconds(250));
                last = clock.Elapsed.TotalSeconds;
                continue;
            }

            // Targets for this frame. With a headphone delay, use the audio from that long ago.
            var audio = DelayedAudio(now, settings.AudioDelayMs ?? 0);
            bool music = settings.Animation == AnimationMode.MusicSync && !(onBattery && settings.BatteryMode == BatteryMode.Idle);
            float targetLevel = music ? (audio.Silent ? 0f : audio.Level) : 0.6f;
            float targetPulse = music && !audio.Silent ? audio.Pulse : 0f;
            _level.Step(targetLevel, dt, attackSeconds: 0.030f, releaseSeconds: 0.250f);
            _pulse.Step(targetPulse, dt, attackSeconds: 0.010f, releaseSeconds: 0.120f);

            // Palette cross-fade when a new palette arrives.
            var wanted = settings.ColorSource == ColorSource.Manual ? settings.ManualPalette() : _palette.Value;
            if (wanted != _fadeTo)
            {
                _fadeFrom = _shownPalette;
                _fadeTo = wanted;
                _fadeElapsed = 0;
            }
            bool fading = _fadeElapsed < PaletteFadeSeconds;
            if (fading)
            {
                _fadeElapsed = Math.Min(PaletteFadeSeconds, _fadeElapsed + dt);
                _shownPalette = Palette.Lerp(_fadeFrom, _fadeTo, Ease.InOutCubic(_fadeElapsed / PaletteFadeSeconds));
            }

            // Ambilight: sample the screen under each visible overlay and fade between modes.
            bool screenMode = settings.ColorSource == ColorSource.Screen;
            bool screenMoving = UpdateScreenSamplers(screenMode, now, dt);
            float screenTarget = screenMode ? 1f : 0f;
            bool screenFading = _screenMix != screenTarget;
            if (screenFading)
                _screenMix = Math.Clamp(_screenMix + Math.Sign(screenTarget - _screenMix) * dt / PaletteFadeSeconds, 0f, 1f);
            fading |= screenFading || screenMoving;

            bool active = music && !audio.Silent;
            bool settling = Math.Abs(_level.Value - targetLevel) > 0.002f || _pulse.Value > 0.002f;
            if (active) _phase = (_phase + dt * 0.01f) % 1f; // slow drift of the gradient while music plays

            if (active || settling || fading || dirty)
            {
                // Frame cap: draw on every Nth compositor tick so the rate stays near TargetFps on any refresh rate
                // (144 Hz → 72 fps, 120 Hz → 60, 60 Hz → 60). Half a tick of slack keeps frames on tick boundaries.
                if (dirty || now >= lastFrameAt + 1.0 / targetFps - tickSeconds * 0.5)
                {
                    long start = Stopwatch.GetTimestamp();
                    if (!RenderFrame(settings)) RecoverDevice();
                    busySeconds += Stopwatch.GetElapsedTime(start).TotalSeconds;
                    FramesRendered++;
                    lastFrameAt = now;
                }
                double beforeFlush = clock.Elapsed.TotalSeconds;
                _ = Native.DwmFlush(); // wait for the next compositor tick
                double afterFlush = clock.Elapsed.TotalSeconds;

                // With the display off DWM stops composing and DwmFlush returns at once: never spin a core.
                fastFlushes = afterFlush - beforeFlush < 0.0005 ? fastFlushes + 1 : 0;
                if (fastFlushes >= 3)
                {
                    Thread.Sleep((int)(1000 / targetFps));
                    afterFlush = clock.Elapsed.TotalSeconds;
                }
                double tick = afterFlush - lastFlushAt;
                if (tick > 0.002 && tick < 0.05) tickSeconds += (tick - tickSeconds) * 0.1; // smoothed refresh period
                lastFlushAt = afterFlush;
            }
            else
            {
                // Parked: the last frame stays on screen. Wake on any new snapshot
                // (and look at the screen again soon in Ambilight mode).
                _wake.Wait(TimeSpan.FromMilliseconds(screenMode ? 50 : 200));
            }

            if (now >= statsAt)
            {
                long frames = FramesRendered - framesAtStats;
                Log.Info($"Render: {frames / 10.0:0.0} fps (cap {targetFps:0}) over 10 s, {(frames > 0 ? busySeconds / frames * 1000 : 0):0.00} ms CPU/frame, " +
                         $"{(active ? "active" : settling || fading ? "settling" : "parked")}, level {_level.Value:0.00}, " +
                         $"refresh {1 / tickSeconds:0} Hz, process CPU {CpuPercent():0.0}%, overlays {_overlays.Count}");
                framesAtStats = FramesRendered;
                busySeconds = 0;
                statsAt = now + 10;
            }
        }
    }

    /// <summary>Runs each visible overlay's screen sampler while screen colors are on or fading out.</summary>
    private bool UpdateScreenSamplers(bool screenMode, double now, float dt)
    {
        bool moving = false;
        bool needed = screenMode || _screenMix > 0;
        foreach (var o in _overlays)
        {
            if (needed && o.Visible)
            {
                o.Sampler ??= new ScreenSampler(_device!, o.Window.Monitor);
                moving |= o.Sampler.Update(_context!, now, dt);
            }
            else if (!needed && o.Sampler is not null)
            {
                o.Sampler.Dispose();
                o.Sampler = null;
            }
        }
        return moving;
    }

    /// <summary>The glow must never appear in its own screen capture, so Ambilight mode always hides it.</summary>
    private static bool HideFromCapture(GlowSettings s) => s.HideFromCapture || s.ColorSource == ColorSource.Screen;

    /// <summary>Applies settings that change geometry or window state. Returns true when a redraw is needed.</summary>
    private bool ApplySettings(GlowSettings settings)
    {
        if (ReferenceEquals(settings, _appliedSettings)) return false;
        var previous = _appliedSettings;
        _appliedSettings = settings;
        if (previous is null) return true;

        bool geometryChanged = previous.ThicknessPx != settings.ThicknessPx || previous.SpreadPx != settings.SpreadPx ||
                               previous.WrapTaskbar != settings.WrapTaskbar;
        bool monitorsChanged = previous.AllScreens != settings.AllScreens || !previous.Monitors.SequenceEqual(settings.Monitors);
        if (monitorsChanged)
        {
            DisposeOverlays();
            BuildOverlays(settings);
        }
        else
        {
            foreach (var o in _overlays)
            {
                if (geometryChanged) o.ResizeStrips(this, settings);
                if (o.Window.HiddenFromCapture != HideFromCapture(settings)) o.Window.SetHiddenFromCapture(HideFromCapture(settings));
            }
            if (geometryChanged) _dcomp!.Commit().CheckError();
        }
        return true;
    }

    private bool RenderFrame(GlowSettings settings)
    {
        var ctx = _context!;
        ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        ctx.VSSetShader(_vs);
        ctx.PSSetShader(_ps);
        ctx.PSSetConstantBuffer(0, _constants);
        ctx.PSSetSampler(0, _linearSampler);

        var a = _shownPalette.Primary.ToLinearSrgb();
        var b = _shownPalette.Secondary.ToLinearSrgb();

        foreach (var overlay in _overlays)
        {
            if (!overlay.Visible) continue;
            float scale = overlay.Window.Monitor.Scale;
            var constants = new GlowConstants
            {
                MonitorSize = new Vector2(overlay.Window.Monitor.Width, overlay.Window.Monitor.Height),
                Thickness = settings.ThicknessPx * scale,
                Spread = settings.SpreadPx * scale,
                CornerRadius = settings.CornerRadiusPx * scale,
                Intensity = settings.Intensity,
                ColorA = new Vector3(Clamp01(a.R), Clamp01(a.G), Clamp01(a.B)),
                Mix = settings.Mix,
                ColorB = new Vector3(Clamp01(b.R), Clamp01(b.G), Clamp01(b.B)),
                Phase = _phase,
                Level = _level.Value,
                Pulse = _pulse.Value,
                StripDepth = overlay.Depth,
                ScreenMix = overlay.Sampler?.View is null ? 0f : _screenMix,
                Inset = new Vector4(overlay.Insets.Left, overlay.Insets.Top, overlay.Insets.Right, overlay.Insets.Bottom),
            };
            ctx.PSSetShaderResource(0, overlay.Sampler?.View!); // null unbinds

            foreach (var strip in overlay.Strips)
            {
                constants.StripOffset = new Vector2(strip.X, strip.Y);
                ctx.UpdateSubresource(in constants, _constants!, 0, 0, 0, null);
                ctx.OMSetRenderTargets(strip.Target!, null);
                ctx.RSSetViewport(0, 0, strip.Width, strip.Height, 0, 1);
                ctx.Draw(3, 0);
                Result result = strip.SwapChain!.Present(0, PresentFlags.None);
                if (result.Code == DxgiErrorDeviceRemoved || result.Code == DxgiErrorDeviceReset)
                {
                    Log.Warn($"GPU device lost (0x{result.Code:X8}).");
                    return false;
                }
            }
        }
        return true;
    }

    private static float Clamp01(float v) => Math.Clamp(v, 0f, 1f);

    /// <summary>
    /// Bluetooth headphones play sound 100–300 ms after Windows hands it over, while loopback hears it at
    /// hand-over. Holding audio features back by the same delay puts the light back on the beat.
    /// </summary>
    private AudioFeatures DelayedAudio(double now, int delayMs)
    {
        var latest = _audio.Value;
        if (delayMs <= 0)
        {
            _delayedAudio.Clear();
            return latest;
        }

        _delayedAudio.Enqueue((now, latest));
        double cutoff = now - delayMs / 1000.0;
        AudioFeatures? delayed = null;
        while (_delayedAudio.Count > 0 && _delayedAudio.Peek().Time <= cutoff)
            delayed = _delayedAudio.Dequeue().Features;
        if (delayed is { } d) _lastDelayed = d;
        return _lastDelayed;
    }

    private AudioFeatures _lastDelayed = AudioFeatures.SilentAt(TimeSpan.Zero);

    private TimeSpan _cpuAtLastSample;
    private long _wallAtLastSample = Stopwatch.GetTimestamp();

    /// <summary>Whole-process CPU use since the last call, as % of all cores (the number Task Manager shows).</summary>
    private double CpuPercent()
    {
        var cpu = Process.GetCurrentProcess().TotalProcessorTime;
        double wall = Stopwatch.GetElapsedTime(_wallAtLastSample).TotalSeconds;
        double percent = wall > 0 ? (cpu - _cpuAtLastSample).TotalSeconds / (wall * Environment.ProcessorCount) * 100 : 0;
        _cpuAtLastSample = cpu;
        _wallAtLastSample = Stopwatch.GetTimestamp();
        return percent;
    }

    private void RecoverDevice()
    {
        Log.Info("Recreating GPU device and overlays.");
        DisposeOverlays();
        DisposeDevice();
        Thread.Sleep(500);
        CreateDevice();
        BuildOverlays(_settings.Value);
    }

    private void CreateDevice()
    {
        _factory = DXGI.CreateDXGIFactory2<IDXGIFactory2>(false);

        // Prefer the integrated GPU so hybrid laptops never wake the discrete one for a glow.
        IDXGIAdapter1? adapter = null;
        using (var factory6 = _factory.QueryInterfaceOrNull<IDXGIFactory6>())
        {
            if (factory6 is not null)
                adapter = factory6.EnumAdapterByGpuPreference<IDXGIAdapter1>(0, GpuPreference.MinimumPower);
        }

        var flags = DeviceCreationFlags.BgraSupport;
        if (Environment.GetEnvironmentVariable("AMBIWIN_D3D_DEBUG") == "1") flags |= DeviceCreationFlags.Debug;

        FeatureLevel[] levels = [FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0];
        D3D11.D3D11CreateDevice(adapter!, adapter is null ? DriverType.Hardware : DriverType.Unknown, flags, levels,
            out _device, out _context).CheckError();
        if (adapter is not null)
        {
            Log.Info($"GPU: {adapter.Description1.Description}");
            adapter.Dispose();
        }

        using var dxgiDevice = _device!.QueryInterface<IDXGIDevice>();
        _dcomp = DComp.DCompositionCreateDevice<IDCompositionDevice>(dxgiDevice);

        var vsCode = Compiler.Compile(GlowShader.Source, "VSMain", "glow.hlsl", "vs_4_0", ShaderFlags.OptimizationLevel3, EffectFlags.None);
        var psCode = Compiler.Compile(GlowShader.Source, "PSMain", "glow.hlsl", "ps_4_0", ShaderFlags.OptimizationLevel3, EffectFlags.None);
        _vs = _device.CreateVertexShader(vsCode.Span, null);
        _ps = _device.CreatePixelShader(psCode.Span, null);
        _constants = _device.CreateBuffer(GlowConstants.Size, BindFlags.ConstantBuffer, ResourceUsage.Default,
            CpuAccessFlags.None, ResourceOptionFlags.None, 0);
        _linearSampler = _device.CreateSamplerState(SamplerDescription.LinearClamp);
    }

    private void DisposeDevice()
    {
        _linearSampler?.Dispose(); _linearSampler = null;
        _constants?.Dispose(); _constants = null;
        _ps?.Dispose(); _ps = null;
        _vs?.Dispose(); _vs = null;
        _dcomp?.Dispose(); _dcomp = null;
        _context?.Dispose(); _context = null;
        _device?.Dispose(); _device = null;
        _factory?.Dispose(); _factory = null;
    }

    private void BuildOverlays(GlowSettings settings)
    {
        var monitors = Monitors.GetAll();
        foreach (var m in monitors)
            Log.Info($"Monitor {m.DeviceName}: {m.Width}x{m.Height} at ({m.X},{m.Y}), scale {m.Scale:0.00}{(m.IsPrimary ? ", primary" : "")}");

        // All screens, or per-monitor choices if any are saved, else the primary monitor only.
        var enabled = settings.AllScreens ? monitors
            : settings.Monitors.Count == 0 ? monitors.Where(m => m.IsPrimary)
            : monitors.Where(m => settings.Monitors.TryGetValue(m.DeviceName, out var s) && s.Enabled);

        foreach (var monitor in enabled)
        {
            var overlay = new MonitorOverlay(new OverlayWindow(monitor, HideFromCapture(settings)));
            _dcomp!.CreateTargetForHwnd(overlay.Window.Handle, true, out var target).CheckError();
            overlay.Target = target;
            overlay.Root = _dcomp.CreateVisual();
            overlay.Target!.SetRoot(overlay.Root).CheckError();
            overlay.ResizeStrips(this, settings);
            _overlays.Add(overlay);
        }
        _dcomp!.Commit().CheckError();
        _appliedSettings = settings;
        Log.Info($"Overlays ready on {_overlays.Count} monitor(s); hidden from capture: {HideFromCapture(settings)}.");
    }

    private void DisposeOverlays()
    {
        foreach (var o in _overlays) o.Dispose();
        _overlays.Clear();
    }

    /// <summary>One monitor: its window, DirectComposition target/root, and four edge strips.</summary>
    private sealed class MonitorOverlay(OverlayWindow window) : IDisposable
    {
        public OverlayWindow Window { get; } = window;
        public IDCompositionTarget? Target { get; set; }
        public IDCompositionVisual? Root { get; set; }
        public List<Strip> Strips { get; } = [];
        public bool Visible { get; set; } = true;
        public ScreenSampler? Sampler { get; set; }
        public float Depth { get; private set; }

        /// <summary>Where the light's edge sits, in from the monitor edges: the taskbar's inner edge, or the screen edge.</summary>
        public EdgeInsets Insets { get; private set; }

        public void ResizeStrips(GlowRenderer renderer, GlowSettings settings)
        {
            foreach (var s in Strips) { Root!.RemoveVisual(s.Visual!); s.Dispose(); }
            Strips.Clear();

            var m = Window.Monitor;
            float scale = m.Scale;
            Insets = settings.WrapTaskbar ? m.WorkInsets : default;

            // The rectangle the glow follows; nothing is drawn outside it (so the taskbar stays clear).
            int left = Insets.Left, top = Insets.Top;
            int width = m.Width - Insets.Left - Insets.Right, height = m.Height - Insets.Top - Insets.Bottom;

            // Deep enough for the core, the beat-expanded halo and its tail; the shader fades to 0 before the edge.
            int depth = (int)MathF.Ceiling(settings.ThicknessPx * scale + settings.SpreadPx * scale * 1.35f * 1.6f + 4);
            depth = Math.Clamp(depth, 4, Math.Min(width, height) / 2 - 1);
            Depth = depth;

            Strips.Add(Strip.Create(renderer, Root!, left, top, width, depth));                                       // top
            Strips.Add(Strip.Create(renderer, Root!, left, top + height - depth, width, depth));                      // bottom
            Strips.Add(Strip.Create(renderer, Root!, left, top + depth, depth, height - 2 * depth));                  // left
            Strips.Add(Strip.Create(renderer, Root!, left + width - depth, top + depth, depth, height - 2 * depth));   // right
        }

        public void Dispose()
        {
            foreach (var s in Strips) s.Dispose();
            Strips.Clear();
            Sampler?.Dispose();
            Sampler = null;
            Root?.Dispose();
            Target?.Dispose();
            Window.Dispose();
        }
    }

    /// <summary>One edge strip: a composition swap chain shown by a visual at its offset.</summary>
    private sealed class Strip : IDisposable
    {
        public int X { get; private init; }
        public int Y { get; private init; }
        public int Width { get; private init; }
        public int Height { get; private init; }
        public IDXGISwapChain1? SwapChain { get; private set; }
        public ID3D11RenderTargetView? Target { get; private set; }
        public IDCompositionVisual? Visual { get; private set; }

        public static Strip Create(GlowRenderer r, IDCompositionVisual root, int x, int y, int width, int height)
        {
            var strip = new Strip { X = x, Y = y, Width = Math.Max(1, width), Height = Math.Max(1, height) };
            var desc = new SwapChainDescription1
            {
                Width = (uint)strip.Width,
                Height = (uint)strip.Height,
                Format = Format.B8G8R8A8_UNorm,
                Stereo = false,
                SampleDescription = new SampleDescription(1, 0),
                BufferUsage = Usage.RenderTargetOutput,
                BufferCount = 2,
                Scaling = Scaling.Stretch,
                SwapEffect = SwapEffect.FlipSequential,
                AlphaMode = AlphaMode.Premultiplied,
                Flags = SwapChainFlags.None,
            };
            strip.SwapChain = r._factory!.CreateSwapChainForComposition(r._device!, desc, null);
            using (var buffer = strip.SwapChain.GetBuffer<ID3D11Texture2D>(0))
                strip.Target = r._device!.CreateRenderTargetView(buffer, null);

            strip.Visual = r._dcomp!.CreateVisual();
            strip.Visual.SetContent(strip.SwapChain).CheckError();
            strip.Visual.SetOffsetX(x).CheckError();
            strip.Visual.SetOffsetY(y).CheckError();
            root.AddVisual(strip.Visual, false, null).CheckError();
            return strip;
        }

        public void Dispose()
        {
            Target?.Dispose(); Target = null;
            SwapChain?.Dispose(); SwapChain = null;
            Visual?.Dispose(); Visual = null;
        }
    }
}
