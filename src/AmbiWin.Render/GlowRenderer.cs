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
    private volatile bool _suspended;

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
    private Palette _shownPalette;
    private Palette _fadeFrom;
    private Palette _fadeTo;
    private float _fadeElapsed = PaletteFadeSeconds;
    private GlowSettings? _appliedSettings;
    private bool _visible = true;

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

    /// <summary>Hides the glow and parks the render thread (fullscreen apps, lock, display off).</summary>
    public void SetSuspended(bool suspended)
    {
        _suspended = suspended;
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

        while (!_stop)
        {
            if (!OverlayWindow.PumpMessages()) break;

            if (OverlayWindow.DisplayChanged)
            {
                OverlayWindow.DisplayChanged = false;
                Log.Info("Display configuration changed; rebuilding overlays.");
                DisposeOverlays();
                BuildOverlays(_settings.Value);
            }

            double now = clock.Elapsed.TotalSeconds;
            float dt = (float)Math.Clamp(now - last, 0, 0.1);
            last = now;

            var settings = _settings.Value;
            bool dirty = ApplySettings(settings);

            bool show = settings.Enabled && !_suspended && _overlays.Count > 0;
            if (show != _visible)
            {
                foreach (var o in _overlays) o.Window.Show(show);
                _visible = show;
            }

            if (!show)
            {
                _wake.Wait(TimeSpan.FromMilliseconds(250));
                last = clock.Elapsed.TotalSeconds;
                continue;
            }

            // Targets for this frame.
            var audio = _audio.Value;
            bool music = settings.Animation == AnimationMode.MusicSync;
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

            bool active = music && !audio.Silent;
            bool settling = Math.Abs(_level.Value - targetLevel) > 0.002f || _pulse.Value > 0.002f;
            if (active) _phase = (_phase + dt * 0.01f) % 1f; // slow drift of the gradient while music plays

            if (active || settling || fading || dirty)
            {
                // Frame cap: draw on every Nth compositor tick so the rate stays near TargetFps on any refresh rate
                // (144 Hz → 72 fps, 120 Hz → 60, 60 Hz → 60). Half a tick of slack keeps frames on tick boundaries.
                if (dirty || now >= lastFrameAt + 1.0 / TargetFps - tickSeconds * 0.5)
                {
                    long start = Stopwatch.GetTimestamp();
                    if (!RenderFrame(settings)) RecoverDevice();
                    busySeconds += Stopwatch.GetElapsedTime(start).TotalSeconds;
                    FramesRendered++;
                    lastFrameAt = now;
                }
                _ = Native.DwmFlush(); // wait for the next compositor tick
                double afterFlush = clock.Elapsed.TotalSeconds;
                double tick = afterFlush - lastFlushAt;
                if (tick > 0.002 && tick < 0.05) tickSeconds += (tick - tickSeconds) * 0.1; // smoothed refresh period
                lastFlushAt = afterFlush;
            }
            else
            {
                // Parked: the last frame stays on screen. Wake on any new snapshot.
                _wake.Wait(TimeSpan.FromMilliseconds(200));
            }

            if (now >= statsAt)
            {
                long frames = FramesRendered - framesAtStats;
                Log.Info($"Render: {frames / 10.0:0.0} fps over 10 s, {(frames > 0 ? busySeconds / frames * 1000 : 0):0.00} ms CPU/frame, " +
                         $"{(active ? "active" : settling || fading ? "settling" : "parked")}, level {_level.Value:0.00}, " +
                         $"refresh {1 / tickSeconds:0} Hz, process CPU {CpuPercent():0.0}%, overlays {_overlays.Count}");
                framesAtStats = FramesRendered;
                busySeconds = 0;
                statsAt = now + 10;
            }
        }
    }

    /// <summary>Applies settings that change geometry or window state. Returns true when a redraw is needed.</summary>
    private bool ApplySettings(GlowSettings settings)
    {
        if (ReferenceEquals(settings, _appliedSettings)) return false;
        var previous = _appliedSettings;
        _appliedSettings = settings;
        if (previous is null) return true;

        bool geometryChanged = previous.ThicknessPx != settings.ThicknessPx || previous.SpreadPx != settings.SpreadPx;
        bool monitorsChanged = !previous.Monitors.SequenceEqual(settings.Monitors);
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
                if (o.Window.HiddenFromCapture != settings.HideFromCapture) o.Window.SetHiddenFromCapture(settings.HideFromCapture);
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

        var a = _shownPalette.Primary.ToLinearSrgb();
        var b = _shownPalette.Secondary.ToLinearSrgb();

        foreach (var overlay in _overlays)
        {
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
            };

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
    }

    private void DisposeDevice()
    {
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

        // No per-monitor choices saved yet → primary monitor only.
        var enabled = settings.Monitors.Count == 0
            ? monitors.Where(m => m.IsPrimary)
            : monitors.Where(m => settings.Monitors.TryGetValue(m.DeviceName, out var s) && s.Enabled);

        foreach (var monitor in enabled)
        {
            var overlay = new MonitorOverlay(new OverlayWindow(monitor, settings.HideFromCapture));
            _dcomp!.CreateTargetForHwnd(overlay.Window.Handle, true, out var target).CheckError();
            overlay.Target = target;
            overlay.Root = _dcomp.CreateVisual();
            overlay.Target!.SetRoot(overlay.Root).CheckError();
            overlay.ResizeStrips(this, settings);
            _overlays.Add(overlay);
        }
        _dcomp!.Commit().CheckError();
        _visible = true;
        _appliedSettings = settings;
        Log.Info($"Overlays ready on {_overlays.Count} monitor(s); hidden from capture: {settings.HideFromCapture}.");
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
        public float Depth { get; private set; }

        public void ResizeStrips(GlowRenderer renderer, GlowSettings settings)
        {
            foreach (var s in Strips) { Root!.RemoveVisual(s.Visual!); s.Dispose(); }
            Strips.Clear();

            var m = Window.Monitor;
            float scale = m.Scale;
            // Deep enough for the core, the beat-expanded halo and its tail; the shader fades to 0 before the edge.
            int depth = (int)MathF.Ceiling(settings.ThicknessPx * scale + settings.SpreadPx * scale * 1.35f * 1.6f + 4);
            depth = Math.Clamp(depth, 4, Math.Min(m.Width, m.Height) / 2 - 1);
            Depth = depth;

            Strips.Add(Strip.Create(renderer, Root!, 0, 0, m.Width, depth));                            // top
            Strips.Add(Strip.Create(renderer, Root!, 0, m.Height - depth, m.Width, depth));             // bottom
            Strips.Add(Strip.Create(renderer, Root!, 0, depth, depth, m.Height - 2 * depth));           // left
            Strips.Add(Strip.Create(renderer, Root!, m.Width - depth, depth, depth, m.Height - 2 * depth)); // right
        }

        public void Dispose()
        {
            foreach (var s in Strips) s.Dispose();
            Strips.Clear();
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
