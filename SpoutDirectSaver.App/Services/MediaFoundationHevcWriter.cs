using System;
using System.IO;
using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.Direct3D11;
using Vortice.MediaFoundation;

namespace SpoutDirectSaver.App.Services;

internal sealed class MediaFoundationHevcWriter : IDisposable
{
    private const long HundredNanosecondsPerSecond = 10_000_000;
    private static readonly object StartupGate = new();
    private static int _startupRefCount;

    private readonly IMFSinkWriter _sinkWriter;
    private readonly int _streamIndex;
    private readonly long _frameDurationHns;
    private readonly int _gpuFrameLength;
    private readonly IMFDXGIDeviceManager? _deviceManager;
    private readonly D3D11Nv12TextureConverter? _nv12Converter;
    private long _submittedFrameCount;
    private bool _completed;
    private bool _disposed;

    public MediaFoundationHevcWriter(
        uint width,
        uint height,
        double frameRate,
        string outputPath,
        ID3D11Device? device = null,
        uint averageBitrate = 40_000_000)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        EnsureMediaFoundationStarted();

        using var sinkWriterAttributes = MediaFactory.MFCreateAttributes(6);
        sinkWriterAttributes.Set(SinkWriterAttributeKeys.ReadwriteEnableHardwareTransforms, 1u);
        sinkWriterAttributes.Set(SinkWriterAttributeKeys.DisableThrottling, 1u);
        sinkWriterAttributes.Set(SinkWriterAttributeKeys.LowLatency, 1u);
        sinkWriterAttributes.Set(SinkWriterAttributeKeys.ReadwriteMmcssClass, "Capture");
        sinkWriterAttributes.Set(SinkWriterAttributeKeys.ReadwriteMmcssPriority, 2u);
        if (device is not null)
        {
            _deviceManager = MediaFactory.MFCreateDXGIDeviceManager();
            _deviceManager.ResetDevice(device).CheckError();
            sinkWriterAttributes.Set(SinkWriterAttributeKeys.D3DManager, _deviceManager);
            _nv12Converter = new D3D11Nv12TextureConverter(device, device.ImmediateContext, width, height, frameRate);
        }

        _sinkWriter = MediaFactory.MFCreateSinkWriterFromURL(outputPath, null, sinkWriterAttributes);

        using var outputMediaType = MediaFactory.MFCreateMediaType();
        outputMediaType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        outputMediaType.Set(MediaTypeAttributeKeys.Subtype, VideoFormatGuids.Hevc);
        outputMediaType.Set(MediaTypeAttributeKeys.AvgBitrate, averageBitrate);
        outputMediaType.Set(MediaTypeAttributeKeys.InterlaceMode, (uint)VideoInterlaceMode.Progressive);
        MediaFactory.MFSetAttributeSize(outputMediaType, MediaTypeAttributeKeys.FrameSize, width, height).CheckError();
        SetFrameRate(outputMediaType, frameRate);
        MediaFactory.MFSetAttributeRatio(outputMediaType, MediaTypeAttributeKeys.PixelAspectRatio, 1, 1).CheckError();
        ApplyEncodedVideoColorAttributes(outputMediaType);

        _streamIndex = _sinkWriter.AddStream(outputMediaType);

        using var inputMediaType = MediaFactory.MFCreateMediaType();
        inputMediaType.Set(MediaTypeAttributeKeys.MajorType, MediaTypeGuids.Video);
        inputMediaType.Set(MediaTypeAttributeKeys.Subtype, device is not null ? VideoFormatGuids.NV12 : VideoFormatGuids.Rgb32);
        inputMediaType.Set(MediaTypeAttributeKeys.FixedSizeSamples, 1u);
        inputMediaType.Set(MediaTypeAttributeKeys.AllSamplesIndependent, 1u);
        inputMediaType.Set(MediaTypeAttributeKeys.InterlaceMode, (uint)VideoInterlaceMode.Progressive);
        inputMediaType.Set(MediaTypeAttributeKeys.DefaultStride, checked((uint)(device is not null ? width : width * 4)));
        inputMediaType.Set(MediaTypeAttributeKeys.SampleSize, checked((uint)(device is not null ? (width * height * 3 / 2) : (width * height * 4))));
        MediaFactory.MFSetAttributeSize(inputMediaType, MediaTypeAttributeKeys.FrameSize, width, height).CheckError();
        SetFrameRate(inputMediaType, frameRate);
        MediaFactory.MFSetAttributeRatio(inputMediaType, MediaTypeAttributeKeys.PixelAspectRatio, 1, 1).CheckError();
        ApplyInputVideoColorAttributes(inputMediaType, device is not null);

