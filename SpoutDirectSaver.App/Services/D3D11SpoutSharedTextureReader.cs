using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using SharpGen.Runtime;
using SpoutDirectSaver.App.Models;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace SpoutDirectSaver.App.Services;

internal sealed class D3D11SpoutSharedTextureReader : IDisposable
{
    private const int KeyedMutexTimeoutMilliseconds = 67;
    private const int AccessMutexTimeoutMilliseconds = 67;
    private const int StagingTextureCount = 3;
    private const int RecordingTextureCount = 10;
    private readonly IDXGIFactory1 _dxgiFactory;
    private readonly object _recordingSlotGate = new();
    private readonly Queue<int> _availableRecordingSlotIndices = new();

    private Mutex? _accessMutex;
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _deviceContext;
    private ID3D11Texture2D? _sharedTexture;
    private ID3D11ShaderResourceView? _sharedTextureShaderView;
    private readonly ID3D11Texture2D?[] _stagingTextures = new ID3D11Texture2D?[StagingTextureCount];
    private readonly RecordingTextureSlot?[] _recordingTextures = new RecordingTextureSlot?[RecordingTextureCount];
    private IDXGIKeyedMutex? _keyedMutex;
    private ID3D11VertexShader? _blitVertexShader;
    private ID3D11PixelShader? _copyPixelShader;
    private ID3D11PixelShader? _alphaPixelShader;
    private ID3D11SamplerState? _blitSampler;
    private ID3D11Buffer? _alphaOptionsBuffer;
    private ID3D11Texture2D? _alphaRenderTexture;
    private ID3D11RenderTargetView? _alphaRenderTargetView;
    private ID3D11Texture2D? _alphaReadbackTexture;
    private int _deviceAdapterIndex = int.MinValue;
    private IntPtr _sharedHandle;
    private string _senderName = string.Empty;
    private uint _width;
    private uint _height;
    private Format _format = Format.Unknown;
    private int _copyIndex;
    private int _copiedFrameCount;

    public D3D11SpoutSharedTextureReader()
    {
        _dxgiFactory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
    }

    public uint Width => _width;

    public uint Height => _height;

    public Format Format => _format;

    public bool Matches(string senderName, IntPtr sharedHandle, uint width, uint height, Format format)
    {
        return _sharedTexture is not null &&
               sharedHandle == _sharedHandle &&
               width == _width &&
               height == _height &&
               format == _format &&
               string.Equals(senderName, _senderName, StringComparison.Ordinal);
    }

    public bool TrySynchronizeSender(
        string senderName,
        IntPtr sharedHandle,
        uint width,
        uint height,
        Format format,
        int adapterIndex,
        out string? errorMessage)
    {
        if (string.IsNullOrWhiteSpace(senderName))
        {
            ResetResources();
            errorMessage = "Spout sender 名が取得できませんでした。";
            return false;
        }

        if (sharedHandle == IntPtr.Zero || width == 0 || height == 0)
        {
            ResetResources();
            errorMessage = $"sender \"{senderName}\" の共有テクスチャ情報が不正です。";
            return false;
        }

        if (!IsSupportedFormat(format))
        {
            ResetResources();
            errorMessage = $"未対応の DXGI format です: {format}.";
            return false;
        }

        if (Matches(senderName, sharedHandle, width, height, format) && _deviceAdapterIndex == adapterIndex)
        {
            errorMessage = null;
            return true;
        }

        try
        {
            EnsureDevice(adapterIndex);
            RecreateResources(senderName, sharedHandle, width, height, format);
            errorMessage = null;
            return true;
        }
        catch (Exception ex)
        {
            var primaryError = ex.Message;
            ResetResources();

            if (adapterIndex >= 0)
            {
                try
                {
                    EnsureDevice(-1);
                    RecreateResources(senderName, sharedHandle, width, height, format);
                    errorMessage = null;
                    DebugTrace.WriteLine(
                        "D3D11SpoutSharedTextureReader",
                        $"adapter fallback succeeded sender={senderName} originalAdapter={adapterIndex}");
                    return true;
                }
                catch (Exception fallbackEx)
                {
                    ResetResources();
                    errorMessage = $"共有テクスチャを開けませんでした: primary={primaryError}; fallback={fallbackEx.Message}";
                    DebugTrace.WriteLine(
                        "D3D11SpoutSharedTextureReader",
                        $"sync failed sender={senderName} adapter={adapterIndex} error={errorMessage}");
                    return false;
                }
            }

            errorMessage = $"共有テクスチャを開けませんでした: {primaryError}";
            DebugTrace.WriteLine(
                "D3D11SpoutSharedTextureReader",
                $"sync failed sender={senderName} adapter={adapterIndex} error={errorMessage}");
            return false;
        }
    }

