using System;
using System.Threading;
using SharpGen.Runtime;
using Spout.Interop;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace SpoutDirectSaver.App.Services;

internal sealed class D3D11SpoutSharedTextureReader : IDisposable
{
    private const int KeyedMutexTimeoutMilliseconds = 5;
    private const int AccessMutexTimeoutMilliseconds = 67;
    private readonly IDXGIFactory1 _dxgiFactory;

    private Mutex? _accessMutex;
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _deviceContext;
    private ID3D11Texture2D? _sharedTexture;
    private ID3D11Texture2D? _stagingTexture;
    private IDXGIKeyedMutex? _keyedMutex;
    private int _deviceAdapterIndex = int.MinValue;
    private IntPtr _sharedHandle;
    private string _senderName = string.Empty;
    private uint _width;
    private uint _height;
    private Format _format = Format.Unknown;

    public D3D11SpoutSharedTextureReader()
    {
        _dxgiFactory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
    }

    public uint Width => _width;

    public uint Height => _height;

    public Format Format => _format;

    public bool TrySynchronizeSender(SpoutReceiver receiver, string senderName, out string? errorMessage)
    {
        if (string.IsNullOrWhiteSpace(senderName))
        {
            ResetResources();
            errorMessage = "Spout sender 名が取得できませんでした。";
            return false;
        }

        unsafe
        {
            EnsureDevice(receiver.Adapter);

            uint width = 0;
            uint height = 0;
            uint dxgiFormat = 0;
            IntPtr sharedHandle = IntPtr.Zero;
            if (!receiver.GetSenderInfo(senderName, ref width, ref height, &sharedHandle, ref dxgiFormat))
            {
                ResetResources();
                errorMessage = $"sender \"{senderName}\" の共有テクスチャ情報を取得できませんでした。";
                return false;
            }

            if (sharedHandle == IntPtr.Zero || width == 0 || height == 0)
            {
                ResetResources();
                errorMessage = $"sender \"{senderName}\" の共有テクスチャ情報が不正です。";
                return false;
            }

            var format = (Format)dxgiFormat;
            if (!IsSupportedFormat(format))
            {
                ResetResources();
                errorMessage = $"未対応の DXGI format です: {format}.";
                return false;
            }

            if (_sharedTexture is not null &&
                sharedHandle == _sharedHandle &&
                width == _width &&
                height == _height &&
                format == _format &&
                string.Equals(senderName, _senderName, StringComparison.Ordinal))
            {
                errorMessage = null;
                return true;
            }

            try
            {
                RecreateResources(senderName, sharedHandle, width, height, format);
                errorMessage = null;
                return true;
            }
            catch (Exception ex)
            {
                ResetResources();
                errorMessage = $"共有テクスチャを開けませんでした: {ex.Message}";
                return false;
            }
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
        if (_sharedTexture is null || _stagingTexture is null)
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
            _deviceContext!.CopyResource(_stagingTexture, _sharedTexture);
            _deviceContext.Flush();
            var mapped = _deviceContext.Map(_stagingTexture, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);

            try
            {
                unsafe
                {
                    CopyMappedTextureToBgraBuffer(mapped, (byte*)destination);
                }
            }
            finally
            {
                _deviceContext.Unmap(_stagingTexture, 0);
            }

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
        _stagingTexture = _device.CreateTexture2D(stagingDescription);
        _keyedMutex = sharedTexture.QueryInterfaceOrNull<IDXGIKeyedMutex>();
        _accessMutex = _keyedMutex is null
            ? new Mutex(false, $"{senderName}_SpoutAccessMutex")
            : null;
        _sharedHandle = sharedHandle;
        _senderName = senderName;
        _width = width;
        _height = height;
        _format = format;
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
        _stagingTexture?.Dispose();
        _stagingTexture = null;
        _sharedTexture?.Dispose();
        _sharedTexture = null;
        _sharedHandle = IntPtr.Zero;
        _senderName = string.Empty;
        _width = 0;
        _height = 0;
        _format = Format.Unknown;
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

}
