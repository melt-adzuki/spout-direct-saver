using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using SpoutDirectSaver.App.Models;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace SpoutDirectSaver.App.Services;

internal sealed class RealtimeRgbNvencWriter : IAsyncDisposable
{
    private const int GpuCopyTextureCount = 10;
    private const int OutputReadyRetryCount = 50;
    private readonly MediaFoundationHevcWriter _writer;
    private readonly Channel<PendingFrame> _channel;
    private readonly Task _writeTask;
    private readonly object _gpuCopyGate = new();
    private readonly Queue<int> _availableGpuCopySlotIndices = new();
    private readonly GpuCopySlot?[] _gpuCopySlots = new GpuCopySlot?[GpuCopyTextureCount];
    private readonly ID3D11DeviceContext? _deviceContext;
    private readonly ID3D11Multithread? _multithread;
    private readonly string _outputPath;
    private bool _writerDisposed;
    private bool _completed;
    private bool _disposed;

    public RealtimeRgbNvencWriter(
        uint width,
        uint height,
        double frameRate,
        string outputPath,
        ID3D11Device? device = null,
        int queueCapacity = 4,
        RgbMediaFoundationEncoderSettings? settings = null)
    {
        _outputPath = outputPath;
        _writer = new MediaFoundationHevcWriter(width, height, frameRate, outputPath, device, settings: settings);
        DebugTrace.WriteLine(
            "RealtimeRgbNvencWriter",
            $"create path={_outputPath} device={(device is not null ? "gpu" : "cpu")}");
        if (device is not null)
        {
            _deviceContext = device.ImmediateContext;
            _multithread = _deviceContext.QueryInterfaceOrNull<ID3D11Multithread>();
            _multithread?.SetMultithreadProtected(true);
            InitializeGpuCopySlots(device, width, height);
        }

        _channel = Channel.CreateBounded<PendingFrame>(new BoundedChannelOptions(Math.Max(queueCapacity, 1))
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait
        });

        _writeTask = Task.Factory.StartNew(
            static state =>
            {
                var writer = (RealtimeRgbNvencWriter)state!;
                using var schedulingScope = WindowsScheduling.EnterWriterProfile();
                writer.WriteLoopAsync().GetAwaiter().GetResult();
            },
            this,
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
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
        _channel.Writer.WriteAsync(PendingFrame.ForCpu(pixelData, repeatCount)).AsTask().GetAwaiter().GetResult();
    }

    public void QueueFrame(GpuTextureFrame gpuFrame, int repeatCount)
    {
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

        ThrowIfWriteFailed();
        var copySlot = AcquireGpuCopySlot();
        try
        {
            CopyGpuFrame(gpuFrame.Texture, copySlot.Texture);
        }
        catch
        {
            ReleaseGpuCopySlot(copySlot.Index);
            gpuFrame.Dispose();
            throw;
        }

        gpuFrame.Dispose();
        _channel.Writer.WriteAsync(PendingFrame.ForGpu(copySlot, repeatCount, ReleaseGpuCopySlot)).AsTask().GetAwaiter().GetResult();
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

        _ = cancellationToken;
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
    }

    private async Task WriteLoopAsync()
    {
        await foreach (var pending in _channel.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                if (pending.GpuFrame is not null)
                {
                    _writer.WriteTextureFrame(pending.GpuFrame.Texture, pending.RepeatCount);
                }
                else if (pending.PixelData is not null)
                {
                    _writer.WriteFrame(pending.PixelData.Buffer, pending.PixelData.Length, pending.RepeatCount);
                }
                else
                {
                    throw new InvalidOperationException("RGB writer pending frame payload が存在しません。");
                }
            }
            catch (Exception ex)
            {
                DebugTrace.WriteLine(
                    "RealtimeRgbNvencWriter",
                    $"write failed path={_outputPath} gpu={pending.GpuFrame is not null} cpu={pending.PixelData is not null} repeat={pending.RepeatCount} error={ex.GetType().Name}: {ex.Message}");
                throw;
            }
            finally
            {
                pending.Dispose();
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

        if (_multithread is not null)
        {
            _multithread.Enter();
            try
            {
                _deviceContext.CopyResource(destinationTexture, sourceTexture);
                _deviceContext.Flush();
            }
            finally
            {
                _multithread.Leave();
            }

            return;
        }

        _deviceContext.CopyResource(destinationTexture, sourceTexture);
        _deviceContext.Flush();
    }

    private sealed class PendingFrame : IDisposable
    {
        private readonly Action<int>? _releaseGpuSlot;

        private PendingFrame(PixelBufferLease? pixelData, GpuCopySlot? gpuFrame, int repeatCount, Action<int>? releaseGpuSlot)
        {
            PixelData = pixelData;
            GpuFrame = gpuFrame;
            RepeatCount = repeatCount;
            _releaseGpuSlot = releaseGpuSlot;
        }

        public PixelBufferLease? PixelData { get; }

        public GpuCopySlot? GpuFrame { get; }

        public int RepeatCount { get; }

        public static PendingFrame ForCpu(PixelBufferLease pixelData, int repeatCount)
            => new(pixelData, null, repeatCount, null);

        public static PendingFrame ForGpu(GpuCopySlot gpuFrame, int repeatCount, Action<int> releaseGpuSlot)
            => new(null, gpuFrame, repeatCount, releaseGpuSlot);

        public void Dispose()
        {
            PixelData?.Dispose();
            if (GpuFrame is not null)
            {
                _releaseGpuSlot?.Invoke(GpuFrame.Index);
            }
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