    public bool TryReadFrame(byte[] destination, out string? errorMessage)
    {
        unsafe
        {
            fixed (byte* destinationPtr = destination)
            {
                return TryReadFrame((IntPtr)destinationPtr, destination.Length, out errorMessage);
            }
        }
    }

    public bool TryReadFrame(IntPtr destination, int destinationLength, out string? errorMessage)
    {
        var requiredBytes = checked((int)(_width * _height * 4));
        if (_sharedTexture is null || Array.Exists(_stagingTextures, static texture => texture is null))
        {
            errorMessage = "共有テクスチャが初期化されていません。";
            return false;
        }

        if (destination == IntPtr.Zero || destinationLength < requiredBytes)
        {
            throw new ArgumentException("Destination buffer is too small.", nameof(destinationLength));
        }

        var sharedAccessAcquired = false;

        try
        {
            if (!TryAcquireTextureAccess())
            {
                errorMessage = "共有テクスチャのアクセス待機がタイムアウトしました。";
                return false;
            }

            sharedAccessAcquired = true;
            var copyTexture = _stagingTextures[_copyIndex]!;
            _deviceContext!.CopyResource(copyTexture, _sharedTexture);
            _copiedFrameCount++;

            ID3D11Texture2D readTexture;
            if (_copiedFrameCount < StagingTextureCount)
            {
                readTexture = copyTexture;
            }
            else
            {
                var readIndex = (_copyIndex + 1) % StagingTextureCount;
                readTexture = _stagingTextures[readIndex]!;
            }

            var mapped = _deviceContext.Map(readTexture, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);

            try
            {
                unsafe
                {
                    CopyMappedTextureToBgraBuffer(mapped, (byte*)destination);
                }
            }
            finally
            {
                _deviceContext.Unmap(readTexture, 0);
            }

            _copyIndex = (_copyIndex + 1) % StagingTextureCount;

            errorMessage = null;
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = $"共有テクスチャの読み出しに失敗しました: {ex.Message}";
            return false;
        }
        finally
        {
            if (sharedAccessAcquired)
            {
                ReleaseTextureAccess();
            }
        }
    }

