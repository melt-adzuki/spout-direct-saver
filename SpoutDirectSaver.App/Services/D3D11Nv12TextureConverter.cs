using System;
using System.Collections.Generic;
using SharpGen.Runtime;
using Vortice;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace SpoutDirectSaver.App.Services;

internal sealed class D3D11Nv12TextureConverter : IDisposable
{
    private const int OutputTextureCount = 10;
    private readonly ID3D11Texture2D[] _outputTextures;
    private readonly ID3D11DeviceContext _deviceContext;
    private readonly ID3D11VideoDevice _videoDevice;
    private readonly ID3D11VideoContext _videoContext;
    private readonly ID3D11VideoContext1? _videoContext1;
    private readonly ID3D11VideoProcessorEnumerator _videoProcessorEnumerator;
    private readonly ID3D11VideoProcessor _videoProcessor;
    private readonly ID3D11VideoProcessorOutputView[] _outputViews;
    private readonly Dictionary<nint, ID3D11VideoProcessorInputView> _inputViews = new();
    private readonly RawRect _sourceRect;
    private readonly RawRect _targetRect;
    private int _outputIndex;
    private bool _disposed;

    public D3D11Nv12TextureConverter(ID3D11Device device, ID3D11DeviceContext deviceContext, uint width, uint height, double frameRate)
    {
        _deviceContext = deviceContext;
        _videoDevice = device.QueryInterface<ID3D11VideoDevice>();
        _videoContext = deviceContext.QueryInterface<ID3D11VideoContext>();
        _videoContext1 = deviceContext.QueryInterfaceOrNull<ID3D11VideoContext1>();

        var frameRateRatio = CreateFrameRateRatio(frameRate);
        var contentDescription = new VideoProcessorContentDescription
        {
            InputFrameFormat = VideoFrameFormat.Progressive,
            InputFrameRate = frameRateRatio,
            InputWidth = width,
            InputHeight = height,
            OutputFrameRate = frameRateRatio,
            OutputWidth = width,
            OutputHeight = height,
            Usage = VideoUsage.OptimalSpeed
        };

        _videoDevice.CreateVideoProcessorEnumerator(contentDescription, out _videoProcessorEnumerator).CheckError();
        _videoDevice.CreateVideoProcessor(_videoProcessorEnumerator, 0, out _videoProcessor).CheckError();

        var outputDescription = new Texture2DDescription(
            Format.NV12,
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
        _outputTextures = new ID3D11Texture2D[OutputTextureCount];
        _outputViews = new ID3D11VideoProcessorOutputView[OutputTextureCount];
        var outputViewDescription = new VideoProcessorOutputViewDescription
        {
            ViewDimension = VideoProcessorOutputViewDimension.Texture2D,
            Texture2D = new Texture2DVideoProcessorOutputView
            {
                MipSlice = 0
            }
        };

        for (var index = 0; index < OutputTextureCount; index++)
        {
            _outputTextures[index] = device.CreateTexture2D(outputDescription);
            _videoDevice.CreateVideoProcessorOutputView(
                _outputTextures[index],
                _videoProcessorEnumerator,
                outputViewDescription,
                out _outputViews[index]).CheckError();
        }

        _sourceRect = new RawRect(0, 0, checked((int)width), checked((int)height));
        _targetRect = _sourceRect;

        _videoContext.VideoProcessorSetStreamFrameFormat(_videoProcessor, 0, VideoFrameFormat.Progressive);
        _videoContext.VideoProcessorSetStreamSourceRect(_videoProcessor, 0, true, _sourceRect);
        _videoContext.VideoProcessorSetStreamDestRect(_videoProcessor, 0, true, _targetRect);
        _videoContext.VideoProcessorSetOutputTargetRect(_videoProcessor, true, _targetRect);
        ConfigureColorSpace();
    }

    public ID3D11Texture2D Convert(ID3D11Texture2D inputTexture)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var outputIndex = _outputIndex;
        var outputTexture = _outputTextures[outputIndex];
        var outputView = _outputViews[outputIndex];
        _outputIndex = (_outputIndex + 1) % OutputTextureCount;

        var stream = new VideoProcessorStream
        {
            Enable = true,
            OutputIndex = 0,
            InputFrameOrField = 0,
            PastFrames = 0,
            FutureFrames = 0,
            InputSurface = GetOrCreateInputView(inputTexture)
        };

        _videoContext.VideoProcessorBlt(_videoProcessor, outputView, 0, 1, [stream]).CheckError();
        _deviceContext.Flush();
        return outputTexture;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (var inputView in _inputViews.Values)
        {
            inputView.Dispose();
        }

        _inputViews.Clear();
        foreach (var outputView in _outputViews)
        {
            outputView.Dispose();
        }

        foreach (var outputTexture in _outputTextures)
        {
            outputTexture.Dispose();
        }

        _videoProcessor.Dispose();
        _videoProcessorEnumerator.Dispose();
        _videoContext1?.Dispose();
        _videoContext.Dispose();
        _videoDevice.Dispose();
    }

    private void ConfigureColorSpace()
    {
        if (_videoContext1 is null)
        {
            DebugTrace.WriteLine(
                "D3D11Nv12TextureConverter",
                "ID3D11VideoContext1 unavailable; using driver default color space for BGRA -> NV12 conversion.");
            return;
        }

        // Match OBS' SDR path semantics: full-range RGB input into limited-range BT.709 NV12.
        _videoContext1.VideoProcessorSetStreamColorSpace1(_videoProcessor, 0, ColorSpaceType.RgbFullG22NoneP709);
        _videoContext1.VideoProcessorSetOutputColorSpace1(_videoProcessor, ColorSpaceType.YcbcrStudioG22LeftP709);
    }

    private ID3D11VideoProcessorInputView GetOrCreateInputView(ID3D11Texture2D inputTexture)
    {
        var key = inputTexture.NativePointer;
        if (_inputViews.TryGetValue(key, out var inputView))
        {
            return inputView;
        }

        var inputViewDescription = new VideoProcessorInputViewDescription
        {
            FourCC = 0,
            ViewDimension = VideoProcessorInputViewDimension.Texture2D,
            Texture2D = new Texture2DVideoProcessorInputView
            {
                MipSlice = 0,
                ArraySlice = 0
            }
        };

        _videoDevice.CreateVideoProcessorInputView(inputTexture, _videoProcessorEnumerator, inputViewDescription, out inputView).CheckError();
        _inputViews.Add(key, inputView);
        return inputView;
    }

    private static Rational CreateFrameRateRatio(double frameRate)
    {
        var numerator = (uint)Math.Clamp((int)Math.Round(frameRate * 1000.0), 1, int.MaxValue);
        return new Rational(numerator, 1000u);
    }
}
