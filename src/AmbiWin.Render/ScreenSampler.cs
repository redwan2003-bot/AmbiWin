using AmbiWin.Core.Color;
using AmbiWin.Core.Diagnostics;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace AmbiWin.Render;

/// <summary>
/// Ambilight mode for one monitor: watches what is on screen with Desktop Duplication and keeps a tiny,
/// smoothed copy of it (about 60×34 pixels) in a texture the glow shader samples, so each part of the glow
/// takes the color of the content next to it.
/// </summary>
/// <remarks>
/// Capture runs on its own D3D11 device created on the GPU that drives this monitor (on hybrid laptops that
/// may not be the GPU that draws the glow). The GPU averages each frame down with mipmaps and only the
/// smallest useful level, a few KB, is read back, one sample later so the CPU never waits for the GPU.
/// The overlay itself is excluded from capture, so the glow never samples its own light.
/// </remarks>
internal sealed unsafe class ScreenSampler : IDisposable
{
    private const double SampleInterval = 1.0 / 30;
    private const int TargetWidth = 48;              // smallest downscaled width we accept
    private const int DxgiErrorWaitTimeout = unchecked((int)0x887A0027);
    private const int DxgiErrorAccessLost = unchecked((int)0x887A0026);

    private readonly MonitorInfo _monitor;
    private readonly ID3D11Device _renderDevice;
    private readonly ScreenColorFilter _filter = new();

    private ID3D11Device? _captureDevice;
    private ID3D11DeviceContext? _captureContext;
    private IDXGIOutputDuplication? _duplication;
    private ID3D11Texture2D? _mipTexture;
    private ID3D11ShaderResourceView? _mipView;
    private readonly ID3D11Texture2D?[] _staging = new ID3D11Texture2D?[2];
    private int _stagingWrite;
    private bool _stagingPending;
    private int _level, _sampleWidth, _sampleHeight;
    private Format _frameFormat;

    private ID3D11Texture2D? _colorTexture;   // on the render device; sampled by the glow shader
    private double _nextSampleAt;
    private double _retryAt;
    private bool _unavailableLogged;

    public ScreenSampler(ID3D11Device renderDevice, MonitorInfo monitor)
    {
        _renderDevice = renderDevice;
        _monitor = monitor;
    }

    /// <summary>The smoothed screen colors (sRGB texture), or null until the first frame arrives.</summary>
    public ID3D11ShaderResourceView? View { get; private set; }

    /// <summary>Captures (at most 30×/s) and eases the colors. Returns true while the colors are changing.</summary>
    public bool Update(ID3D11DeviceContext renderContext, double now, float dt)
    {
        if (_duplication is null && now >= _retryAt)
        {
            if (!TryOpen())
            {
                _retryAt = now + 2;
                return false;
            }
        }

        if (_duplication is not null && now >= _nextSampleAt)
        {
            _nextSampleAt = now + SampleInterval;
            try
            {
                ReadBackPending();
                CaptureFrame();
            }
            catch (SharpGenException ex)
            {
                Log.Warn($"Screen capture on {_monitor.DeviceName} stopped (0x{ex.HResult:X8}); retrying.");
                CloseCapture();
                _retryAt = now + 1;
            }
        }

        bool moving = _filter.Step(dt);
        if (_filter.HasFrame && (moving || View is null)) Upload(renderContext);
        return moving;
    }