    public bool TryCaptureFrame(out GpuTextureFrame gpuFrame, out PixelBufferLease alphaBuffer, out string? errorMessage)
    {
        gpuFrame = null!;
        alphaBuffer = null!;

        if (_sharedTexture is null ||
            _sharedTextureShaderView is null ||
            _blitVertexShader is null ||
            _copyPixelShader is null ||
            _alphaPixelShader is null ||
            _blitSampler is null ||
            _alphaOptionsBuffer is null ||
            _alphaRenderTexture is null ||
            _alphaRenderTargetView is null ||
            _alphaReadbackTexture is null)
        {
            errorMessage = "共有テクスチャ capture リソースが初期化されていません。";
            return false;
        }

        RecordingTextureSlot? slot = null;
        PixelBufferLease? alphaLease = null;
        var sharedAccessAcquired = false;

        try
        {
            slot = AcquireRecordingSlot();

            if (!TryAcquireTextureAccess())
            {
                errorMessage = "共有テクスチャのアクセス待機がタイムアウトしました。";
                ReleaseRecordingSlot(slot.Index);
                return false;
            }

            sharedAccessAcquired = true;
            RenderSharedTextureToBgra(slot);
            ReleaseTextureAccess();
            sharedAccessAcquired = false;

            alphaLease = PixelBufferLease.Rent(checked((int)(_width * _height)));
            ExtractAlphaPlane(slot, alphaLease.Buffer);

            gpuFrame = new GpuTextureFrame(_device!, slot.Texture, _width, _height, () => ReleaseRecordingSlot(slot.Index));
            alphaBuffer = alphaLease;
            errorMessage = null;
            return true;
        }
        catch (Exception ex)
        {
            alphaLease?.Dispose();
            if (slot is not null)
            {
                ReleaseRecordingSlot(slot.Index);
            }

            errorMessage = $"共有テクスチャ frame capture に失敗しました: {ex.Message}";
            return false;
        }
        finally
        {
            if (sharedAccessAcquired)
            {
                ReleaseTextureAccess();
            }
        }
    }

    public void Dispose()
    {
        ResetResources();
        _deviceContext?.Dispose();
        _device?.Dispose();
        _dxgiFactory.Dispose();
    }

    private void RecreateResources(string senderName, IntPtr sharedHandle, uint width, uint height, Format format)
    {
        ResetResources();

        var sharedTexture = _device!.OpenSharedResource<ID3D11Texture2D>(sharedHandle);
        var sharedDescription = sharedTexture.Description;
        var stagingDescription = new Texture2DDescription(
            sharedDescription.Format,
            sharedDescription.Width,
            sharedDescription.Height,
            1,
            1,
            BindFlags.None,
            ResourceUsage.Staging,
            CpuAccessFlags.Read,
            sharedDescription.SampleDescription.Count,
            sharedDescription.SampleDescription.Quality,
            ResourceOptionFlags.None);

        _sharedTexture = sharedTexture;
        _sharedTextureShaderView = CreateSharedTextureShaderResourceView(sharedTexture, format);
        for (var index = 0; index < StagingTextureCount; index++)
        {
            _stagingTextures[index] = _device.CreateTexture2D(stagingDescription);
        }

        CreateRecordingResources(sharedDescription.Width, sharedDescription.Height);

        _keyedMutex = sharedTexture.QueryInterfaceOrNull<IDXGIKeyedMutex>();
        _accessMutex = _keyedMutex is null
            ? new Mutex(false, $"{senderName}_SpoutAccessMutex")
            : null;
        _sharedHandle = sharedHandle;
        _senderName = senderName;
        _width = width;
        _height = height;
        _format = format;
        _copyIndex = 0;
        _copiedFrameCount = 0;
    }

