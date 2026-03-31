using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using SharpGen.Runtime;
using SpoutDirectSaver.App.Models;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace SpoutDirectSaver.App.Services;

internal sealed class RealtimeRgbNvencWriter : IAsyncDisposable
{
    private const int GpuCopyTextureCount = 32;
    private const int OutputReadyRetryCount = 50;
    private readonly MediaFoundationHevcWriter _writer;
    private readonly Channel<PendingFrame> _channel;
    private readonly Task _writeTask;
    private readonly object _gpuCopyGate = new();
    private readonly Queue<int> _availableGpuCopySlotIndices = new();
    private readonly GpuCopySlot?[] _gpuCopySlots = new GpuCopySlot?[GpuCopyTextureCount];
    private readonly ID3D11Device? _device;
    private readonly ID3D11DeviceContext? _deviceContext;
    private readonly ID3D11Multithread? _multithread;
    private readonly string _outputPath;
    private readonly bool _ownsDevice;
    private bool _writerDisposed;
    private bool _completed;
    private bool _disposed;

    public RealtimeRgbNvencWriter(
        uint width,
        uint height,
        double frameRate,
        string outputPath,
        int? adapterIndex = null,
        int queueCapacity = GpuCopyTextureCount)
    {
        _outputPath = outputPath;

        if (adapterIndex.HasValue)
        {
            _device = CreateDevice(adapterIndex.Value);
            _deviceContext = _device.ImmediateContext;
            _multithread = _deviceContext.QueryInterfaceOrNull<ID3D11Multithread>();
            _multithread?.SetMultithreadProtected(true);
            InitializeGpuCopySlots(_device, width, height);
            _ownsDevice = true;
        }

        _writer = new MediaFoundationHevcWriter(width, height, frameRate, outputPath, _device);
        DebugTrace.WriteLine(
            "RealtimeRgbNvencWriter",
            $"create path={_outputPath} device={(_device is not null ? "gpu" : "cpu")} adapter={(adapterIndex.HasValue ? adapterIndex.Value : -1)}");

        _channel = CreateChannel(queueCapacity);
        _writeTask = StartWriteTask();
    }

    public RealtimeRgbNvencWriter(
        uint width,
        uint height,
        double frameRate,
        string outputPath,
        ID3D11Device device,
        int queueCapacity = GpuCopyTextureCount)
    {
        _outputPath = outputPath;
        _device = device;
        _deviceContext = device.ImmediateContext;
        _multithread = _deviceContext.QueryInterfaceOrNull<ID3D11Multithread>();
        _multithread?.SetMultithreadProtected(true);
        InitializeGpuCopySlots(device, width, height);
        _writer = new MediaFoundationHevcWriter(width, height, frameRate, outputPath, device);
        DebugTrace.WriteLine(
            "RealtimeRgbNvencWriter",
            $"create path={_outputPath} device=gpu-shared");

        _channel = CreateChannel(queueCapacity);
        _writeTask = StartWriteTask();
    }

    public void QueueFrame(PixelBufferLease pixelData, int repeatCount)
    {
        if (repeatCount <= 0)
        {
            pixelData.Dispose();
            return;
        }

        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_completed)
        {
            pixelData.Dispose();
            throw new InvalidOperationException("Realtime RGB writer は既に完了しています。");
        }

