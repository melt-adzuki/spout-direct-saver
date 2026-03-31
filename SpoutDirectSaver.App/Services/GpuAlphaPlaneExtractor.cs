using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using SharpGen.Runtime;
using SpoutDirectSaver.App.Models;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace SpoutDirectSaver.App.Services;

internal sealed class GpuAlphaPlaneExtractor : IAsyncDisposable
{
    private const int CaptureTextureCount = 32;
    private const int IdlePollSleepMilliseconds = 1;
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _deviceContext;
    private readonly ID3D11Multithread? _multithread;
    private readonly bool _ownsDevice;
    private readonly object _captureSlotGate = new();
    private readonly Queue<int> _availableCaptureSlotIndices = new();
    private readonly AlphaCaptureSlot?[] _captureSlots = new AlphaCaptureSlot?[CaptureTextureCount];
    private readonly Queue<PendingReadback> _pendingReadbacks = new();
    private readonly Channel<PendingSubmission> _channel;
    private readonly Task _workerTask;
    private readonly uint _width;
    private readonly uint _height;
    private readonly ID3D11VertexShader _blitVertexShader;
    private readonly ID3D11PixelShader _alphaPixelShader;
    private readonly ID3D11SamplerState _blitSampler;
    private bool _completed;
    private bool _disposed;

    public GpuAlphaPlaneExtractor(int adapterIndex, uint width, uint height, int queueCapacity = CaptureTextureCount)
    {
        _device = CreateDevice(adapterIndex);
        _deviceContext = _device.ImmediateContext;
        _multithread = _deviceContext.QueryInterfaceOrNull<ID3D11Multithread>();
        _multithread?.SetMultithreadProtected(true);
        _ownsDevice = true;
        _width = width;
        _height = height;
        (_blitVertexShader, _alphaPixelShader, _blitSampler) = CreateShadersAndSampler();
        InitializeCaptureSlots(width, height);
        _channel = CreateChannel(queueCapacity);
        _workerTask = StartWorkerTask();
    }

    public GpuAlphaPlaneExtractor(ID3D11Device device, uint width, uint height, int queueCapacity = CaptureTextureCount)
    {
        _device = device;
        _deviceContext = device.ImmediateContext;
        _multithread = _deviceContext.QueryInterfaceOrNull<ID3D11Multithread>();
        _multithread?.SetMultithreadProtected(true);
        _width = width;
        _height = height;
        (_blitVertexShader, _alphaPixelShader, _blitSampler) = CreateShadersAndSampler();
        InitializeCaptureSlots(width, height);
        _channel = CreateChannel(queueCapacity);
        _workerTask = StartWorkerTask();
    }

    public void QueueFrame(GpuTextureFrame gpuFrame, ulong lockKey, ulong releaseKey, Action<PixelBufferLease> onAlphaReady)
    {
        _ = lockKey;
        _ = releaseKey;

        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_completed)
        {
            gpuFrame.Dispose();
            throw new InvalidOperationException("GPU alpha extractor は既に完了しています。");
        }