    private void CreateRecordingResources(uint width, uint height)
    {
        var vertexShaderBytecode = Vortice.D3DCompiler.Compiler.Compile(
            BlitShaderSource,
            "vs_main",
            "SpoutDirectSaverBlit.hlsl",
            "vs_5_0",
            ShaderFlags.OptimizationLevel3,
            EffectFlags.None);
        var copyPixelShaderBytecode = Vortice.D3DCompiler.Compiler.Compile(
            BlitShaderSource,
            "ps_copy",
            "SpoutDirectSaverBlit.hlsl",
            "ps_5_0",
            ShaderFlags.OptimizationLevel3,
            EffectFlags.None);
        var alphaPixelShaderBytecode = Vortice.D3DCompiler.Compiler.Compile(
            BlitShaderSource,
            "ps_alpha",
            "SpoutDirectSaverBlit.hlsl",
            "ps_5_0",
            ShaderFlags.OptimizationLevel3,
            EffectFlags.None);

        _blitVertexShader = _device!.CreateVertexShader(vertexShaderBytecode.Span);
        _copyPixelShader = _device.CreatePixelShader(copyPixelShaderBytecode.Span);
        _alphaPixelShader = _device.CreatePixelShader(alphaPixelShaderBytecode.Span);
        _blitSampler = _device.CreateSamplerState(new SamplerDescription(Filter.MinMagMipPoint, TextureAddressMode.Clamp, TextureAddressMode.Clamp, TextureAddressMode.Clamp));
        _alphaOptionsBuffer = _device.CreateBuffer(new BufferDescription(16, BindFlags.ConstantBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write));

        var alphaTextureDescription = new Texture2DDescription(
            Format.R8_UNorm,
            width,
            height,
            1,
            1,
            BindFlags.RenderTarget,
            ResourceUsage.Default,
            CpuAccessFlags.None,
            1,
            0,
            ResourceOptionFlags.None);
        _alphaRenderTexture = _device.CreateTexture2D(alphaTextureDescription);
        _alphaRenderTargetView = _device.CreateRenderTargetView(_alphaRenderTexture);
        _alphaReadbackTexture = _device.CreateTexture2D(new Texture2DDescription(
            Format.R8_UNorm,
            width,
            height,
            1,
            1,
            BindFlags.None,
            ResourceUsage.Staging,
            CpuAccessFlags.Read,
            1,
            0,
            ResourceOptionFlags.None));

        lock (_recordingSlotGate)
        {
            _availableRecordingSlotIndices.Clear();
            for (var index = 0; index < RecordingTextureCount; index++)
            {
                _recordingTextures[index] = CreateRecordingTextureSlot(index, width, height);
                _availableRecordingSlotIndices.Enqueue(index);
            }

            Monitor.PulseAll(_recordingSlotGate);
        }
    }

    private RecordingTextureSlot CreateRecordingTextureSlot(int index, uint width, uint height)
    {
        var textureDescription = new Texture2DDescription(
            Format.B8G8R8A8_UNorm,
            width,
            height,
            1,
            1,
            BindFlags.RenderTarget | BindFlags.ShaderResource,
            ResourceUsage.Default,
            CpuAccessFlags.None,
            1,
            0,
            ResourceOptionFlags.None);

        var texture = _device!.CreateTexture2D(textureDescription);
        var renderTargetView = _device.CreateRenderTargetView(texture);
        var shaderResourceView = _device.CreateShaderResourceView(texture);
        return new RecordingTextureSlot(index, texture, renderTargetView, shaderResourceView);
    }

    private void RenderSharedTextureToBgra(RecordingTextureSlot slot)
    {
        _deviceContext!.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _deviceContext.VSSetShader(_blitVertexShader);
        _deviceContext.PSSetShader(_copyPixelShader);
        _deviceContext.PSSetShaderResource(0, _sharedTextureShaderView!);
        _deviceContext.PSSetSampler(0, _blitSampler);
        SetViewport(_width, _height);
        SetRenderTarget(slot.RenderTargetView);
        _deviceContext.Draw(3, 0);
        _deviceContext.PSSetShaderResource(0, (ID3D11ShaderResourceView)null!);
        ClearRenderTargets();
        _deviceContext.Flush();
    }