        ThrowIfWriteFailed();
        var enqueueStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        _channel.Writer.WriteAsync(PendingFrame.ForCpu(pixelData, repeatCount)).AsTask().GetAwaiter().GetResult();
        DebugTrace.WriteTimingIfSlow(
            "RealtimeRgbNvencWriter",
            "QueueFrameCpu enqueue",
            enqueueStarted,
            2.0,
            $"repeat={repeatCount}");
    }

    public void QueueFrame(GpuTextureFrame gpuFrame, int repeatCount, ulong lockKey, ulong releaseKey)
    {
        _ = lockKey;
        _ = releaseKey;

        if (repeatCount <= 0)
        {
            gpuFrame.Dispose();
            return;
        }

        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_completed)
        {
            gpuFrame.Dispose();
            throw new InvalidOperationException("Realtime RGB writer は既に完了しています。");
        }

        if (_deviceContext is null)
        {
            gpuFrame.Dispose();
            throw new InvalidOperationException("GPU texture 書き込み用の D3D manager が初期化されていません。");
        }

        ThrowIfWriteFailed();
        var enqueueStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        _channel.Writer.WriteAsync(PendingFrame.ForGpu(gpuFrame, repeatCount)).AsTask().GetAwaiter().GetResult();
        DebugTrace.WriteTimingIfSlow(
            "RealtimeRgbNvencWriter",
            "QueueFrameGpu enqueue",
            enqueueStarted,
            2.0,
            $"repeat={repeatCount} texture=0x{gpuFrame.Texture.NativePointer.ToInt64():X}");
    }

    public async Task CompleteAsync(CancellationToken cancellationToken)
    {
        if (_completed)
        {
            return;
        }

        _completed = true;
        DebugTrace.WriteLine("RealtimeRgbNvencWriter", $"complete start path={_outputPath}");
        _channel.Writer.TryComplete();
        await _writeTask.ConfigureAwait(false);
        _writer.Complete();
        if (!await WaitForOutputAsync(cancellationToken, OutputReadyRetryCount / 2).ConfigureAwait(false))
        {
            DebugTrace.WriteLine("RealtimeRgbNvencWriter", $"complete phase1 timeout path={_outputPath}");
            DisposeInnerWriter();
            if (!await WaitForOutputAsync(cancellationToken, OutputReadyRetryCount).ConfigureAwait(false))
            {
                DebugTrace.WriteLine("RealtimeRgbNvencWriter", $"complete fail path={_outputPath}");
                throw new InvalidOperationException($"RGB intermediate が出力されませんでした: {_outputPath}");
            }
        }

        DebugTrace.WriteLine(
            "RealtimeRgbNvencWriter",
            $"complete success path={_outputPath} size={(File.Exists(_outputPath) ? new FileInfo(_outputPath).Length : -1)}");
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
            await _writeTask.ConfigureAwait(false);
        }
        catch
        {
            // Ignore cleanup-time writer failures.
        }

        DisposeInnerWriter();
        foreach (var gpuCopySlot in _gpuCopySlots)
        {
            gpuCopySlot?.Dispose();
        }

        _multithread?.Dispose();
        if (_ownsDevice)
        {
            _deviceContext?.Dispose();
            _device?.Dispose();
        }
    }

    private Channel<PendingFrame> CreateChannel(int queueCapacity)
    {
        return Channel.CreateBounded<PendingFrame>(new BoundedChannelOptions(Math.Max(queueCapacity, 1))
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait
        });
    }

    private Task StartWriteTask()
    {
        return Task.Factory.StartNew(
            static state =>
            {
                var writer = (RealtimeRgbNvencWriter)state!;
                using var schedulingScope = WindowsScheduling.EnterRealtimeWriterProfile();
                writer.WriteLoop();
            },
            this,
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    private void WriteLoop()
    {
        while (_channel.Reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
        {
            while (_channel.Reader.TryRead(out var pending))
            {
                try
                {
                    var submitStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                    if (pending.SharedGpuFrame is not null)
                    {
                        var gpuCopySlot = AcquireGpuCopySlot();
                        try
                        {
                            try
                            {
                                var gpuCopyStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                                CopyGpuFrame(pending.SharedGpuFrame.Texture, gpuCopySlot.Texture);
                                DebugTrace.WriteTimingIfSlow(
                                    "RealtimeRgbNvencWriter",
                                    "CopyGpuFrame",
                                    gpuCopyStarted,
                                    2.0,
                                    $"repeat={pending.RepeatCount} slot={gpuCopySlot.Index}");
                            }
                            catch (Exception ex)
                            {
                                DebugTrace.WriteLine(
                                    "RealtimeRgbNvencWriter",
                                    $"CopyGpuFrame failed repeat={pending.RepeatCount} slot={gpuCopySlot.Index} texture=0x{pending.SharedGpuFrame.Texture.NativePointer.ToInt64():X} hresult=0x{ex.HResult:X8} error={ex.GetType().Name}: {ex.Message}");
                                throw;
                            }

                            try
                            {
                                _writer.WriteTextureFrame(gpuCopySlot.Texture, pending.RepeatCount);
                            }
                            catch (Exception ex)
                            {
                                DebugTrace.WriteLine(
                                    "RealtimeRgbNvencWriter",
                                    $"WriteTextureFrame failed repeat={pending.RepeatCount} slot={gpuCopySlot.Index} texture=0x{gpuCopySlot.Texture.NativePointer.ToInt64():X} hresult=0x{ex.HResult:X8} error={ex.GetType().Name}: {ex.Message}");
                                throw;
                            }
                        }
                        finally
                        {
                            ReleaseGpuCopySlot(gpuCopySlot.Index);
                        }
                    }
                    else if (pending.PixelData is not null)
                    {
                        _writer.WriteFrame(pending.PixelData.Buffer, pending.PixelData.Length, pending.RepeatCount);
                    }
                    else
                    {
                        throw new InvalidOperationException("RGB writer pending frame payload が存在しません。");
                    }

                    DebugTrace.WriteTimingIfSlow(
                        "RealtimeRgbNvencWriter",
                        "Writer submit",
                        submitStarted,
                        3.0,
                        $"gpu={pending.SharedGpuFrame is not null} cpu={pending.PixelData is not null} repeat={pending.RepeatCount}");
                }
                catch (Exception ex)
                {
                    DebugTrace.WriteLine(
                        "RealtimeRgbNvencWriter",
                        $"write failed path={_outputPath} gpu={pending.SharedGpuFrame is not null} cpu={pending.PixelData is not null} repeat={pending.RepeatCount} error={ex.GetType().Name}: {ex.Message}");
                    throw;
                }
                finally
                {
                    pending.Dispose();
                }
            }
        }
    }

    private void ThrowIfWriteFailed()
    {
        if (_writeTask.Exception is { } exception)
        {
            throw exception.GetBaseException();
        }
    }

    private void DisposeInnerWriter()
    {
        if (_writerDisposed)
        {
            return;
        }

        DebugTrace.WriteLine("RealtimeRgbNvencWriter", $"dispose inner writer path={_outputPath}");
        _writer.Dispose();
        _writerDisposed = true;
    }

    private async Task<bool> WaitForOutputAsync(CancellationToken cancellationToken, int retryCount)
    {
        for (var index = 0; index < retryCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (File.Exists(_outputPath))
            {
                var fileInfo = new FileInfo(_outputPath);
                if (fileInfo.Exists && fileInfo.Length > 0)
                {
                    return true;
                }
            }

            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    private void InitializeGpuCopySlots(ID3D11Device device, uint width, uint height)
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

        lock (_gpuCopyGate)
        {
            for (var index = 0; index < GpuCopyTextureCount; index++)
            {
                _gpuCopySlots[index] = new GpuCopySlot(index, device.CreateTexture2D(textureDescription));
                _availableGpuCopySlotIndices.Enqueue(index);
            }
        }
    }

    private GpuCopySlot AcquireGpuCopySlot()
    {
        lock (_gpuCopyGate)
        {
            while (_availableGpuCopySlotIndices.Count == 0)
            {
                Monitor.Wait(_gpuCopyGate);
            }

            return _gpuCopySlots[_availableGpuCopySlotIndices.Dequeue()]
                ?? throw new InvalidOperationException("GPU copy slot が初期化されていません。");
        }
    }

    private void ReleaseGpuCopySlot(int index)
    {
        lock (_gpuCopyGate)
        {
            if (_gpuCopySlots[index] is null)
            {
                return;
            }

            _availableGpuCopySlotIndices.Enqueue(index);
            Monitor.Pulse(_gpuCopyGate);
        }
    }

    private void CopyGpuFrame(ID3D11Texture2D sourceTexture, ID3D11Texture2D destinationTexture)
    {
        if (_deviceContext is null)
        {
            throw new InvalidOperationException("GPU texture 書き込み用の D3D manager が初期化されていません。");
        }

        ExecuteWithContextLock(() =>
        {
            _deviceContext.CopyResource(destinationTexture, sourceTexture);
        });
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

    private sealed class PendingFrame : IDisposable
    {
        private PendingFrame(PixelBufferLease? pixelData, GpuTextureFrame? sharedGpuFrame, int repeatCount)
        {
            PixelData = pixelData;
            SharedGpuFrame = sharedGpuFrame;
            RepeatCount = repeatCount;
        }

        public PixelBufferLease? PixelData { get; }

        public GpuTextureFrame? SharedGpuFrame { get; }

        public int RepeatCount { get; }

        public static PendingFrame ForCpu(PixelBufferLease pixelData, int repeatCount)
            => new(pixelData, null, repeatCount);

        public static PendingFrame ForGpu(GpuTextureFrame gpuFrame, int repeatCount)
            => new(null, gpuFrame, repeatCount);

        public void Dispose()
        {
            PixelData?.Dispose();
            SharedGpuFrame?.Dispose();
        }
    }

    private sealed class GpuCopySlot : IDisposable
    {
        public GpuCopySlot(int index, ID3D11Texture2D texture)
        {
            Index = index;
            Texture = texture;
        }

        public int Index { get; }

        public ID3D11Texture2D Texture { get; }

        public void Dispose() => Texture.Dispose();
    }
}