        ThrowIfFailed();
        var enqueueStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        _channel.Writer.WriteAsync(new PendingSubmission(gpuFrame, onAlphaReady)).AsTask().GetAwaiter().GetResult();
        DebugTrace.WriteTimingIfSlow(
            "GpuAlphaPlaneExtractor",
            "QueueFrame enqueue",
            enqueueStarted,
            2.0,
            $"texture=0x{gpuFrame.Texture.NativePointer.ToInt64():X} size={_width}x{_height}");
    }

    public async Task CompleteAsync()
    {
        if (_completed)
        {
            return;
        }

        _completed = true;
        _channel.Writer.TryComplete();
        await _workerTask.ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _channel.Writer.TryComplete();
        try
        {
            await _workerTask.ConfigureAwait(false);
        }
        catch
        {
            // Ignore cleanup-time failures.
        }

        foreach (var captureSlot in _captureSlots)
        {
            captureSlot?.Dispose();
        }

        _blitSampler.Dispose();
        _alphaPixelShader.Dispose();
        _blitVertexShader.Dispose();
        _multithread?.Dispose();
        if (_ownsDevice)
        {
            _deviceContext.Dispose();
            _device.Dispose();
        }
    }

    private (ID3D11VertexShader VertexShader, ID3D11PixelShader PixelShader, ID3D11SamplerState Sampler) CreateShadersAndSampler()
    {
        var vertexShaderBytecode = Compiler.Compile(
            ShaderSource,
            "vs_main",
            "SpoutDirectSaverAlphaExtract.hlsl",
            "vs_5_0",
            ShaderFlags.OptimizationLevel3,
            EffectFlags.None);
        var alphaPixelShaderBytecode = Compiler.Compile(
            ShaderSource,
            "ps_alpha",
            "SpoutDirectSaverAlphaExtract.hlsl",
            "ps_5_0",
            ShaderFlags.OptimizationLevel3,
            EffectFlags.None);

        return (
            _device.CreateVertexShader(vertexShaderBytecode.Span),
            _device.CreatePixelShader(alphaPixelShaderBytecode.Span),
            _device.CreateSamplerState(
                new SamplerDescription(Filter.MinMagMipPoint, TextureAddressMode.Clamp, TextureAddressMode.Clamp, TextureAddressMode.Clamp)));
    }

    private void InitializeCaptureSlots(uint width, uint height)
    {
        lock (_captureSlotGate)
        {
            for (var index = 0; index < CaptureTextureCount; index++)
            {
                _captureSlots[index] = CreateCaptureSlot(index, width, height);
                _availableCaptureSlotIndices.Enqueue(index);
            }
        }
    }

    private Channel<PendingSubmission> CreateChannel(int queueCapacity)
    {
        return Channel.CreateBounded<PendingSubmission>(new BoundedChannelOptions(Math.Max(queueCapacity, 1))
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });
    }

    private Task StartWorkerTask()
    {
        return Task.Factory.StartNew(
            static state =>
            {
                var extractor = (GpuAlphaPlaneExtractor)state!;
                using var schedulingScope = WindowsScheduling.EnterWriterProfile();
                extractor.WorkLoop();
            },
            this,
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    private void WorkLoop()
    {
        while (true)
        {
            var didWork = false;

            while (_channel.Reader.TryPeek(out _))
            {
                AlphaCaptureSlot? captureSlot = null;
                PendingSubmission? pending = null;
                try
                {
                    if (!TryAcquireCaptureSlot(out captureSlot))
                    {
                        break;
                    }

                    if (!_channel.Reader.TryRead(out pending))
                    {
                        ReleaseCaptureSlot(captureSlot!.Index);
                        continue;
                    }

                    var acquiredSlot = captureSlot!;
                    var stageStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                    StageFrameCopy(pending.SourceFrame.Texture, acquiredSlot.Texture);
                    DebugTrace.WriteTimingIfSlow(
                        "GpuAlphaPlaneExtractor",
                        "StageFrameCopy",
                        stageStarted,
                        2.0,
                        $"slot={acquiredSlot.Index} size={_width}x{_height}");

                    SubmitAlphaExtraction(acquiredSlot);
                    _pendingReadbacks.Enqueue(new PendingReadback(acquiredSlot, pending.OnAlphaReady, ReleaseCaptureSlot));
                    didWork = true;
                }
                catch
                {
                    if (captureSlot is not null)
                    {
                        ReleaseCaptureSlot(captureSlot.Index);
                    }

                    throw;
                }
                finally
                {
                    pending?.Dispose();
                }
            }

            while (TryCompleteReadyReadback())
            {
                didWork = true;
            }

            if (_channel.Reader.Completion.IsCompleted)
            {
                if (_pendingReadbacks.Count == 0)
                {
                    break;
                }

                if (!didWork)
                {
                    CompleteNextReadbackBlocking();
                }

                continue;
            }

            if (!didWork)
            {
                Thread.Sleep(IdlePollSleepMilliseconds);
            }
        }
    }

    private AlphaCaptureSlot CreateCaptureSlot(int index, uint width, uint height)
    {
        var bgraTexture = _device.CreateTexture2D(new Texture2DDescription(
            Format.B8G8R8A8_UNorm,
            width,
            height,
            1,
            1,
            BindFlags.ShaderResource,
            ResourceUsage.Default,
            CpuAccessFlags.None,
            1,
            0,
            ResourceOptionFlags.None));
        var shaderResourceView = _device.CreateShaderResourceView(bgraTexture);
        var alphaRenderTexture = _device.CreateTexture2D(new Texture2DDescription(
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
            ResourceOptionFlags.None));
        var alphaRenderTargetView = _device.CreateRenderTargetView(alphaRenderTexture);
        var alphaReadbackTexture = _device.CreateTexture2D(new Texture2DDescription(
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
        var completionQuery = _device.CreateQuery(new QueryDescription(QueryType.Event, QueryFlags.None));
        return new AlphaCaptureSlot(
            index,
            bgraTexture,
            shaderResourceView,
            alphaRenderTexture,
            alphaRenderTargetView,
            alphaReadbackTexture,
            completionQuery);
    }

    private void StageFrameCopy(ID3D11Texture2D sourceTexture, ID3D11Texture2D destinationTexture)
    {
        ExecuteWithContextLock(() =>
        {
            _deviceContext.CopyResource(destinationTexture, sourceTexture);
        });
    }

    private void SubmitAlphaExtraction(AlphaCaptureSlot captureSlot)
    {
        var submitStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        ExecuteWithContextLock(() =>
        {
            _deviceContext.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
            _deviceContext.VSSetShader(_blitVertexShader);
            _deviceContext.PSSetShader(_alphaPixelShader);
            _deviceContext.PSSetShaderResource(0, captureSlot.ShaderResourceView);
            _deviceContext.PSSetSampler(0, _blitSampler);
            _deviceContext.RSSetViewports([new Viewport(0, 0, _width, _height, 0.0f, 1.0f)]);
            _deviceContext.OMSetRenderTargets(captureSlot.AlphaRenderTargetView, null);
            _deviceContext.Draw(3, 0);
            _deviceContext.PSSetShaderResource(0, (ID3D11ShaderResourceView)null!);
            _deviceContext.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>(), null);
            _deviceContext.CopyResource(captureSlot.AlphaReadbackTexture, captureSlot.AlphaRenderTexture);
            _deviceContext.End(captureSlot.CompletionQuery);
        });

        DebugTrace.WriteTimingIfSlow(
            "GpuAlphaPlaneExtractor",
            "SubmitAlphaExtraction",
            submitStarted,
            2.0,
            $"slot={captureSlot.Index} size={_width}x{_height}");
    }

    private bool TryCompleteReadyReadback()
    {
        if (_pendingReadbacks.Count == 0)
        {
            return false;
        }

        var pending = _pendingReadbacks.Peek();
        if (!IsReadbackReady(pending.CaptureSlot.CompletionQuery, block: false))
        {
            return false;
        }

        _pendingReadbacks.Dequeue();
        CompleteReadback(pending);
        return true;
    }

    private void CompleteNextReadbackBlocking()
    {
        if (_pendingReadbacks.Count == 0)
        {
            return;
        }

        var pending = _pendingReadbacks.Dequeue();
        IsReadbackReady(pending.CaptureSlot.CompletionQuery, block: true);
        CompleteReadback(pending);
    }

    private bool IsReadbackReady(ID3D11Query completionQuery, bool block)
    {
        while (true)
        {
            var result = Result.False;
            ExecuteWithContextLock(() =>
            {
                result = _deviceContext.GetData(
                    completionQuery,
                    IntPtr.Zero,
                    0,
                    block ? AsyncGetDataFlags.None : AsyncGetDataFlags.DoNotFlush);
            });

            if (result.Code == Result.Ok.Code)
            {
                return true;
            }

            if (result.Code != Result.False.Code)
            {
                result.CheckError();
            }

            if (!block)
            {
                return false;
            }

            Thread.Sleep(IdlePollSleepMilliseconds);
        }
    }

    private void CompleteReadback(PendingReadback pending)
    {
        PixelBufferLease? alphaLease = null;
        try
        {
            alphaLease = PixelBufferLease.Rent(checked((int)(_width * _height)));
            var readbackStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            ReadbackAlphaPlane(pending.CaptureSlot, alphaLease.Buffer);
            DebugTrace.WriteTimingIfSlow(
                "GpuAlphaPlaneExtractor",
                "ReadbackAlphaPlane",
                readbackStarted,
                2.0,
                $"slot={pending.CaptureSlot.Index} size={_width}x{_height}");
            pending.OnAlphaReady(alphaLease);
            alphaLease = null;
        }
        finally
        {
            alphaLease?.Dispose();
            pending.Dispose();
        }
    }

    private void ReadbackAlphaPlane(AlphaCaptureSlot captureSlot, byte[] destination)
    {
        ExecuteWithContextLock(() =>
        {
            var mapped = _deviceContext.Map(captureSlot.AlphaReadbackTexture, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                unsafe
                {
                    var pixelWidth = checked((int)_width);
                    var pixelHeight = checked((int)_height);
                    for (var row = 0; row < pixelHeight; row++)
                    {
                        var sourceRow = (byte*)mapped.DataPointer + (row * mapped.RowPitch);
                        Marshal.Copy((IntPtr)sourceRow, destination, row * pixelWidth, pixelWidth);
                    }
                }
            }
            finally
            {
                _deviceContext.Unmap(captureSlot.AlphaReadbackTexture, 0);
            }
        });
    }

    private AlphaCaptureSlot AcquireCaptureSlot()
    {
        lock (_captureSlotGate)
        {
            while (_availableCaptureSlotIndices.Count == 0)
            {
                Monitor.Wait(_captureSlotGate);
            }

            return _captureSlots[_availableCaptureSlotIndices.Dequeue()]!;
        }
    }

    private bool TryAcquireCaptureSlot(out AlphaCaptureSlot? captureSlot)
    {
        lock (_captureSlotGate)
        {
            if (_availableCaptureSlotIndices.Count == 0)
            {
                captureSlot = null;
                return false;
            }

            captureSlot = _captureSlots[_availableCaptureSlotIndices.Dequeue()]!;
            return true;
        }
    }

    private void ReleaseCaptureSlot(int index)
    {
        lock (_captureSlotGate)
        {
            if (_captureSlots[index] is null)
            {
                return;
            }

            _availableCaptureSlotIndices.Enqueue(index);
            Monitor.Pulse(_captureSlotGate);
        }
    }

    private void ExecuteWithContextLock(Action action)
    {
        if (_multithread is not null)
        {
            _multithread.Enter();
            try
            {
                action();
            }
            finally
            {
                _multithread.Leave();
            }

            return;
        }

        action();
    }

    private void ThrowIfFailed()
    {
        if (_workerTask.Exception is { } exception)
        {
            throw exception.GetBaseException();
        }
    }

    private static ID3D11Device CreateDevice(int adapterIndex)
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        IDXGIAdapter1? adapter = null;
        try
        {
            if (adapterIndex >= 0)
            {
                var adapterResult = factory.EnumAdapters1((uint)adapterIndex, out adapter);
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
                    context.Dispose();
                    return device;
                }
            }

            return D3D11.D3D11CreateDevice(
                DriverType.Hardware,
                DeviceCreationFlags.BgraSupport,
                Array.Empty<FeatureLevel>());
        }
        finally
        {
            adapter?.Dispose();
        }
    }

    private sealed class PendingSubmission : IDisposable
    {
        public PendingSubmission(GpuTextureFrame sourceFrame, Action<PixelBufferLease> onAlphaReady)
        {
            SourceFrame = sourceFrame;
            OnAlphaReady = onAlphaReady;
        }

        public GpuTextureFrame SourceFrame { get; }

        public Action<PixelBufferLease> OnAlphaReady { get; }

        public void Dispose()
        {
            SourceFrame.Dispose();
        }
    }

    private sealed class PendingReadback : IDisposable
    {
        private readonly Action<int> _releaseCaptureSlot;

        public PendingReadback(AlphaCaptureSlot captureSlot, Action<PixelBufferLease> onAlphaReady, Action<int> releaseCaptureSlot)
        {
            CaptureSlot = captureSlot;
            OnAlphaReady = onAlphaReady;
            _releaseCaptureSlot = releaseCaptureSlot;
        }

        public AlphaCaptureSlot CaptureSlot { get; }

        public Action<PixelBufferLease> OnAlphaReady { get; }

        public void Dispose()
        {
            _releaseCaptureSlot(CaptureSlot.Index);
        }
    }

    private sealed class AlphaCaptureSlot : IDisposable
    {
        public AlphaCaptureSlot(
            int index,
            ID3D11Texture2D texture,
            ID3D11ShaderResourceView shaderResourceView,
            ID3D11Texture2D alphaRenderTexture,
            ID3D11RenderTargetView alphaRenderTargetView,
            ID3D11Texture2D alphaReadbackTexture,
            ID3D11Query completionQuery)
        {
            Index = index;
            Texture = texture;
            ShaderResourceView = shaderResourceView;
            AlphaRenderTexture = alphaRenderTexture;
            AlphaRenderTargetView = alphaRenderTargetView;
            AlphaReadbackTexture = alphaReadbackTexture;
            CompletionQuery = completionQuery;
        }

        public int Index { get; }

        public ID3D11Texture2D Texture { get; }

        public ID3D11ShaderResourceView ShaderResourceView { get; }

        public ID3D11Texture2D AlphaRenderTexture { get; }

        public ID3D11RenderTargetView AlphaRenderTargetView { get; }

        public ID3D11Texture2D AlphaReadbackTexture { get; }

        public ID3D11Query CompletionQuery { get; }

        public void Dispose()
        {
            CompletionQuery.Dispose();
            AlphaReadbackTexture.Dispose();
            AlphaRenderTargetView.Dispose();
            AlphaRenderTexture.Dispose();
            ShaderResourceView.Dispose();
            Texture.Dispose();
        }
    }

    private const string ShaderSource = """
Texture2D inputTexture : register(t0);
SamplerState inputSampler : register(s0);

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

float4 ps_alpha(VsOutput input) : SV_Target
{
    float4 color = inputTexture.SampleLevel(inputSampler, input.uv, 0.0);
    return float4(color.a, 0.0, 0.0, 1.0);
}
""";
}