    private unsafe void ExtractAlphaPlane(RecordingTextureSlot slot, byte[] destination)
    {
        var alphaOptions = new AlphaShaderOptions
        {
            ForceOpaqueAlpha = HasOpaqueAlphaOnly(_format) ? 1u : 0u
        };
        var mappedOptions = _deviceContext!.Map(_alphaOptionsBuffer!, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        try
        {
            *(AlphaShaderOptions*)mappedOptions.DataPointer = alphaOptions;
        }
        finally
        {
            _deviceContext.Unmap(_alphaOptionsBuffer, 0);
        }

        _deviceContext.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _deviceContext.VSSetShader(_blitVertexShader);
        _deviceContext.PSSetShader(_alphaPixelShader);
        _deviceContext.PSSetShaderResource(0, slot.ShaderResourceView);
        _deviceContext.PSSetSampler(0, _blitSampler);
        _deviceContext.PSSetConstantBuffer(0, _alphaOptionsBuffer);
        SetViewport(_width, _height);
        SetRenderTarget(_alphaRenderTargetView!);
        _deviceContext.Draw(3, 0);
        _deviceContext.PSSetShaderResource(0, (ID3D11ShaderResourceView)null!);
        _deviceContext.PSSetConstantBuffer(0, (ID3D11Buffer)null!);
        ClearRenderTargets();
        _deviceContext.Flush();

        _deviceContext.CopyResource(_alphaReadbackTexture!, _alphaRenderTexture!);
        _deviceContext.Flush();
        var mappedAlpha = _deviceContext.Map(_alphaReadbackTexture!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);

        try
        {
            var pixelWidth = checked((int)_width);
            var pixelHeight = checked((int)_height);
            for (var row = 0; row < pixelHeight; row++)
            {
                var sourceRow = (byte*)mappedAlpha.DataPointer + (row * mappedAlpha.RowPitch);
                Marshal.Copy((IntPtr)sourceRow, destination, row * pixelWidth, pixelWidth);
            }
        }
        finally
        {
            _deviceContext.Unmap(_alphaReadbackTexture, 0);
        }
    }

    private RecordingTextureSlot AcquireRecordingSlot()
    {
        lock (_recordingSlotGate)
        {
            while (_availableRecordingSlotIndices.Count == 0)
            {
                Monitor.Wait(_recordingSlotGate);
            }

            return _recordingTextures[_availableRecordingSlotIndices.Dequeue()]!;
        }
    }

    private void ReleaseRecordingSlot(int index)
    {
        lock (_recordingSlotGate)
        {
            if (_recordingTextures[index] is null)
            {
                return;
            }

            _availableRecordingSlotIndices.Enqueue(index);
            Monitor.Pulse(_recordingSlotGate);
        }
    }

    private ID3D11ShaderResourceView CreateSharedTextureShaderResourceView(ID3D11Texture2D sharedTexture, Format format)
    {
        var shaderViewFormat = ResolveShaderResourceFormat(format);
        if (shaderViewFormat == format)
        {
            return _device!.CreateShaderResourceView(sharedTexture);
        }

        return _device!.CreateShaderResourceView(sharedTexture, new ShaderResourceViewDescription(
            ShaderResourceViewDimension.Texture2D,
            shaderViewFormat,
            0,
            1));
    }

    private void SetRenderTarget(ID3D11RenderTargetView renderTargetView)
    {
        _deviceContext!.OMSetRenderTargets(renderTargetView, null);
    }

    private void ClearRenderTargets()
    {
        _deviceContext!.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>(), null);
    }

    private void SetViewport(uint width, uint height)
    {
        var viewport = new Viewport(0, 0, width, height, 0.0f, 1.0f);
        _deviceContext!.RSSetViewports([viewport]);
    }

    private static Format ResolveShaderResourceFormat(Format format)
    {
        return format switch
        {
            Format.B8G8R8A8_Typeless => Format.B8G8R8A8_UNorm,
            Format.B8G8R8X8_Typeless => Format.B8G8R8X8_UNorm,
            Format.R8G8B8A8_Typeless => Format.R8G8B8A8_UNorm,
            _ => format
        };
    }

    private void EnsureDevice(int adapterIndex)
    {
        if (_device is not null && _deviceContext is not null && _deviceAdapterIndex == adapterIndex)
        {
            return;
        }

        ResetResources();
        _deviceContext?.Dispose();
        _device?.Dispose();
        _deviceContext = null;
        _device = null;

        IDXGIAdapter1? adapter = null;
        try
        {
            if (adapterIndex >= 0)
            {
                var adapterResult = _dxgiFactory.EnumAdapters1((uint)adapterIndex, out adapter);
                if (adapterResult.Success && adapter is not null)
                {
                    ID3D11Device? device;
                    ID3D11DeviceContext? context;
                    var result = D3D11.D3D11CreateDevice(
                        adapter,
                        DriverType.Unknown,
                        DeviceCreationFlags.BgraSupport,
                        Array.Empty<FeatureLevel>(),
                        out device,
                        out context);
                    result.CheckError();
                    _device = device;
                    _deviceContext = context;
                    _deviceAdapterIndex = adapterIndex;
                    return;
                }
            }

            _device = D3D11.D3D11CreateDevice(
                DriverType.Hardware,
                DeviceCreationFlags.BgraSupport,
                Array.Empty<FeatureLevel>());
            _deviceContext = _device.ImmediateContext;
            _deviceAdapterIndex = -1;
        }
        finally
        {
            adapter?.Dispose();
        }
    }