        _sinkWriter.SetInputMediaType(_streamIndex, inputMediaType, null);
        _sinkWriter.BeginWriting();
        _frameDurationHns = Math.Max(1, (long)Math.Round(HundredNanosecondsPerSecond / frameRate));
        _gpuFrameLength = checked((int)(width * height * 3 / 2));
    }

    public void WriteFrame(byte[] bgraFrame, int frameLength, int repeatCount)
    {
        if (repeatCount <= 0)
        {
            return;
        }

        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_completed)
        {
            throw new InvalidOperationException("Media Foundation writer は既に完了しています。");
        }

        for (var index = 0; index < repeatCount; index++)
        {
            using var mediaBuffer = MediaFactory.MFCreateMemoryBuffer(frameLength);
            IntPtr destination = IntPtr.Zero;

            try
            {
                mediaBuffer.Lock(out destination, out _, out _);
                Marshal.Copy(bgraFrame, 0, destination, frameLength);
            }
            finally
            {
                if (destination != IntPtr.Zero)
                {
                    mediaBuffer.Unlock();
                }
            }

            mediaBuffer.CurrentLength = frameLength;

            using var sample = MediaFactory.MFCreateSample();
            sample.AddBuffer(mediaBuffer);
            sample.SampleTime = _submittedFrameCount * _frameDurationHns;
            sample.SampleDuration = _frameDurationHns;
            _sinkWriter.WriteSample(_streamIndex, sample);
            _submittedFrameCount++;
        }
    }

    public void WriteTextureFrame(ID3D11Texture2D texture, int repeatCount)
    {
        if (repeatCount <= 0)
        {
            return;
        }

        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_completed)
        {
            throw new InvalidOperationException("Media Foundation writer は既に完了しています。");
        }

        if (_deviceManager is null)
        {
            throw new InvalidOperationException("GPU texture 書き込み用の D3D manager が初期化されていません。");
        }

        if (_nv12Converter is null)
        {
            throw new InvalidOperationException("NV12 converter が初期化されていません。");
        }

        ID3D11Texture2D nv12Texture;
        try
        {
            nv12Texture = _nv12Converter.Convert(texture);
        }
        catch (Exception ex)
        {
            DebugTrace.WriteLine(
                "MediaFoundationHevcWriter",
                $"Convert texture failed frame={_submittedFrameCount} repeatCount={repeatCount} hresult=0x{ex.HResult:X8} error={ex.GetType().Name}: {ex.Message}");
            throw;
        }

        for (var index = 0; index < repeatCount; index++)
        {
            try
            {
                using var mediaBuffer = MediaFactory.MFCreateDXGISurfaceBuffer(typeof(ID3D11Texture2D).GUID, nv12Texture, 0, false);
                using var contiguousBuffer = mediaBuffer.QueryInterfaceOrNull<IMF2DBuffer>();
                if (contiguousBuffer is not null)
                {
                    mediaBuffer.CurrentLength = contiguousBuffer.ContiguousLength;
                }
                else
                {
                    mediaBuffer.CurrentLength = _gpuFrameLength;
                }

                MediaFactory.MFCreateVideoSampleFromSurface((IUnknown?)null, out var sample).CheckError();
                using (sample)
                {
                    sample.AddBuffer(mediaBuffer);
                    sample.SampleTime = _submittedFrameCount * _frameDurationHns;
                    sample.SampleDuration = _frameDurationHns;
                    _sinkWriter.WriteSample(_streamIndex, sample);
                    _submittedFrameCount++;
                }
            }
            catch (Exception ex)
            {
                DebugTrace.WriteLine(
                    "MediaFoundationHevcWriter",
                    $"WriteTextureFrame failed frame={_submittedFrameCount} repeatIndex={index} texturePtr=0x{nv12Texture.NativePointer.ToInt64():X} hresult=0x{ex.HResult:X8} error={ex.GetType().Name}: {ex.Message}");
                throw;
            }
        }
    }

    public void Complete()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_completed)
        {
            return;
        }

        _completed = true;
        _sinkWriter.Finalize();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _sinkWriter.Dispose();
        _nv12Converter?.Dispose();
        _deviceManager?.Dispose();
        ShutdownMediaFoundationIfNeeded();
    }

    private static void EnsureMediaFoundationStarted()
    {
        lock (StartupGate)
        {
            if (_startupRefCount == 0)
            {
                MediaFactory.MFStartup();
            }

            _startupRefCount++;
        }
    }

    private static void ShutdownMediaFoundationIfNeeded()
    {
        lock (StartupGate)
        {
            if (_startupRefCount <= 0)
            {
                return;
            }

            _startupRefCount--;
            if (_startupRefCount == 0)
            {
                MediaFactory.MFShutdown();
            }
        }
    }

    private static void SetFrameRate(IMFAttributes attributes, double frameRate)
    {
        var fpsNumerator = (uint)Math.Clamp((int)Math.Round(frameRate * 1000.0), 1, int.MaxValue);
        const uint fpsDenominator = 1000;
        MediaFactory.MFSetAttributeRatio(attributes, MediaTypeAttributeKeys.FrameRate, fpsNumerator, fpsDenominator).CheckError();
    }

    private static void ApplyEncodedVideoColorAttributes(IMFAttributes attributes)
    {
        attributes.Set(MediaTypeAttributeKeys.VideoPrimaries, (uint)VideoPrimaries.Bt709);
        attributes.Set(MediaTypeAttributeKeys.TransferFunction, (uint)VideoTransferFunction.Func709);
        attributes.Set(MediaTypeAttributeKeys.YuvMatrix, (uint)VideoTransferMatrix.Bt709);
        attributes.Set(MediaTypeAttributeKeys.VideoNominalRange, (uint)NominalRange.Range16_235);
    }

    private static void ApplyInputVideoColorAttributes(IMFAttributes attributes, bool gpuNv12Input)
    {
        attributes.Set(MediaTypeAttributeKeys.VideoPrimaries, (uint)VideoPrimaries.Bt709);
        attributes.Set(
            MediaTypeAttributeKeys.TransferFunction,
            (uint)(gpuNv12Input ? VideoTransferFunction.Func709 : VideoTransferFunction.FuncSRGB));
        attributes.Set(MediaTypeAttributeKeys.YuvMatrix, (uint)VideoTransferMatrix.Bt709);
        attributes.Set(
            MediaTypeAttributeKeys.VideoNominalRange,
            (uint)(gpuNv12Input ? NominalRange.Range16_235 : NominalRange.Range0_255));
    }
}