    private bool TryOpen()
    {
        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            for (uint a = 0; factory.EnumAdapters1(a, out var adapter).Success; a++)
            {
                using (adapter)
                {
                    for (uint o = 0; adapter.EnumOutputs(o, out var output).Success; o++)
                    {
                        using (output)
                        {
                            if (!string.Equals(output.Description.DeviceName, _monitor.DeviceName, StringComparison.OrdinalIgnoreCase))
                                continue;

                            D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport,
                                [FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0],
                                out _captureDevice, out _captureContext).CheckError();
                            using var output1 = output.QueryInterface<IDXGIOutput1>();
                            _duplication = output1.DuplicateOutput(_captureDevice);

                            var rotation = _duplication.Description.Rotation;
                            if (rotation is not (ModeRotation.Identity or ModeRotation.Unspecified))
                            {
                                // Rotated displays deliver frames in panel orientation; mapping them is a later step.
                                LogUnavailable($"rotated display ({rotation})");
                                CloseCapture();
                                return false;
                            }

                            Log.Info($"Screen colors: capturing {_monitor.DeviceName} on {adapter.Description1.Description}.");
                            _unavailableLogged = false;
                            return true;
                        }
                    }
                }
            }
            LogUnavailable("no matching display output");
        }
        catch (SharpGenException ex)
        {
            // E_ACCESSDENIED on the secure desktop (UAC, lock screen); unsupported on some remote sessions.
            LogUnavailable($"0x{ex.HResult:X8}");
            CloseCapture();
        }
        return false;
    }

    private void LogUnavailable(string reason)
    {
        if (_unavailableLogged) return;
        _unavailableLogged = true;
        Log.Warn($"Screen colors unavailable on {_monitor.DeviceName}: {reason}. Will keep trying.");
    }

    private void CaptureFrame()
    {
        var result = _duplication!.AcquireNextFrame(0, out var info, out var resource);
        if (result.Code == DxgiErrorWaitTimeout) return;          // nothing new on screen
        if (result.Code == DxgiErrorAccessLost) throw new SharpGenException(result);
        result.CheckError();

        try
        {
            if (info.LastPresentTime == 0 && _mipTexture is not null) return; // only the mouse moved

            using var frame = resource.QueryInterface<ID3D11Texture2D>();
            EnsureTextures(frame.Description);
            var ctx = _captureContext!;
            ctx.CopySubresourceRegion(_mipTexture!, 0, 0, 0, 0, frame, 0, null);
            ctx.GenerateMips(_mipView!);
            ctx.CopySubresourceRegion(_staging[_stagingWrite]!, 0, 0, 0, 0, _mipTexture!, (uint)_level, null);
            _stagingPending = true;
        }
        finally
        {
            resource.Dispose();
            _duplication.ReleaseFrame();
        }
    }

    /// <summary>Reads the copy made one sample ago; by now the GPU has long finished it.</summary>
    private void ReadBackPending()
    {
        if (!_stagingPending) return;
        _stagingPending = false;
        var staging = _staging[_stagingWrite]!;
        _stagingWrite ^= 1;

        var ctx = _captureContext!;
        var mapped = ctx.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var bytes = new ReadOnlySpan<byte>((void*)mapped.DataPointer, (int)(mapped.RowPitch * (uint)(_sampleHeight - 1)) + _sampleWidth * 4);
            if (_frameFormat == Format.R8G8B8A8_UNorm) SwapRedBlue(bytes, (int)mapped.RowPitch);
            else _filter.SetFrame(bytes, _sampleWidth, _sampleHeight, (int)mapped.RowPitch);
        }
        finally
        {
            ctx.Unmap(staging, 0);
        }
    }

    private void SwapRedBlue(ReadOnlySpan<byte> rgba, int stride)
    {
        var bgra = new byte[_sampleWidth * _sampleHeight * 4];
        for (int y = 0; y < _sampleHeight; y++)
        for (int x = 0; x < _sampleWidth; x++)
        {
            int s = y * stride + x * 4, d = (y * _sampleWidth + x) * 4;
            bgra[d] = rgba[s + 2]; bgra[d + 1] = rgba[s + 1]; bgra[d + 2] = rgba[s]; bgra[d + 3] = 255;
        }
        _filter.SetFrame(bgra, _sampleWidth, _sampleHeight, _sampleWidth * 4);
    }

    private void EnsureTextures(Texture2DDescription frame)
    {
        if (_mipTexture is not null)
        {
            var current = _mipTexture.Description;
            if (current.Width == frame.Width && current.Height == frame.Height && current.Format == frame.Format) return;
            DisposeCaptureTextures();
        }

        _frameFormat = frame.Format;
        int w = (int)frame.Width, h = (int)frame.Height;
        _level = 0;
        while ((w >> (_level + 1)) >= TargetWidth && (h >> (_level + 1)) >= 8) _level++;
        _sampleWidth = Math.Max(1, w >> _level);
        _sampleHeight = Math.Max(1, h >> _level);

        _mipTexture = _captureDevice!.CreateTexture2D(new Texture2DDescription
        {
            Width = frame.Width,
            Height = frame.Height,
            MipLevels = (uint)(_level + 1),
            ArraySize = 1,
            Format = frame.Format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.GenerateMips,
        });
        _mipView = _captureDevice.CreateShaderResourceView(_mipTexture);

        for (int i = 0; i < _staging.Length; i++)
        {
            _staging[i] = _captureDevice.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)_sampleWidth,
                Height = (uint)_sampleHeight,
                MipLevels = 1,
                ArraySize = 1,
                Format = frame.Format,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Read,
                MiscFlags = ResourceOptionFlags.None,
            });
        }
        _stagingPending = false;
        Log.Info($"Screen colors: {w}x{h} {frame.Format} sampled at {_sampleWidth}x{_sampleHeight} (mip {_level}).");
    }

    private void Upload(ID3D11DeviceContext ctx)
    {
        if (_colorTexture is not null &&
            (_colorTexture.Description.Width != _filter.Width || _colorTexture.Description.Height != _filter.Height))
        {
            View?.Dispose(); View = null;
            _colorTexture.Dispose(); _colorTexture = null;
        }

        if (_colorTexture is null)
        {
            _colorTexture = _renderDevice.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)_filter.Width,
                Height = (uint)_filter.Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.R8G8B8A8_UNorm_SRgb,   // the shader reads linear light
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Dynamic,
                BindFlags = BindFlags.ShaderResource,
                CPUAccessFlags = CpuAccessFlags.Write,
                MiscFlags = ResourceOptionFlags.None,
            });
            View = _renderDevice.CreateShaderResourceView(_colorTexture);
        }

        var mapped = ctx.Map(_colorTexture, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var span = new Span<byte>((void*)mapped.DataPointer, (int)(mapped.RowPitch * (uint)(_filter.Height - 1)) + _filter.Width * 4);
            _filter.WriteRgba8(span, (int)mapped.RowPitch);
        }
        finally
        {
            ctx.Unmap(_colorTexture, 0);
        }
    }

    private void DisposeCaptureTextures()
    {
        for (int i = 0; i < _staging.Length; i++) { _staging[i]?.Dispose(); _staging[i] = null; }
        _mipView?.Dispose(); _mipView = null;
        _mipTexture?.Dispose(); _mipTexture = null;
        _stagingPending = false;
    }

    private void CloseCapture()
    {
        DisposeCaptureTextures();
        _duplication?.Dispose(); _duplication = null;
        _captureContext?.Dispose(); _captureContext = null;
        _captureDevice?.Dispose(); _captureDevice = null;
    }

    public void Dispose()
    {
        CloseCapture();
        View?.Dispose(); View = null;
        _colorTexture?.Dispose(); _colorTexture = null;
    }
}