    private void ResetResources()
    {
        _accessMutex?.Dispose();
        _accessMutex = null;
        _keyedMutex?.Dispose();
        _keyedMutex = null;
        _sharedTextureShaderView?.Dispose();
        _sharedTextureShaderView = null;
        for (var index = 0; index < _stagingTextures.Length; index++)
        {
            _stagingTextures[index]?.Dispose();
            _stagingTextures[index] = null;
        }

        lock (_recordingSlotGate)
        {
            _availableRecordingSlotIndices.Clear();
            for (var index = 0; index < _recordingTextures.Length; index++)
            {
                _recordingTextures[index]?.Dispose();
                _recordingTextures[index] = null;
            }

            Monitor.PulseAll(_recordingSlotGate);
        }

        _alphaReadbackTexture?.Dispose();
        _alphaReadbackTexture = null;
        _alphaRenderTargetView?.Dispose();
        _alphaRenderTargetView = null;
        _alphaRenderTexture?.Dispose();
        _alphaRenderTexture = null;
        _alphaOptionsBuffer?.Dispose();
        _alphaOptionsBuffer = null;
        _blitSampler?.Dispose();
        _blitSampler = null;
        _alphaPixelShader?.Dispose();
        _alphaPixelShader = null;
        _copyPixelShader?.Dispose();
        _copyPixelShader = null;
        _blitVertexShader?.Dispose();
        _blitVertexShader = null;
        _sharedTexture?.Dispose();
        _sharedTexture = null;
        _sharedHandle = IntPtr.Zero;
        _senderName = string.Empty;
        _width = 0;
        _height = 0;
        _format = Format.Unknown;
        _copyIndex = 0;
        _copiedFrameCount = 0;
    }

    private bool TryAcquireTextureAccess()
    {
        try
        {
            if (_keyedMutex is not null)
            {
                _keyedMutex.AcquireSync(0, KeyedMutexTimeoutMilliseconds);
                return true;
            }

            return _accessMutex?.WaitOne(AccessMutexTimeoutMilliseconds) != false;
        }
        catch
        {
            return false;
        }
    }

    private void ReleaseTextureAccess()
    {
        try
        {
            if (_keyedMutex is not null)
            {
                _keyedMutex.ReleaseSync(0);
                return;
            }

            _accessMutex?.ReleaseMutex();
        }
        catch
        {
            // Ignore release failures and retry on the next frame.
        }
    }

    private static bool IsSupportedFormat(Format format)
    {
        return format is
            Format.B8G8R8A8_Typeless or
            Format.B8G8R8A8_UNorm or
            Format.B8G8R8A8_UNorm_SRgb or
            Format.B8G8R8X8_Typeless or
            Format.B8G8R8X8_UNorm or
            Format.B8G8R8X8_UNorm_SRgb or
            Format.R8G8B8A8_Typeless or
            Format.R8G8B8A8_UNorm or
            Format.R8G8B8A8_UNorm_SRgb;
    }

