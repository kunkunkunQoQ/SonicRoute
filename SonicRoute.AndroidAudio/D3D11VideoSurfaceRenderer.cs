using System.Runtime.InteropServices;

namespace SonicRoute.AndroidAudio;

public enum VideoColorMatrix
{
    Bt601Limited = 0,
    Bt709Limited = 1,
    Bt601Full = 2
}

/// <summary>
/// Zero-idle-timer Direct3D 11 + DXGI swapchain video presenter.
/// Consumes hardware <c>DXGI_FORMAT_NV12</c> texture slices directly on GPU via <c>CopySubresourceRegion</c>
/// (or uploads software NV12 planes) and renders only when a frame arrives, the window resizes, or <c>WM_PAINT</c> fires.
/// </summary>
public sealed class D3D11VideoSurfaceRenderer : IDisposable
{
    private static readonly Guid IidId3d11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
    private static readonly Guid IidId3d10Multithread = new("9b7e4e00-342c-4106-a19f-4f2704f689f0");
    private static readonly Guid IidIdxgiDevice1 = new("77db970f-6276-48ba-ba28-070143b4392c");
    private static readonly Guid IidIdxgiDevice3 = new("6007896c-3244-4afd-bf18-a6d3beda5023");
    private static readonly Guid IidIdxgiFactory2 = new("50c83a1c-e072-4c48-87b0-3630fa36a6d0");

    private const int D3DDriverTypeHardware = 1;
    private const int D3DDriverTypeWarp = 5;
    private const uint D3D11SdkVersion = 7;
    private const uint D3D11CreateDeviceBgraSupport = 0x20;
    private const uint D3D11CreateDeviceVideoSupport = 0x800;

    private const int DxgiFormatR8G8Unorm = 49;
    private const int DxgiFormatR8Unorm = 61;
    private const int DxgiFormatB8G8R8A8Unorm = 87;
    private const int DxgiFormatNv12 = 103;
    private const uint DxgiUsageRenderTargetOutput = 0x20;
    private const int DxgiScalingStretch = 0;
    private const int DxgiSwapEffectFlipDiscard = 4;
    private const int DxgiSwapEffectFlipSequential = 3;
    private const int DxgiSwapEffectDiscard = 0;
    private const int DxgiAlphaModeIgnore = 3;

    private const uint D3D11BindShaderResource = 0x8;
    private const uint D3D11BindConstantBuffer = 0x4;
    private const int D3D11UsageDefault = 0;
    private const int D3D11UsageDynamic = 2;
    private const uint D3D11CpuAccessWrite = 0x10000;
    private const int D3D11MapWriteDiscard = 4;
    private const int D3D11SrvDimensionTexture2D = 4;
    private const int D3D11PrimitiveTopologyTriangleList = 4;
    private const int D3D11FilterMinMagMipLinear = 0x15;
    private const int D3D11TextureAddressClamp = 3;
    private const int D3D11ComparisonNever = 1;

    private static int _activeInstanceCount;

    private readonly object _renderGate = new();
    private readonly nint _blackColorPtr;
    private readonly bool _isMirrorMode;

    private ComPtr _device = new();
    private ComPtr _context = new();
    private ComPtr _swapChain = new();
    private ComPtr _rtv = new();
    private ComPtr _vs = new();
    private ComPtr _ps = new();
    private ComPtr _sampler = new();
    private ComPtr _constantBuffer = new();

    // Hardware path cached NV12 texture + Y/UV SRVs
    private ComPtr _hwNv12Tex = new();
    private ComPtr _hwYSrv = new();
    private ComPtr _hwUvSrv = new();
    private int _hwTexW;
    private int _hwTexH;

    // Software fallback path cached R8 + R8G8 dynamic textures + SRVs
    private ComPtr _swYTex = new();
    private ComPtr _swUvTex = new();
    private ComPtr _swYSrv = new();
    private ComPtr _swUvSrv = new();
    private int _swTexW;
    private int _swTexH;

    private int _swapWidth;
    private int _swapHeight;
    private int _frameCropX;
    private int _frameCropY;
    private int _frameCropW;
    private int _frameCropH;
    private VideoColorMatrix _colorMatrix = VideoColorMatrix.Bt601Limited;
    private bool _hasHwFrame;
    private bool _hasSwFrame;
    private bool _counted;
    private bool _disposed;