    private unsafe void CopyMappedTextureToBgraBuffer(MappedSubresource mapped, byte* destination)
    {
        var width = checked((int)_width);
        var height = checked((int)_height);
        var destinationRowPitch = width * 4;

        for (var row = 0; row < height; row++)
        {
            var sourceRow = (byte*)mapped.DataPointer + (row * mapped.RowPitch);
            var destinationRow = destination + (row * destinationRowPitch);

            if (IsBgraCompatibleFormat(_format))
            {
                Buffer.MemoryCopy(sourceRow, destinationRow, destinationRowPitch, destinationRowPitch);
                if (HasOpaqueAlphaOnly(_format))
                {
                    ForceOpaqueAlpha(destinationRow, width);
                }

                continue;
            }

            ConvertRgbaSourceRowToBgra(sourceRow, destinationRow, width);
        }
    }

    private static bool IsBgraCompatibleFormat(Format format)
    {
        return format is
            Format.B8G8R8A8_Typeless or
            Format.B8G8R8A8_UNorm or
            Format.B8G8R8A8_UNorm_SRgb or
            Format.B8G8R8X8_Typeless or
            Format.B8G8R8X8_UNorm or
            Format.B8G8R8X8_UNorm_SRgb;
    }

    private static bool HasOpaqueAlphaOnly(Format format)
    {
        return format is
            Format.B8G8R8X8_Typeless or
            Format.B8G8R8X8_UNorm or
            Format.B8G8R8X8_UNorm_SRgb;
    }

    private static unsafe void ForceOpaqueAlpha(byte* destinationRow, int width)
    {
        for (var x = 0; x < width; x++)
        {
            destinationRow[(x * 4) + 3] = 255;
        }
    }

    private static unsafe void ConvertRgbaSourceRowToBgra(byte* sourceRow, byte* destinationRow, int width)
    {
        for (var x = 0; x < width; x++)
        {
            var offset = x * 4;
            destinationRow[offset] = sourceRow[offset + 2];
            destinationRow[offset + 1] = sourceRow[offset + 1];
            destinationRow[offset + 2] = sourceRow[offset];
            destinationRow[offset + 3] = sourceRow[offset + 3];
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AlphaShaderOptions
    {
        public uint ForceOpaqueAlpha;
        public uint Reserved0;
        public uint Reserved1;
        public uint Reserved2;
    }

    private sealed class RecordingTextureSlot : IDisposable
    {
        public RecordingTextureSlot(
            int index,
            ID3D11Texture2D texture,
            ID3D11RenderTargetView renderTargetView,
            ID3D11ShaderResourceView shaderResourceView)
        {
            Index = index;
            Texture = texture;
            RenderTargetView = renderTargetView;
            ShaderResourceView = shaderResourceView;
        }

        public int Index { get; }

        public ID3D11Texture2D Texture { get; }

        public ID3D11RenderTargetView RenderTargetView { get; }

        public ID3D11ShaderResourceView ShaderResourceView { get; }

        public void Dispose()
        {
            ShaderResourceView.Dispose();
            RenderTargetView.Dispose();
            Texture.Dispose();
        }
    }

    private const string BlitShaderSource = """
Texture2D inputTexture : register(t0);
SamplerState inputSampler : register(s0);

cbuffer AlphaOptions : register(b0)
{
    uint forceOpaqueAlpha;
    uint reserved0;
    uint reserved1;
    uint reserved2;
};

struct VsOutput
{
    float4 position : SV_Position;
    float2 uv : TEXCOORD0;
};

VsOutput vs_main(uint vertexId : SV_VertexID)
{
    VsOutput output;
    float2 pos = float2((vertexId << 1) & 2, vertexId & 2);
    output.position = float4(pos * float2(2.0, -2.0) + float2(-1.0, 1.0), 0.0, 1.0);
    output.uv = pos;
    return output;
}

float4 ps_copy(VsOutput input) : SV_Target
{
    return inputTexture.SampleLevel(inputSampler, input.uv, 0.0);
}

float4 ps_alpha(VsOutput input) : SV_Target
{
    float4 color = inputTexture.SampleLevel(inputSampler, input.uv, 0.0);
    float alpha = forceOpaqueAlpha != 0 ? 1.0 : color.a;
    return float4(alpha, 0.0, 0.0, 1.0);
}
""";
}