    public D3D11VideoSurfaceRenderer(nint hwnd, int initialWidth, int initialHeight, bool isMirrorMode = false)
    {
        if (hwnd == 0) throw new ArgumentException("Window handle cannot be zero.", nameof(hwnd));
        _isMirrorMode = isMirrorMode;
        Interlocked.Increment(ref _activeInstanceCount);
        _counted = true;

        _blackColorPtr = Marshal.AllocHGlobal(16);
        Marshal.Copy(new[] { 0f, 0f, 0f, 1f }, 0, _blackColorPtr, 4);

        try
        {
            CreateDeviceAndContext();
            CreatePipelineResources();
            CreateSwapChainForWindow(hwnd, Math.Max(1, initialWidth), Math.Max(1, initialHeight));
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public static int ActiveInstanceCount => Volatile.Read(ref _activeInstanceCount);
    public nint DeviceHandle => _device.Handle;
    public int SwapWidth => _swapWidth;
    public int SwapHeight => _swapHeight;
    public bool IsMirrorMode => _isMirrorMode;
    public bool HasPresentedFrame => _hasHwFrame || _hasSwFrame;

    internal VideoRect ComputeRenderRect()
        => _isMirrorMode
            ? VideoCoordinateMapper.ComputeContentRect(_swapWidth, _swapHeight, _frameCropW, _frameCropH)
            : new VideoRect(0, 0, Math.Max(1, _swapWidth), Math.Max(1, _swapHeight));

    private void CreateDeviceAndContext()
    {
        int[] featureLevels = [0xB100, 0xB000, 0xA100, 0xA000]; // 11.1, 11.0, 10.1, 10.0
        uint[] flagsToTry =
        [
            D3D11CreateDeviceBgraSupport | D3D11CreateDeviceVideoSupport,
            D3D11CreateDeviceBgraSupport
        ];

        nint dev = 0, ctx = 0;
        int lastHr = -1;
        foreach (int driver in new[] { D3DDriverTypeHardware, D3DDriverTypeWarp })
        {
            foreach (uint flags in flagsToTry)
            {
                if (driver == D3DDriverTypeWarp && (flags & D3D11CreateDeviceVideoSupport) != 0) continue;
                lastHr = D3D11CreateDevice(
                    0, driver, 0, flags, featureLevels, featureLevels.Length,
                    D3D11SdkVersion, out dev, out _, out ctx);
                if (lastHr >= 0 && dev != 0 && ctx != 0) break;
                ComVtbl.SafeRelease(ref dev);
                ComVtbl.SafeRelease(ref ctx);
            }
            if (dev != 0 && ctx != 0) break;
        }

        ComVtbl.ThrowIfFailed(lastHr, "D3D11CreateDevice");
        _device = new ComPtr(dev);
        _context = new ComPtr(ctx);

        if (_device.TryQueryInterface(in IidId3d10Multithread, out var mt) && mt != null)
        {
            using (mt) ComVtbl.Get<SetMultithreadProtectedFn>(mt.Handle, 5)(mt.Handle, 1);
        }

        if (_device.TryQueryInterface(in IidIdxgiDevice1, out var dxgiDev) && dxgiDev != null)
        {
            using (dxgiDev) ComVtbl.Get<SetMaxFrameLatencyFn>(dxgiDev.Handle, 12)(dxgiDev.Handle, 1);
        }
    }

    private void CreatePipelineResources()
    {
        unsafe
        {
            ReadOnlySpan<byte> vsBytes = VertexShaderBytecode;
            ReadOnlySpan<byte> psBytes = PixelShaderBytecode;
            fixed (byte* pVs = vsBytes)
            fixed (byte* pPs = psBytes)
            {
                int hr = ComVtbl.Get<CreateVertexShaderFn>(_device.Handle, 12)(
                    _device.Handle, (nint)pVs, vsBytes.Length, 0, out nint vsPtr);
                ComVtbl.ThrowIfFailed(hr, "CreateVertexShader");
                _vs = new ComPtr(vsPtr);

                hr = ComVtbl.Get<CreatePixelShaderFn>(_device.Handle, 15)(
                    _device.Handle, (nint)pPs, psBytes.Length, 0, out nint psPtr);
                ComVtbl.ThrowIfFailed(hr, "CreatePixelShader");
                _ps = new ComPtr(psPtr);
            }
        }

        var samp = new D3D11SamplerDesc
        {
            Filter = D3D11FilterMinMagMipLinear,
            AddressU = D3D11TextureAddressClamp,
            AddressV = D3D11TextureAddressClamp,
            AddressW = D3D11TextureAddressClamp,
            ComparisonFunc = D3D11ComparisonNever,
            MaxLOD = float.MaxValue
        };
        int sampHr = ComVtbl.Get<CreateSamplerStateFn>(_device.Handle, 23)(_device.Handle, in samp, out nint sampPtr);
        ComVtbl.ThrowIfFailed(sampHr, "CreateSamplerState");
        _sampler = new ComPtr(sampPtr);

        var cbDesc = new D3D11BufferDesc
        {
            ByteWidth = 32,
            Usage = D3D11UsageDynamic,
            BindFlags = D3D11BindConstantBuffer,
            CPUAccessFlags = D3D11CpuAccessWrite
        };
        int cbHr = ComVtbl.Get<CreateBufferFn>(_device.Handle, 3)(_device.Handle, in cbDesc, 0, out nint cbPtr);
        ComVtbl.ThrowIfFailed(cbHr, "CreateConstantBuffer");
        _constantBuffer = new ComPtr(cbPtr);
    }

    private void CreateSwapChainForWindow(nint hwnd, int width, int height)
    {
        using var dxgiDevice = _device.QueryInterface(in IidIdxgiDevice1);
        int hr = ComVtbl.Get<GetAdapterFn>(dxgiDevice.Handle, 7)(dxgiDevice.Handle, out nint adapterPtr);
        ComVtbl.ThrowIfFailed(hr, "IDXGIDevice.GetAdapter");
        using var adapter = new ComPtr(adapterPtr);

        Guid factoryIid = IidIdxgiFactory2;
        hr = ComVtbl.Get<GetParentFn>(adapter.Handle, 6)(adapter.Handle, in factoryIid, out nint factoryPtr);
        ComVtbl.ThrowIfFailed(hr, "IDXGIAdapter.GetParent");
        using var factory = new ComPtr(factoryPtr);

        _swapWidth = width;
        _swapHeight = height;

        foreach (int effect in new[] { DxgiSwapEffectFlipDiscard, DxgiSwapEffectFlipSequential, DxgiSwapEffectDiscard })
        {
            uint bufferCount = effect == DxgiSwapEffectDiscard ? 1u : 2u;
            var desc = new DxgiSwapChainDesc1
            {
                Width = (uint)width,
                Height = (uint)height,
                Format = DxgiFormatB8G8R8A8Unorm,
                SampleCount = 1,
                SampleQuality = 0,
                BufferUsage = DxgiUsageRenderTargetOutput,
                BufferCount = bufferCount,
                Scaling = DxgiScalingStretch,
                SwapEffect = effect,
                AlphaMode = DxgiAlphaModeIgnore,
                Flags = 0
            };

            hr = ComVtbl.Get<CreateSwapChainForHwndFn>(factory.Handle, 15)(
                factory.Handle, _device.Handle, hwnd, in desc, 0, 0, out nint scPtr);
            if (hr >= 0 && scPtr != 0)
            {
                _swapChain = new ComPtr(scPtr);
                break;
            }
            ComVtbl.SafeRelease(ref scPtr);
        }

        if (_swapChain.IsNull) ComVtbl.ThrowIfFailed(hr, "CreateSwapChainForHwnd");
        RebuildRenderTargetView();
    }

    private void RebuildRenderTargetView()
    {
        _rtv.Dispose();
        Guid texIid = IidId3d11Texture2D;
        int hr = ComVtbl.Get<GetBufferFn>(_swapChain.Handle, 9)(_swapChain.Handle, 0, in texIid, out nint backBufPtr);
        ComVtbl.ThrowIfFailed(hr, "IDXGISwapChain.GetBuffer");
        using var backBuf = new ComPtr(backBufPtr);

        hr = ComVtbl.Get<CreateRenderTargetViewFn>(_device.Handle, 9)(_device.Handle, backBuf.Handle, 0, out nint rtvPtr);
        ComVtbl.ThrowIfFailed(hr, "CreateRenderTargetView");
        _rtv = new ComPtr(rtvPtr);
    }

    public void Resize(int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        lock (_renderGate)
        {
            if (_disposed || _swapChain.IsNull) return;
            if (width == _swapWidth && height == _swapHeight) return;

            nint zero = 0;
            ComVtbl.Get<OmSetRenderTargetsFn>(_context.Handle, 33)(_context.Handle, 0, in zero, 0);
            _rtv.Dispose();

            int hr = ComVtbl.Get<ResizeBuffersFn>(_swapChain.Handle, 13)(
                _swapChain.Handle, 0, (uint)width, (uint)height, DxgiFormatB8G8R8A8Unorm, 0);
            if (hr < 0) return;

            _swapWidth = width;
            _swapHeight = height;
            RebuildRenderTargetView();
            RenderLocked();
        }
    }

    public void RenderLastFrame()
    {
        lock (_renderGate)
        {
            if (_disposed || _swapChain.IsNull || _rtv.IsNull) return;
            RenderLocked();
        }
    }

    public void PresentHardwareTexture(
        nint srcTexture, uint arraySlice, int codedWidth, int codedHeight,
        int cropX, int cropY, int cropWidth, int cropHeight, VideoColorMatrix matrix)
    {
        if (srcTexture == 0 || codedWidth <= 0 || codedHeight <= 0) return;
        lock (_renderGate)
        {
            if (_disposed) return;
            EnsureHardwareStagingTexture(codedWidth, codedHeight);
            ComVtbl.Get<CopySubresourceRegionFn>(_context.Handle, 46)(
                _context.Handle, _hwNv12Tex.Handle, 0, 0, 0, 0, srcTexture, arraySlice, 0);

            _hasHwFrame = true;
            _hasSwFrame = false;
            _frameCropX = cropX;
            _frameCropY = cropY;
            _frameCropW = cropWidth > 0 ? cropWidth : codedWidth;
            _frameCropH = cropHeight > 0 ? cropHeight : codedHeight;
            _colorMatrix = matrix;
            RenderLocked();
        }
    }

    public void PresentSoftwareNv12(
        ReadOnlySpan<byte> nv12Contiguous, int stride, int codedHeight,
        int cropX, int cropY, int cropWidth, int cropHeight, VideoColorMatrix matrix)
    {
        if (stride <= 0 || codedHeight <= 0 || cropWidth <= 0 || cropHeight <= 0) return;
        int copyW = Math.Clamp(cropX + cropWidth, 2, stride);
        int uvHeight = (codedHeight + 1) / 2;
        if (nv12Contiguous.Length < stride * (codedHeight + uvHeight)) return;

        lock (_renderGate)
        {
            if (_disposed) return;
            EnsureSoftwareTextures(copyW, codedHeight);

            unsafe
            {
                fixed (byte* basePtr = nv12Contiguous)
                {
                    UploadPlane(_swYTex.Handle, (nint)basePtr, stride, copyW, codedHeight);
                    nint uvPtr = (nint)(basePtr + (nint)stride * codedHeight);
                    UploadPlane(_swUvTex.Handle, uvPtr, stride, ((copyW + 1) / 2) * 2, uvHeight);
                }
            }

            _hasSwFrame = true;
            _hasHwFrame = false;
            _frameCropX = cropX;
            _frameCropY = cropY;
            _frameCropW = cropWidth;
            _frameCropH = cropHeight;
            _colorMatrix = matrix;
            RenderLocked();
        }
    }

    private void UploadPlane(nint texture, nint srcPtr, int srcStride, int rowBytes, int rows)
    {
        int hr = ComVtbl.Get<MapFn>(_context.Handle, 14)(_context.Handle, texture, 0, D3D11MapWriteDiscard, 0, out var mapped);
        if (hr < 0) return;
        try
        {
            int copyBytes = Math.Min(rowBytes, (int)mapped.RowPitch);
            if (copyBytes <= 0) return;
            unsafe
            {
                byte* dstBase = (byte*)mapped.pData;
                byte* srcBase = (byte*)srcPtr;
                nint dstPitch = (nint)mapped.RowPitch;
                nint srcPitch = srcStride;
                for (int y = 0; y < rows; y++)
                {
                    Buffer.MemoryCopy(srcBase + (nint)y * srcPitch, dstBase + (nint)y * dstPitch, copyBytes, copyBytes);
                }
            }
        }
        finally
        {
            ComVtbl.Get<UnmapFn>(_context.Handle, 15)(_context.Handle, texture, 0);
        }
    }

    private void EnsureHardwareStagingTexture(int width, int height)
    {
        if (!_hwNv12Tex.IsNull && _hwTexW == width && _hwTexH == height) return;
        _hwYSrv.Dispose();
        _hwUvSrv.Dispose();
        _hwNv12Tex.Dispose();

        // Width/Height for NV12 must be multiples of 2.
        int texW = (width + 1) & ~1;
        int texH = (height + 1) & ~1;

        var texDesc = new D3D11Texture2DDesc
        {
            Width = (uint)texW,
            Height = (uint)texH,
            MipLevels = 1,
            ArraySize = 1,
            Format = DxgiFormatNv12,
            SampleCount = 1,
            SampleQuality = 0,
            Usage = D3D11UsageDefault,
            BindFlags = D3D11BindShaderResource,
            CPUAccessFlags = 0,
            MiscFlags = 0
        };
        int hr = ComVtbl.Get<CreateTexture2DFn>(_device.Handle, 5)(_device.Handle, in texDesc, 0, out nint texPtr);
        ComVtbl.ThrowIfFailed(hr, "CreateHardwareStagingNV12");
        _hwNv12Tex = new ComPtr(texPtr);

        var yDesc = new D3D11ShaderResourceViewDesc
        {
            Format = DxgiFormatR8Unorm,
            ViewDimension = D3D11SrvDimensionTexture2D,
            MostDetailedMip = 0,
            MipLevels = 1
        };
        hr = ComVtbl.Get<CreateSrvFn>(_device.Handle, 7)(_device.Handle, _hwNv12Tex.Handle, in yDesc, out nint ySrvPtr);
        ComVtbl.ThrowIfFailed(hr, "CreateNv12YSrv");
        _hwYSrv = new ComPtr(ySrvPtr);

        var uvDesc = new D3D11ShaderResourceViewDesc
        {
            Format = DxgiFormatR8G8Unorm,
            ViewDimension = D3D11SrvDimensionTexture2D,
            MostDetailedMip = 0,
            MipLevels = 1
        };
        hr = ComVtbl.Get<CreateSrvFn>(_device.Handle, 7)(_device.Handle, _hwNv12Tex.Handle, in uvDesc, out nint uvSrvPtr);
        ComVtbl.ThrowIfFailed(hr, "CreateNv12UvSrv");
        _hwUvSrv = new ComPtr(uvSrvPtr);

        _hwTexW = width;
        _hwTexH = height;
    }

    private void EnsureSoftwareTextures(int width, int height)
    {
        if (!_swYTex.IsNull && _swTexW == width && _swTexH == height) return;
        _swYSrv.Dispose();
        _swUvSrv.Dispose();
        _swYTex.Dispose();
        _swUvTex.Dispose();

        int uvW = Math.Max(1, (width + 1) / 2);
        int uvH = Math.Max(1, (height + 1) / 2);

        var yDesc = new D3D11Texture2DDesc
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = DxgiFormatR8Unorm,
            SampleCount = 1,
            SampleQuality = 0,
            Usage = D3D11UsageDynamic,
            BindFlags = D3D11BindShaderResource,
            CPUAccessFlags = D3D11CpuAccessWrite
        };
        int hr = ComVtbl.Get<CreateTexture2DFn>(_device.Handle, 5)(_device.Handle, in yDesc, 0, out nint yTexPtr);
        ComVtbl.ThrowIfFailed(hr, "CreateSoftwareYTex");
        _swYTex = new ComPtr(yTexPtr);

        var ySrvDesc = new D3D11ShaderResourceViewDesc { Format = DxgiFormatR8Unorm, ViewDimension = D3D11SrvDimensionTexture2D, MipLevels = 1 };
        hr = ComVtbl.Get<CreateSrvFn>(_device.Handle, 7)(_device.Handle, _swYTex.Handle, in ySrvDesc, out nint ySrvPtr);
        ComVtbl.ThrowIfFailed(hr, "CreateSoftwareYSrv");
        _swYSrv = new ComPtr(ySrvPtr);

        var uvDesc = yDesc with { Width = (uint)uvW, Height = (uint)uvH, Format = DxgiFormatR8G8Unorm };
        hr = ComVtbl.Get<CreateTexture2DFn>(_device.Handle, 5)(_device.Handle, in uvDesc, 0, out nint uvTexPtr);
        ComVtbl.ThrowIfFailed(hr, "CreateSoftwareUvTex");
        _swUvTex = new ComPtr(uvTexPtr);

        var uvSrvDesc = new D3D11ShaderResourceViewDesc { Format = DxgiFormatR8G8Unorm, ViewDimension = D3D11SrvDimensionTexture2D, MipLevels = 1 };
        hr = ComVtbl.Get<CreateSrvFn>(_device.Handle, 7)(_device.Handle, _swUvTex.Handle, in uvSrvDesc, out nint uvSrvPtr);
        ComVtbl.ThrowIfFailed(hr, "CreateSoftwareUvSrv");
        _swUvSrv = new ComPtr(uvSrvPtr);

        _swTexW = width;
        _swTexH = height;
    }

    private void RenderLocked()
    {
        nint rtv = _rtv.Handle;
        ComVtbl.Get<OmSetRenderTargetsFn>(_context.Handle, 33)(_context.Handle, 1, in rtv, 0);
        ComVtbl.Get<ClearRenderTargetViewFn>(_context.Handle, 50)(_context.Handle, rtv, _blackColorPtr);

        if (_hasHwFrame || _hasSwFrame)
        {
            int texW = _hasHwFrame ? ((_hwTexW + 1) & ~1) : _swTexW;
            int texH = _hasHwFrame ? ((_hwTexH + 1) & ~1) : _swTexH;
            if (texW > 0 && texH > 0)
            {
                var contentRect = ComputeRenderRect();
                var vp = new D3D11Viewport
                {
                    TopLeftX = contentRect.X,
                    TopLeftY = contentRect.Y,
                    Width = Math.Max(1, contentRect.Width),
                    Height = Math.Max(1, contentRect.Height),
                    MinDepth = 0f,
                    MaxDepth = 1f
                };
                ComVtbl.Get<RsSetViewportsFn>(_context.Handle, 44)(_context.Handle, 1, in vp);

                UpdateConstants(
                    (float)_frameCropX / texW,
                    (float)_frameCropY / texH,
                    (float)_frameCropW / texW,
                    (float)_frameCropH / texH,
                    (int)_colorMatrix);

                ComVtbl.Get<IaSetInputLayoutFn>(_context.Handle, 17)(_context.Handle, 0);
                ComVtbl.Get<IaSetPrimitiveTopologyFn>(_context.Handle, 24)(_context.Handle, D3D11PrimitiveTopologyTriangleList);
                ComVtbl.Get<VsSetShaderFn>(_context.Handle, 11)(_context.Handle, _vs.Handle, 0, 0);
                ComVtbl.Get<PsSetShaderFn>(_context.Handle, 9)(_context.Handle, _ps.Handle, 0, 0);

                nint samp = _sampler.Handle;
                ComVtbl.Get<PsSetSamplersFn>(_context.Handle, 10)(_context.Handle, 0, 1, in samp);

                nint cb = _constantBuffer.Handle;
                ComVtbl.Get<VsSetConstantBuffersFn>(_context.Handle, 7)(_context.Handle, 0, 1, in cb);
                ComVtbl.Get<PsSetConstantBuffersFn>(_context.Handle, 16)(_context.Handle, 0, 1, in cb);

                nint ySrv = _hasHwFrame ? _hwYSrv.Handle : _swYSrv.Handle;
                nint uvSrv = _hasHwFrame ? _hwUvSrv.Handle : _swUvSrv.Handle;
                ComVtbl.Get<PsSetShaderResourcesFn>(_context.Handle, 8)(_context.Handle, 0, 1, in ySrv);
                ComVtbl.Get<PsSetShaderResourcesFn>(_context.Handle, 8)(_context.Handle, 1, 1, in uvSrv);

                ComVtbl.Get<DrawFn>(_context.Handle, 13)(_context.Handle, 3, 0);

                // Unbind SRVs before next Decode/Copy to avoid D3D11 hazard warnings.
                nint nullSrv = 0;
                ComVtbl.Get<PsSetShaderResourcesFn>(_context.Handle, 8)(_context.Handle, 0, 1, in nullSrv);
                ComVtbl.Get<PsSetShaderResourcesFn>(_context.Handle, 8)(_context.Handle, 1, 1, in nullSrv);
            }
        }

        ComVtbl.Get<PresentFn>(_swapChain.Handle, 8)(_swapChain.Handle, 0, 0);
    }

    private void UpdateConstants(float u0, float v0, float uScale, float vScale, int matrixMode)
    {
        if (ComVtbl.Get<MapFn>(_context.Handle, 14)(_context.Handle, _constantBuffer.Handle, 0, D3D11MapWriteDiscard, 0, out var mapped) < 0)
            return;
        try
        {
            var data = new ShaderConstants
            {
                U0 = u0,
                V0 = v0,
                UScale = uScale,
                VScale = vScale,
                MatrixMode = matrixMode
            };
            Marshal.StructureToPtr(data, mapped.pData, false);
        }
        finally
        {
            ComVtbl.Get<UnmapFn>(_context.Handle, 15)(_context.Handle, _constantBuffer.Handle, 0);
        }
    }

    /*
     * Precompiled offline with Windows SDK fxc.exe (/O3 /Qstrip_reflect /Qstrip_debug) to avoid
     * loading d3dcompiler_47.dll (4.5 MB) at runtime. Original HLSL source for future updates:
     *
     * cbuffer VideoParams : register(b0) { float2 uvOffset; float2 uvScale; int matrixMode; float3 _pad; };
     * struct VSOut { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };
     *
     * VSOut VSMain(uint id : SV_VertexID) {
     *     VSOut o;
     *     float2 tex = float2((id << 1) & 2, id & 2);
     *     o.pos = float4(tex.x * 2.0f - 1.0f, 1.0f - tex.y * 2.0f, 0.0f, 1.0f);
     *     o.uv = uvOffset + tex * uvScale;
     *     return o;
     * }
     *
     * Texture2D<float> yTex : register(t0); Texture2D<float2> uvTex : register(t1); SamplerState linSamp : register(s0);
     *
     * float4 PSMain(VSOut i) : SV_TARGET {
     *     float rawY = yTex.Sample(linSamp, i.uv);
     *     float2 rawUV = uvTex.Sample(linSamp, i.uv);
     *     float cb = (rawUV.x - (128.0f / 255.0f));
     *     float cr = (rawUV.y - (128.0f / 255.0f));
     *     float3 rgb;
     *     if (matrixMode == 2) {
     *         float y = rawY;
     *         rgb = float3(y + 1.402f * cr, y - 0.344136f * cb - 0.714136f * cr, y + 1.772f * cb);
     *     } else {
     *         float y = (rawY - (16.0f / 255.0f)) * (255.0f / 219.0f);
     *         cb *= (255.0f / 224.0f); cr *= (255.0f / 224.0f);
     *         if (matrixMode == 1)
     *             rgb = float3(y + 1.5748f * cr, y - 0.187324f * cb - 0.468124f * cr, y + 1.8556f * cb);
     *         else
     *             rgb = float3(y + 1.402f * cr, y - 0.344136f * cb - 0.714136f * cr, y + 1.772f * cb);
     *     }
     *     return float4(saturate(rgb), 1.0f);
     * }
     */
    private static ReadOnlySpan<byte> VertexShaderBytecode =>
    [
        0x44, 0x58, 0x42, 0x43, 0xB3, 0xAE, 0x7D, 0x4A, 0x7F, 0x05, 0xDC, 0x0C, 0x82, 0x1A, 0x55, 0x8D,
        0xDD, 0xD8, 0x21, 0x5D, 0x01, 0x00, 0x00, 0x00, 0x24, 0x02, 0x00, 0x00, 0x03, 0x00, 0x00, 0x00,
        0x2C, 0x00, 0x00, 0x00, 0x60, 0x00, 0x00, 0x00, 0xB8, 0x00, 0x00, 0x00, 0x49, 0x53, 0x47, 0x4E,
        0x2C, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x08, 0x00, 0x00, 0x00, 0x20, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x06, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x01, 0x01, 0x00, 0x00, 0x53, 0x56, 0x5F, 0x56, 0x65, 0x72, 0x74, 0x65, 0x78, 0x49, 0x44, 0x00,
        0x4F, 0x53, 0x47, 0x4E, 0x50, 0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00, 0x08, 0x00, 0x00, 0x00,
        0x38, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x03, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x0F, 0x00, 0x00, 0x00, 0x44, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x03, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x03, 0x0C, 0x00, 0x00,
        0x53, 0x56, 0x5F, 0x50, 0x4F, 0x53, 0x49, 0x54, 0x49, 0x4F, 0x4E, 0x00, 0x54, 0x45, 0x58, 0x43,
        0x4F, 0x4F, 0x52, 0x44, 0x00, 0xAB, 0xAB, 0xAB, 0x53, 0x48, 0x44, 0x52, 0x64, 0x01, 0x00, 0x00,
        0x40, 0x00, 0x01, 0x00, 0x59, 0x00, 0x00, 0x00, 0x59, 0x00, 0x00, 0x04, 0x46, 0x8E, 0x20, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x60, 0x00, 0x00, 0x04, 0x12, 0x10, 0x10, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x06, 0x00, 0x00, 0x00, 0x67, 0x00, 0x00, 0x04, 0xF2, 0x20, 0x10, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x65, 0x00, 0x00, 0x03, 0x32, 0x20, 0x10, 0x00,
        0x01, 0x00, 0x00, 0x00, 0x68, 0x00, 0x00, 0x02, 0x01, 0x00, 0x00, 0x00, 0x29, 0x00, 0x00, 0x07,
        0x12, 0x00, 0x10, 0x00, 0x00, 0x00, 0x00, 0x00, 0x0A, 0x10, 0x10, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x01, 0x40, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x07, 0x12, 0x00, 0x10, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x0A, 0x00, 0x10, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x40, 0x00, 0x00,
        0x02, 0x00, 0x00, 0x00, 0x56, 0x00, 0x00, 0x05, 0x12, 0x00, 0x10, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x0A, 0x00, 0x10, 0x00, 0x00, 0x00, 0x00, 0x00, 0x32, 0x00, 0x00, 0x09, 0x12, 0x20, 0x10, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x0A, 0x00, 0x10, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x40, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x40, 0x01, 0x40, 0x00, 0x00, 0x00, 0x00, 0x80, 0xBF, 0x01, 0x00, 0x00, 0x07,
        0x42, 0x00, 0x10, 0x00, 0x00, 0x00, 0x00, 0x00, 0x0A, 0x10, 0x10, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x01, 0x40, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00, 0x56, 0x00, 0x00, 0x05, 0x22, 0x00, 0x10, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x2A, 0x00, 0x10, 0x00, 0x00, 0x00, 0x00, 0x00, 0x32, 0x00, 0x00, 0x0A,
        0x22, 0x20, 0x10, 0x00, 0x00, 0x00, 0x00, 0x00, 0x1A, 0x00, 0x10, 0x80, 0x41, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x01, 0x40, 0x00, 0x00, 0x00, 0x00, 0x00, 0x40, 0x01, 0x40, 0x00, 0x00,
        0x00, 0x00, 0x80, 0x3F, 0x32, 0x00, 0x00, 0x0B, 0x32, 0x20, 0x10, 0x00, 0x01, 0x00, 0x00, 0x00,
        0x46, 0x00, 0x10, 0x00, 0x00, 0x00, 0x00, 0x00, 0xE6, 0x8A, 0x20, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x46, 0x80, 0x20, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x36, 0x00, 0x00, 0x08, 0xC2, 0x20, 0x10, 0x00, 0x00, 0x00, 0x00, 0x00, 0x02, 0x40, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x80, 0x3F,
        0x3E, 0x00, 0x00, 0x01
    ];

    private static ReadOnlySpan<byte> PixelShaderBytecode =>
    [
        0x44, 0x58, 0x42, 0x43, 0x9B, 0x42, 0x3E, 0x81, 0xF9, 0x7D, 0x9D, 0x2B, 0xDA, 0xC4, 0x0A, 0xB5,
        0xED, 0x52, 0xC1, 0x19, 0x01, 0x00, 0x00, 0x00, 0x0C, 0x04, 0x00, 0x00, 0x03, 0x00, 0x00, 0x00,
        0x2C, 0x00, 0x00, 0x00, 0x84, 0x00, 0x00, 0x00, 0xB8, 0x00, 0x00, 0x00, 0x49, 0x53, 0x47, 0x4E,
        0x50, 0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00, 0x08, 0x00, 0x00, 0x00, 0x38, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x0F, 0x00, 0x00, 0x00, 0x44, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x03, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x03, 0x03, 0x00, 0x00, 0x53, 0x56, 0x5F, 0x50,
        0x4F, 0x53, 0x49, 0x54, 0x49, 0x4F, 0x4E, 0x00, 0x54, 0x45, 0x58, 0x43, 0x4F, 0x4F, 0x52, 0x44,
        0x00, 0xAB, 0xAB, 0xAB, 0x4F, 0x53, 0x47, 0x4E, 0x2C, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00,
        0x08, 0x00, 0x00, 0x00, 0x20, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x0F, 0x00, 0x00, 0x00, 0x53, 0x56, 0x5F, 0x54,
        0x41, 0x52, 0x47, 0x45, 0x54, 0x00, 0xAB, 0xAB, 0x53, 0x48, 0x44, 0x52, 0x4C, 0x03, 0x00, 0x00,
        0x40, 0x00, 0x00, 0x00, 0xD3, 0x00, 0x00, 0x00, 0x59, 0x00, 0x00, 0x04, 0x46, 0x8E, 0x20, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00, 0x5A, 0x00, 0x00, 0x03, 0x00, 0x60, 0x10, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x58, 0x18, 0x00, 0x04, 0x00, 0x70, 0x10, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x55, 0x55, 0x00, 0x00, 0x58, 0x18, 0x00, 0x04, 0x00, 0x70, 0x10, 0x00, 0x01, 0x00, 0x00, 0x00,
        0x55, 0x55, 0x00, 0x00, 0x62, 0x10, 0x00, 0x03, 0x32, 0x10, 0x10, 0x00, 0x01, 0x00, 0x00, 0x00,
        0x65, 0x00, 0x00, 0x03, 0xF2, 0x20, 0x10, 0x00, 0x00, 0x00, 0x00, 0x00, 0x68, 0x00, 0x00, 0x02,
        0x05, 0x00, 0x00, 0x00, 0x20, 0x00, 0x00, 0x0B, 0x32, 0x00, 0x10, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x06, 0x80, 0x20, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x02, 0x40, 0x00, 0x00,
        0x02, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x45, 0x00, 0x00, 0x09, 0xF2, 0x00, 0x10, 0x00, 0x01, 0x00, 0x00, 0x00, 0x46, 0x10, 0x10, 0x00,
        0x01, 0x00, 0x00, 0x00, 0x46, 0x7E, 0x10, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x60, 0x10, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x07, 0x42, 0x00, 0x10, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x0A, 0x00, 0x10, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x40, 0x00, 0x00, 0x81, 0x80, 0x80, 0xBD,
        0x45, 0x00, 0x00, 0x09, 0xF2, 0x00, 0x10, 0x00, 0x02, 0x00, 0x00, 0x00, 0x46, 0x10, 0x10, 0x00,
        0x01, 0x00, 0x00, 0x00, 0x46, 0x7E, 0x10, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x60, 0x10, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x0A, 0xF2, 0x00, 0x10, 0x00, 0x02, 0x00, 0x00, 0x00,
        0x16, 0x01, 0x10, 0x00, 0x02, 0x00, 0x00, 0x00, 0x02, 0x40, 0x00, 0x00, 0x81, 0x80, 0x00, 0xBF,
        0x81, 0x80, 0x00, 0xBF, 0x81, 0x80, 0x00, 0xBF, 0x81, 0x80, 0x00, 0xBF, 0x38, 0x00, 0x00, 0x0A,
        0xE2, 0x00, 0x10, 0x00, 0x01, 0x00, 0x00, 0x00, 0xA6, 0x0F, 0x10, 0x00, 0x02, 0x00, 0x00, 0x00,
        0x02, 0x40, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x8A, 0x78, 0xE5, 0x3F, 0xC3, 0x5D, 0x5A, 0x3E,
        0x97, 0x31, 0x07, 0x40, 0x32, 0x00, 0x00, 0x0A, 0x82, 0x00, 0x10, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x2A, 0x00, 0x10, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x40, 0x00, 0x00, 0x85, 0x0A, 0x95, 0x3F,
        0x2A, 0x00, 0x10, 0x80, 0x41, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x32, 0x00, 0x00, 0x0C,
        0x52, 0x00, 0x10, 0x00, 0x03, 0x00, 0x00, 0x00, 0xA6, 0x0A, 0x10, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x02, 0x40, 0x00, 0x00, 0x85, 0x0A, 0x95, 0x3F, 0x00, 0x00, 0x00, 0x00, 0x85, 0x0A, 0x95, 0x3F,
        0x00, 0x00, 0x00, 0x00, 0x56, 0x07, 0x10, 0x00, 0x01, 0x00, 0x00, 0x00, 0x32, 0x00, 0x00, 0x0A,
        0x22, 0x00, 0x10, 0x00, 0x03, 0x00, 0x00, 0x00, 0x2A, 0x00, 0x10, 0x80, 0x41, 0x00, 0x00, 0x00,
        0x02, 0x00, 0x00, 0x00, 0x01, 0x40, 0x00, 0x00, 0xB9, 0x6C, 0x08, 0x3F, 0x3A, 0x00, 0x10, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x38, 0x00, 0x00, 0x0A, 0xE2, 0x00, 0x10, 0x00, 0x01, 0x00, 0x00, 0x00,
        0x06, 0x0D, 0x10, 0x00, 0x02, 0x00, 0x00, 0x00, 0x02, 0x40, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x9A, 0x4A, 0xCC, 0x3F, 0x06, 0x95, 0xC8, 0x3E, 0x54, 0x1A, 0x01, 0x40, 0x32, 0x00, 0x00, 0x0A,
        0x82, 0x00, 0x10, 0x00, 0x00, 0x00, 0x00, 0x00, 0x2A, 0x00, 0x10, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x01, 0x40, 0x00, 0x00, 0x85, 0x0A, 0x95, 0x3F, 0x2A, 0x00, 0x10, 0x80, 0x41, 0x00, 0x00, 0x00,
        0x01, 0x00, 0x00, 0x00, 0x32, 0x00, 0x00, 0x0C, 0x52, 0x00, 0x10, 0x00, 0x04, 0x00, 0x00, 0x00,
        0xA6, 0x0A, 0x10, 0x00, 0x00, 0x00, 0x00, 0x00, 0x02, 0x40, 0x00, 0x00, 0x85, 0x0A, 0x95, 0x3F,
        0x00, 0x00, 0x00, 0x00, 0x85, 0x0A, 0x95, 0x3F, 0x00, 0x00, 0x00, 0x00, 0x56, 0x07, 0x10, 0x00,
        0x01, 0x00, 0x00, 0x00, 0x32, 0x00, 0x00, 0x0A, 0x22, 0x00, 0x10, 0x00, 0x04, 0x00, 0x00, 0x00,
        0x2A, 0x00, 0x10, 0x80, 0x41, 0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00, 0x01, 0x40, 0x00, 0x00,
        0xA0, 0x1E, 0x50, 0x3F, 0x3A, 0x00, 0x10, 0x00, 0x00, 0x00, 0x00, 0x00, 0x37, 0x00, 0x00, 0x09,
        0xE2, 0x00, 0x10, 0x00, 0x00, 0x00, 0x00, 0x00, 0x56, 0x05, 0x10, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x06, 0x09, 0x10, 0x00, 0x03, 0x00, 0x00, 0x00, 0x06, 0x09, 0x10, 0x00, 0x04, 0x00, 0x00, 0x00,
        0x32, 0x00, 0x00, 0x0A, 0x22, 0x00, 0x10, 0x00, 0x01, 0x00, 0x00, 0x00, 0x3A, 0x00, 0x10, 0x80,
        0x41, 0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00, 0x01, 0x40, 0x00, 0x00, 0x98, 0x32, 0xB0, 0x3E,
        0x0A, 0x00, 0x10, 0x00, 0x01, 0x00, 0x00, 0x00, 0x32, 0x00, 0x00, 0x0C, 0x52, 0x00, 0x10, 0x00,
        0x03, 0x00, 0x00, 0x00, 0xA6, 0x0B, 0x10, 0x00, 0x02, 0x00, 0x00, 0x00, 0x02, 0x40, 0x00, 0x00,
        0xBC, 0x74, 0xB3, 0x3F, 0x00, 0x00, 0x00, 0x00, 0xE5, 0xD0, 0xE2, 0x3F, 0x00, 0x00, 0x00, 0x00,
        0x06, 0x00, 0x10, 0x00, 0x01, 0x00, 0x00, 0x00, 0x32, 0x00, 0x00, 0x0A, 0x22, 0x00, 0x10, 0x00,
        0x03, 0x00, 0x00, 0x00, 0x2A, 0x00, 0x10, 0x80, 0x41, 0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00,
        0x01, 0x40, 0x00, 0x00, 0x9E, 0xD1, 0x36, 0x3F, 0x1A, 0x00, 0x10, 0x00, 0x01, 0x00, 0x00, 0x00,
        0x37, 0x20, 0x00, 0x09, 0x72, 0x20, 0x10, 0x00, 0x00, 0x00, 0x00, 0x00, 0x06, 0x00, 0x10, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x46, 0x02, 0x10, 0x00, 0x03, 0x00, 0x00, 0x00, 0x96, 0x07, 0x10, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x36, 0x00, 0x00, 0x05, 0x82, 0x20, 0x10, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x01, 0x40, 0x00, 0x00, 0x00, 0x00, 0x80, 0x3F, 0x3E, 0x00, 0x00, 0x01
    ];

    public void Dispose()
    {
        lock (_renderGate)
        {
            if (_disposed) return;
            _disposed = true;
            if (!_context.IsNull)
            {
                ComVtbl.Get<ClearStateFn>(_context.Handle, 110)(_context.Handle);
                ComVtbl.Get<FlushFn>(_context.Handle, 111)(_context.Handle);
            }
            _swYSrv.Dispose();
            _swUvSrv.Dispose();
            _swYTex.Dispose();
            _swUvTex.Dispose();
            _hwYSrv.Dispose();
            _hwUvSrv.Dispose();
            _hwNv12Tex.Dispose();
            _constantBuffer.Dispose();
            _sampler.Dispose();
            _ps.Dispose();
            _vs.Dispose();
            _rtv.Dispose();
            _swapChain.Dispose();

            int remaining = _counted ? Interlocked.Decrement(ref _activeInstanceCount) : Volatile.Read(ref _activeInstanceCount);
            _counted = false;

            if (remaining == 0 && !_device.IsNull)
            {
                try
                {
                    if (_device.TryQueryInterface(in IidIdxgiDevice3, out var dxgiDev3) && dxgiDev3 != null)
                    {
                        using (dxgiDev3)
                            ComVtbl.Get<DxgiDeviceTrimFn>(dxgiDev3.Handle, 17)(dxgiDev3.Handle);
                    }
                }
                catch { }
            }

            _context.Dispose();
            _device.Dispose();
            Marshal.FreeHGlobal(_blackColorPtr);
        }
    }

    [DllImport("d3d11.dll")]
    private static extern int D3D11CreateDevice(
        nint pAdapter, int driverType, nint software, uint flags,
        [MarshalAs(UnmanagedType.LPArray)] int[] pFeatureLevels, int featureLevels,
        uint sdkVersion, out nint ppDevice, out int pFeatureLevel, out nint ppImmediateContext);

    [StructLayout(LayoutKind.Sequential)]
    private struct ShaderConstants
    {
        public float U0;
        public float V0;
        public float UScale;
        public float VScale;
        public int MatrixMode;
        public float Pad0;
        public float Pad1;
        public float Pad2;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11BufferDesc
    {
        public uint ByteWidth;
        public int Usage;
        public uint BindFlags;
        public uint CPUAccessFlags;
        public uint MiscFlags;
        public uint StructureByteStride;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11Texture2DDesc
    {
        public uint Width;
        public uint Height;
        public uint MipLevels;
        public uint ArraySize;
        public int Format;
        public uint SampleCount;
        public uint SampleQuality;
        public int Usage;
        public uint BindFlags;
        public uint CPUAccessFlags;
        public uint MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11ShaderResourceViewDesc
    {
        public int Format;
        public int ViewDimension;
        public uint MostDetailedMip;
        public uint MipLevels;
        public uint Pad0;
        public uint Pad1;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11SamplerDesc
    {
        public int Filter;
        public int AddressU;
        public int AddressV;
        public int AddressW;
        public float MipLODBias;
        public uint MaxAnisotropy;
        public int ComparisonFunc;
        public float Border0;
        public float Border1;
        public float Border2;
        public float Border3;
        public float MinLOD;
        public float MaxLOD;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11Viewport
    {
        public float TopLeftX;
        public float TopLeftY;
        public float Width;
        public float Height;
        public float MinDepth;
        public float MaxDepth;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11MappedSubresource
    {
        public nint pData;
        public uint RowPitch;
        public uint DepthPitch;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DxgiSwapChainDesc1
    {
        public uint Width;
        public uint Height;
        public int Format;
        public int Stereo;
        public uint SampleCount;
        public uint SampleQuality;
        public uint BufferUsage;
        public uint BufferCount;
        public int Scaling;
        public int SwapEffect;
        public int AlphaMode;
        public uint Flags;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetMultithreadProtectedFn(nint self, int bMTProtect);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetMaxFrameLatencyFn(nint self, uint maxLatency);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void DxgiDeviceTrimFn(nint self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetAdapterFn(nint self, out nint pAdapter);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetParentFn(nint self, in Guid riid, out nint ppParent);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateSwapChainForHwndFn(nint self, nint pDevice, nint hwnd, in DxgiSwapChainDesc1 pDesc, nint pFullscreenDesc, nint pRestrictToOutput, out nint ppSwapChain);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int PresentFn(nint self, uint syncInterval, uint flags);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetBufferFn(nint self, uint buffer, in Guid riid, out nint ppSurface);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int ResizeBuffersFn(nint self, uint bufferCount, uint width, uint height, int newFormat, uint flags);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateBufferFn(nint self, in D3D11BufferDesc pDesc, nint pInitialData, out nint ppBuffer);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateTexture2DFn(nint self, in D3D11Texture2DDesc pDesc, nint pInitialData, out nint ppTexture2D);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateSrvFn(nint self, nint pResource, in D3D11ShaderResourceViewDesc pDesc, out nint ppSRView);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateRenderTargetViewFn(nint self, nint pResource, nint pDesc, out nint ppRTView);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateVertexShaderFn(nint self, nint pBytecode, nint length, nint pClassLinkage, out nint ppVS);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreatePixelShaderFn(nint self, nint pBytecode, nint length, nint pClassLinkage, out nint ppPS);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CreateSamplerStateFn(nint self, in D3D11SamplerDesc pDesc, out nint ppSampler);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void PsSetShaderResourcesFn(nint self, uint startSlot, uint numViews, in nint ppSrvs);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void PsSetShaderFn(nint self, nint ps, nint classInstances, uint numClassInstances);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void PsSetSamplersFn(nint self, uint startSlot, uint numSamplers, in nint ppSamplers);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void VsSetShaderFn(nint self, nint vs, nint classInstances, uint numClassInstances);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void DrawFn(nint self, uint vertexCount, uint startVertexLocation);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int MapFn(nint self, nint resource, uint subresource, int mapType, uint mapFlags, out D3D11MappedSubresource mapped);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void UnmapFn(nint self, nint resource, uint subresource);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void PsSetConstantBuffersFn(nint self, uint startSlot, uint numBuffers, in nint ppConstantBuffers);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void VsSetConstantBuffersFn(nint self, uint startSlot, uint numBuffers, in nint ppConstantBuffers);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void IaSetInputLayoutFn(nint self, nint inputLayout);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void IaSetPrimitiveTopologyFn(nint self, int topology);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void OmSetRenderTargetsFn(nint self, uint numViews, in nint ppRenderTargetViews, nint pDepthStencilView);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void RsSetViewportsFn(nint self, uint numViewports, in D3D11Viewport pViewports);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void CopySubresourceRegionFn(nint self, nint dstResource, uint dstSubresource, uint dstX, uint dstY, uint dstZ, nint srcResource, uint srcSubresource, nint srcBox);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void ClearRenderTargetViewFn(nint self, nint rtv, nint colorRgba);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void ClearStateFn(nint self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate void FlushFn(nint self);
}
