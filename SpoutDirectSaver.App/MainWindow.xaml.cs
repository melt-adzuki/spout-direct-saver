using System;
using System.Buffers;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using MahApps.Metro.IconPacks;
using Microsoft.Win32;
using VLCMediaPlayer = LibVLCSharp.Shared.MediaPlayer;
using SpoutDirectSaver.App.Models;
using SpoutDirectSaver.App.Services;

namespace SpoutDirectSaver.App;

public partial class MainWindow : Window
{
    private enum UiMode
    {
        NoSignal,
        Receiving,
        Recording,
        RecordingPaused,
        PlaybackPlaying,
        PlaybackPaused,
        Encoding,
        Encoded
    }

    private readonly SpoutPollingService _spoutPollingService = new();
    private readonly VideoExportService _videoExportService = new();
    private readonly EncoderSettingsStore _encoderSettingsStore = new();
    private readonly DispatcherTimer _recordingTimer;
    private readonly EncoderOption[] _encoderOptions = EncoderOption.CreateDefaults();
    private readonly VLCMediaPlayer.LibVLCVideoLockCb _playbackVideoLockCallback;
    private readonly VLCMediaPlayer.LibVLCVideoUnlockCb _playbackVideoUnlockCallback;
    private readonly VLCMediaPlayer.LibVLCVideoDisplayCb _playbackVideoDisplayCallback;

    private LibVLC? _libVlc;
    private VLCMediaPlayer? _mediaPlayer;
    private Media? _previewMedia;
    private WriteableBitmap? _liveBitmap;
    private WriteableBitmap? _playbackBitmap;
    private RecordingSession? _recordingSession;
    private CapturedTake? _capturedTake;
    private string? _pendingTakeDirectory;
    private string? _pendingPreviewPath;
    private EncoderSettingsRoot _encoderSettings = EncoderSettingsRoot.CreateDefaults();
    private EncoderOption _selectedEncoderOption = null!;
    private CaptureStatus? _latestStatus;
    private UiMode _uiMode = UiMode.NoSignal;
    private DateTimeOffset? _recordingStartedAt;
    private DateTimeOffset? _recordingPausedAt;
    private TimeSpan _recordingPausedAccumulated = TimeSpan.Zero;
    private long _lastRenderedPreviewTicks;
    private int _encodingProgressPercent;
    private CancellationTokenSource? _encodeCancellationSource;
    private Task? _encodeTask;
    private GCLatencyMode? _recordingLatencyModeRestore;
    private bool _isClosing;
    private string? _lastExportPath;
    private long _playbackPositionMilliseconds;
    private long _playbackDurationMilliseconds;
    private bool _playbackReachedEnd;
    private IntPtr _playbackBuffer = IntPtr.Zero;
    private IntPtr _playbackBufferRaw = IntPtr.Zero;
    private int _playbackBufferSize;
    private int _playbackBufferPitch;
    private int _playbackBufferWidth;
    private int _playbackBufferHeight;
    private readonly object _playbackSurfaceGate = new();
    private readonly object _playbackRenderGate = new();
    private PlaybackFrameBuffer? _pendingPlaybackFrame;
    private bool _playbackRenderQueued;

    private readonly record struct PlaybackFrameBuffer(byte[] Buffer, int Width, int Height, int Pitch, int Size);

    public MainWindow()
    {
        InitializeComponent();

        Core.Initialize();

        _encoderSettings = _encoderSettingsStore.Load();
        _selectedEncoderOption = ResolveEncoderOption(_encoderSettings.SelectedEncoderKind);

        _libVlc = new LibVLC("--no-video-title-show");
        _mediaPlayer = new VLCMediaPlayer(_libVlc);
        _playbackVideoLockCallback = PlaybackVideo_OnLock;
        _playbackVideoUnlockCallback = PlaybackVideo_OnUnlock;
        _playbackVideoDisplayCallback = PlaybackVideo_OnDisplay;
        _mediaPlayer.TimeChanged += MediaPlayer_OnTimeChanged;
        _mediaPlayer.LengthChanged += MediaPlayer_OnLengthChanged;
        _mediaPlayer.EndReached += MediaPlayer_OnEndReached;
        _mediaPlayer.SetVideoCallbacks(_playbackVideoLockCallback, _playbackVideoUnlockCallback, _playbackVideoDisplayCallback);

        BuildButtonContent();

        _recordingTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(100),
            DispatcherPriority.Background,
            (_, _) => UpdateRecordingElapsed(),
            Dispatcher)
        {
            IsEnabled = false
        };

        _spoutPollingService.StatusChanged += SpoutPollingService_OnStatusChanged;
        CompositionTarget.Rendering += CompositionTarget_OnRendering;

        Loaded += MainWindow_OnLoaded;
        UpdateUiState();
    }

    private async void MainWindow_OnLoaded(object sender, RoutedEventArgs e)
    {
        await _spoutPollingService.StartAsync();
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        if (_isClosing)
        {
            base.OnClosing(e);
            return;
        }

        _isClosing = true;

        try
        {
            _recordingTimer.Stop();
            _spoutPollingService.FrameArrived -= SpoutPollingService_OnFrameArrived;
            _spoutPollingService.StatusChanged -= SpoutPollingService_OnStatusChanged;
            CompositionTarget.Rendering -= CompositionTarget_OnRendering;

            _encodeCancellationSource?.Cancel();
            if (_encodeTask is not null)
            {
                try
                {
                    await _encodeTask.ConfigureAwait(true);
                }
                catch
                {
                    // Best-effort shutdown only.
                }
            }

            await AbortRecordingAsync(showError: false).ConfigureAwait(true);
            await DisposeCapturedTakeAsync().ConfigureAwait(true);
        }
        finally
        {
            EndRecordingLatencyScope();
            _encoderSettings.SelectedEncoderKind = _selectedEncoderOption.Kind;
            _encoderSettingsStore.Save(_encoderSettings);

            StopPreviewPlayback();
            await _spoutPollingService.DisposeAsync().ConfigureAwait(true);

            if (_mediaPlayer is not null)
            {
                _mediaPlayer.TimeChanged -= MediaPlayer_OnTimeChanged;
                _mediaPlayer.LengthChanged -= MediaPlayer_OnLengthChanged;
                _mediaPlayer.EndReached -= MediaPlayer_OnEndReached;
                _mediaPlayer.Dispose();
            }

            _libVlc?.Dispose();
            base.OnClosing(e);
        }
    }

    private void RecordButton_OnClick(object sender, RoutedEventArgs e) => _ = StartRecordingAsync();

    private void SettingsButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_uiMode is not UiMode.NoSignal and not UiMode.Receiving)
        {
            return;
        }

        var dialog = new EncoderSettingsDialog(_encoderOptions, _selectedEncoderOption, _encoderSettings)
        {
            Owner = this
        };

        if (dialog.ShowDialog() == true)
        {
            _selectedEncoderOption = dialog.SelectedEncoderOption;
            _encoderSettings = dialog.Settings;
            _encoderSettings.SelectedEncoderKind = _selectedEncoderOption.Kind;
            _encoderSettingsStore.Save(_encoderSettings);
            UpdateUiState();
        }
    }

    private void DoneButton_OnClick(object sender, RoutedEventArgs e) => _ = StopRecordingAsync();

    private void PauseResumeButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_uiMode == UiMode.Recording)
        {
            PauseRecording();
        }
        else if (_uiMode == UiMode.RecordingPaused)
        {
            ResumeRecording();
        }
    }

    private void RemoveButton_OnClick(object sender, RoutedEventArgs e) => _ = RemoveCapturedTakeAsync();

    private async void BackwardButton_OnClick(object sender, RoutedEventArgs e)
    {
        await StepPlaybackAsync(-5000).ConfigureAwait(true);
    }

    private async void PlayPauseButton_OnClick(object sender, RoutedEventArgs e)
    {
        await TogglePlaybackAsync().ConfigureAwait(true);
    }

    private async void ForwardButton_OnClick(object sender, RoutedEventArgs e)
    {
        await StepPlaybackAsync(5000).ConfigureAwait(true);
    }

    private void EncodeButton_OnClick(object sender, RoutedEventArgs e) => _ = BeginEncodingAsync();

    private async void CancelButton_OnClick(object sender, RoutedEventArgs e)
    {
        await CancelEncodingAsync().ConfigureAwait(true);
    }

    private void ContinueButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_capturedTake is null)
        {
            return;
        }

        _uiMode = UiMode.PlaybackPaused;
        UpdateUiState();
    }

    private void SpoutPollingService_OnStatusChanged(object? sender, CaptureStatus status)
    {
        _latestStatus = status;

        _ = Dispatcher.BeginInvoke(() =>
        {
            if (_uiMode is UiMode.NoSignal or UiMode.Receiving)
            {
                _uiMode = status.IsConnected ? UiMode.Receiving : UiMode.NoSignal;
            }

            UpdateUiState();
        });
    }

    private void SpoutPollingService_OnFrameArrived(object? sender, FramePacket frame)
    {
        try
        {
            if (_recordingSession is null)
            {
                frame.Dispose();
                return;
            }

            _recordingSession.AppendFrame(frame);
        }
        catch (Exception ex)
        {
            frame.Dispose();
            _ = Dispatcher.BeginInvoke(async () => await AbortRecordingAsync(showError: true, errorMessage: ex.Message).ConfigureAwait(true));
        }
    }

    private void CompositionTarget_OnRendering(object? sender, EventArgs e)
    {
        if (_uiMode != UiMode.Receiving)
        {
            return;
        }

        var rendered = _spoutPollingService.TryReadLatestPreviewFrame(frame =>
        {
            if (frame.StopwatchTicks == _lastRenderedPreviewTicks)
            {
                return;
            }

            RenderLivePreview(frame);
            _lastRenderedPreviewTicks = frame.StopwatchTicks;
        });

        if (!rendered)
        {
            UpdateStageVisibility();
        }
    }

    private async Task StartRecordingAsync()
    {
        if (_recordingSession is not null || _capturedTake is not null || _uiMode == UiMode.Encoding)
        {
            return;
        }

        if (_latestStatus?.IsConnected != true)
        {
            return;
        }

        try
        {
            await DisposeCapturedTakeAsync().ConfigureAwait(true);

            _pendingTakeDirectory = BuildTakeDirectory();
            Directory.CreateDirectory(_pendingTakeDirectory);
            _pendingPreviewPath = Path.Combine(_pendingTakeDirectory, $"preview{_selectedEncoderOption.Extension}");

            _encoderSettings.SelectedEncoderKind = _selectedEncoderOption.Kind;
            _encoderSettingsStore.Save(_encoderSettings);

            _recordingSession = new RecordingSession(_selectedEncoderOption, _pendingPreviewPath, _encoderSettings);
            _recordingStartedAt = DateTimeOffset.UtcNow;
            _recordingPausedAt = null;
            _recordingPausedAccumulated = TimeSpan.Zero;
            _lastRenderedPreviewTicks = 0;
            _uiMode = UiMode.Recording;

            BeginRecordingLatencyScope();
            SetRecordingInputActive(true);
            _recordingTimer.Start();

            UpdateUiState();
        }
        catch (Exception ex)
        {
            await AbortRecordingAsync(showError: true, errorMessage: ex.Message).ConfigureAwait(true);
        }
    }

    private async Task StopRecordingAsync()
    {
        if (_recordingSession is null)
        {
            return;
        }

        var session = _recordingSession;
        var takeDirectory = _pendingTakeDirectory;
        var previewPath = _pendingPreviewPath;
        _recordingSession = null;

        _recordingTimer.Stop();
        SetRecordingInputActive(false);

        try
        {
            if (takeDirectory is null || previewPath is null)
            {
                throw new InvalidOperationException("録画の一時保存先が初期化されていません。");
            }

            _encodingProgressPercent = 0;
            _uiMode = UiMode.Encoding;
            UpdateUiState();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);

            _encodeCancellationSource?.Dispose();
            _encodeCancellationSource = new CancellationTokenSource();
            var progress = new Progress<EncodeProgress>(HandleEncodeProgress);
            var finalizeTask = session.FinalizeAsync(
                _videoExportService,
                progress,
                _encodeCancellationSource.Token);
            _encodeTask = finalizeTask;

            var finalizedPreviewPath = await finalizeTask.ConfigureAwait(true);
            var sidecarPath = BuildAlphaSidecarPath(finalizedPreviewPath);
            if (!File.Exists(sidecarPath))
            {
                sidecarPath = null;
            }

            _capturedTake = new CapturedTake(
                _selectedEncoderOption,
                _encoderSettings,
                takeDirectory,
                finalizedPreviewPath,
                sidecarPath,
                session.RecordedWidth,
                session.RecordedHeight);

            _pendingTakeDirectory = null;
            _pendingPreviewPath = null;
            _lastExportPath = null;
            _uiMode = UiMode.PlaybackPaused;
            UpdateUiState();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
            await LoadCapturedTakePreviewAsync(finalizedPreviewPath, startPlaying: false).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            _uiMode = _latestStatus?.IsConnected == true ? UiMode.Receiving : UiMode.NoSignal;
            UpdateUiState();
        }
        catch (Exception ex)
        {
            _uiMode = _latestStatus?.IsConnected == true ? UiMode.Receiving : UiMode.NoSignal;
            UpdateUiState();
            MessageBox.Show(this, ex.Message, "Spout Direct Saver", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _encodeTask = null;
            _encodeCancellationSource?.Dispose();
            _encodeCancellationSource = null;
            await TryDisposeSessionAsync(session).ConfigureAwait(true);
            _pendingTakeDirectory = null;
            _pendingPreviewPath = null;
            ClearRecordingState();
            EndRecordingLatencyScope();
            UpdateUiState();
        }
    }

    private async Task AbortRecordingAsync(bool showError, string? errorMessage = null)
    {
        var session = _recordingSession;
        if (session is not null)
        {
            _recordingSession = null;
            _recordingTimer.Stop();
            SetRecordingInputActive(false);
            ClearRecordingState();

            try
            {
                await session.DisposeAsync().ConfigureAwait(true);
            }
            catch
            {
                // Cleanup is best-effort.
            }
        }

        if (!string.IsNullOrWhiteSpace(_pendingTakeDirectory))
        {
            TryDeleteDirectory(_pendingTakeDirectory);
        }

        _pendingTakeDirectory = null;
        _pendingPreviewPath = null;
        EndRecordingLatencyScope();

        if (showError && !string.IsNullOrWhiteSpace(errorMessage))
        {
            MessageBox.Show(this, errorMessage, "Spout Direct Saver", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        if (_capturedTake is null)
        {
            _uiMode = _latestStatus?.IsConnected == true ? UiMode.Receiving : UiMode.NoSignal;
        }

        UpdateUiState();
    }

    private void PauseRecording()
    {
        if (_recordingSession is null || _uiMode != UiMode.Recording)
        {
            return;
        }

        _recordingSession.Pause();
        _recordingPausedAt = DateTimeOffset.UtcNow;
        _recordingTimer.Stop();
        SetRecordingInputActive(false);
        _uiMode = UiMode.RecordingPaused;
        UpdateUiState();
    }

    private void ResumeRecording()
    {
        if (_recordingSession is null || _uiMode != UiMode.RecordingPaused || _recordingPausedAt is null)
        {
            return;
        }

        _recordingSession.Resume();
        _recordingPausedAccumulated += DateTimeOffset.UtcNow - _recordingPausedAt.Value;
        _recordingPausedAt = null;
        SetRecordingInputActive(true);
        _recordingTimer.Start();
        _uiMode = UiMode.Recording;
        UpdateUiState();
    }

    private async Task RemoveCapturedTakeAsync()
    {
        if (_capturedTake is null || _uiMode is UiMode.Encoding)
        {
            return;
        }

        StopPreviewPlayback();
        await DisposeCapturedTakeAsync().ConfigureAwait(true);
        _uiMode = _latestStatus?.IsConnected == true ? UiMode.Receiving : UiMode.NoSignal;
        UpdateUiState();
    }

    private async Task BeginEncodingAsync(bool launchFromRecording = false)
    {
        if (_capturedTake is null || _uiMode is UiMode.Encoding)
        {
            return;
        }

        if (!launchFromRecording)
        {
            PausePlayback();
        }

        var dialog = new SaveFileDialog
        {
            Title = "Save Recording",
            Filter = _capturedTake.EncoderOption.FileDialogFilter,
            DefaultExt = _capturedTake.EncoderOption.Extension,
            AddExtension = true,
            OverwritePrompt = true,
            FileName = BuildSuggestedOutputName(_capturedTake.EncoderOption)
        };

        if (!string.IsNullOrWhiteSpace(_lastExportPath))
        {
            dialog.InitialDirectory = Path.GetDirectoryName(_lastExportPath);
            dialog.FileName = Path.GetFileName(_lastExportPath);
        }

        var result = dialog.ShowDialog(this);
        if (result != true)
        {
            _uiMode = UiMode.PlaybackPaused;
            UpdateUiState();
            return;
        }

        _lastExportPath = dialog.FileName;
        _encodingProgressPercent = 0;
        _uiMode = UiMode.Encoding;
        UpdateUiState();
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);

        _encodeCancellationSource?.Dispose();
        _encodeCancellationSource = new CancellationTokenSource();
        var progress = new Progress<EncodeProgress>(HandleEncodeProgress);

        _encodeTask = EncodeCapturedTakeAsync(dialog.FileName, progress, _encodeCancellationSource.Token);

        try
        {
            await _encodeTask.ConfigureAwait(true);
            await DisposeCapturedTakeAsync().ConfigureAwait(true);
            _uiMode = _latestStatus?.IsConnected == true ? UiMode.Receiving : UiMode.NoSignal;
        }
        catch (OperationCanceledException)
        {
            _uiMode = UiMode.PlaybackPaused;
        }
        catch (Exception ex)
        {
            _uiMode = UiMode.PlaybackPaused;
            MessageBox.Show(this, ex.Message, "Spout Direct Saver", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _encodeTask = null;
            _encodeCancellationSource?.Dispose();
            _encodeCancellationSource = null;
            UpdateUiState();
        }
    }

    private async Task CancelEncodingAsync()
    {
        if (_uiMode != UiMode.Encoding)
        {
            return;
        }

        _encodeCancellationSource?.Cancel();
        if (_encodeTask is not null)
        {
            try
            {
                await _encodeTask.ConfigureAwait(true);
            }
            catch
            {
                // The encode task handles its own terminal state.
            }
        }
    }

    private async Task EncodeCapturedTakeAsync(
        string outputPath,
        IProgress<EncodeProgress> progress,
        CancellationToken cancellationToken)
    {
        if (_capturedTake is null)
        {
            throw new InvalidOperationException("録画済みの take がありません。");
        }

        await _videoExportService.ExportCapturedTakeAsync(
            _capturedTake,
            outputPath,
            progress,
            cancellationToken).ConfigureAwait(true);
    }

    private async Task TogglePlaybackAsync()
    {
        if (_mediaPlayer is null || _capturedTake is null)
        {
            return;
        }

        if (_mediaPlayer.IsPlaying)
        {
            PausePlayback();
        }
        else
        {
            if (_playbackReachedEnd || IsPlaybackAtEnd())
            {
                await LoadCapturedTakePreviewAsync(_capturedTake.PreviewVideoPath, startPlaying: true).ConfigureAwait(true);
                return;
            }

            _playbackReachedEnd = false;
            _mediaPlayer.Play();
            _uiMode = UiMode.PlaybackPlaying;
            UpdateUiState();
        }
    }

    private void PausePlayback()
    {
        if (_mediaPlayer is null || _capturedTake is null)
        {
            return;
        }

        _playbackPositionMilliseconds = GetPlaybackPositionMilliseconds();
        _playbackReachedEnd = false;
        if (_mediaPlayer.IsPlaying)
        {
            _mediaPlayer.Pause();
        }

        _uiMode = UiMode.PlaybackPaused;
        UpdateUiState();
    }

    private async Task StepPlaybackAsync(int deltaMilliseconds)
    {
        if (_mediaPlayer is null || _capturedTake is null)
        {
            return;
        }

        var current = _playbackReachedEnd
            ? GetPlaybackLengthMilliseconds()
            : GetPlaybackPositionMilliseconds();
        var length = GetPlaybackLengthMilliseconds();
        if (length <= 0)
        {
            return;
        }

        var target = Math.Clamp(current + deltaMilliseconds, 0, length);
        await SeekPlaybackToAsync(target, shouldPlay: _uiMode == UiMode.PlaybackPlaying).ConfigureAwait(true);
    }

    private async Task LoadCapturedTakePreviewAsync(string previewPath)
    {
        await LoadCapturedTakePreviewAsync(previewPath, startPlaying: false).ConfigureAwait(true);
    }

    private async Task LoadCapturedTakePreviewAsync(string previewPath, bool startPlaying)
    {
        if (_mediaPlayer is null || _libVlc is null || _capturedTake is null)
        {
            return;
        }

        StopPreviewPlayback();
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);

        _previewMedia = new Media(_libVlc, new Uri(previewPath, UriKind.Absolute));
        await _previewMedia.Parse(MediaParseOptions.ParseLocal, 3000, CancellationToken.None).ConfigureAwait(true);
        ConfigurePlaybackSurface(_capturedTake.VideoWidth, _capturedTake.VideoHeight);
        _playbackPositionMilliseconds = 0;
        _playbackDurationMilliseconds = 0;
        _playbackReachedEnd = false;
        _mediaPlayer.Play(_previewMedia);

        if (!startPlaying)
        {
            await Task.Delay(50).ConfigureAwait(true);
            _mediaPlayer.Pause();
            _mediaPlayer.SeekTo(TimeSpan.Zero);
            _playbackPositionMilliseconds = 0;
            _uiMode = UiMode.PlaybackPaused;
        }
        else
        {
            _uiMode = UiMode.PlaybackPlaying;
        }

        _playbackDurationMilliseconds = Math.Max(_previewMedia.Duration, 0);
        UpdateUiState();
    }

    private void StopPreviewPlayback()
    {
        if (_mediaPlayer is not null)
        {
            _mediaPlayer.Stop();
        }

        _previewMedia?.Dispose();
        _previewMedia = null;
        DisposePlaybackSurface();
        _playbackPositionMilliseconds = 0;
        _playbackDurationMilliseconds = 0;
        _playbackReachedEnd = false;
    }

    private void RenderLivePreview(LivePreviewFrame frame)
    {
        var width = (int)frame.Width;
        var height = (int)frame.Height;
        var stride = width * 4;

        if (_liveBitmap is null || _liveBitmap.PixelWidth != width || _liveBitmap.PixelHeight != height)
        {
            _liveBitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
            LivePreviewImage.Source = _liveBitmap;
        }

        _liveBitmap.WritePixels(new Int32Rect(0, 0, width, height), frame.PixelData, stride * height, stride);
        LivePreviewImage.Visibility = Visibility.Visible;
        NoSignalOverlayTextBlock.Visibility = Visibility.Collapsed;
    }

    private void ConfigurePlaybackSurface(uint width, uint height)
    {
        if (_mediaPlayer is null || width == 0 || height == 0)
        {
            return;
        }

        lock (_playbackSurfaceGate)
        {
            _playbackBufferWidth = (int)width;
            _playbackBufferHeight = (int)height;
            _playbackBufferPitch = checked((int)width * 4);
            _playbackBufferSize = checked(_playbackBufferPitch * _playbackBufferHeight);

            if (_playbackBufferRaw != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_playbackBufferRaw);
                _playbackBufferRaw = IntPtr.Zero;
            }

            var allocationSize = _playbackBufferSize + 32;
            _playbackBufferRaw = Marshal.AllocHGlobal(allocationSize);
            _playbackBuffer = AlignPointer(_playbackBufferRaw, 32);

            _playbackBitmap = new WriteableBitmap(_playbackBufferWidth, _playbackBufferHeight, 96, 96, PixelFormats.Bgra32, null);
            PlaybackImage.Source = _playbackBitmap;

            _mediaPlayer.SetVideoFormat("RV32", width, height, (uint)_playbackBufferPitch);
        }
    }

    private void DisposePlaybackSurface()
    {
        lock (_playbackSurfaceGate)
        {
            PlaybackImage.Source = null;
            _playbackBitmap = null;
            _playbackBufferWidth = 0;
            _playbackBufferHeight = 0;
            _playbackBufferPitch = 0;
            _playbackBufferSize = 0;
            _playbackBuffer = IntPtr.Zero;

            if (_playbackBufferRaw != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_playbackBufferRaw);
                _playbackBufferRaw = IntPtr.Zero;
            }
        }

        lock (_playbackRenderGate)
        {
            if (_pendingPlaybackFrame is not null)
            {
                ArrayPool<byte>.Shared.Return(_pendingPlaybackFrame.Value.Buffer);
                _pendingPlaybackFrame = null;
            }

            _playbackRenderQueued = false;
        }
    }

    private IntPtr PlaybackVideo_OnLock(IntPtr opaque, IntPtr planes)
    {
        lock (_playbackSurfaceGate)
        {
            if (_playbackBuffer == IntPtr.Zero || planes == IntPtr.Zero)
            {
                return IntPtr.Zero;
            }

            Marshal.WriteIntPtr(planes, _playbackBuffer);
            return _playbackBuffer;
        }
    }

    private void PlaybackVideo_OnUnlock(IntPtr opaque, IntPtr picture, IntPtr planes)
    {
        // No-op. The UI thread copies the buffer during the display callback.
    }

    private void PlaybackVideo_OnDisplay(IntPtr opaque, IntPtr picture)
    {
        byte[]? frameCopy = null;
        var frameWidth = 0;
        var frameHeight = 0;
        var framePitch = 0;
        var frameSize = 0;

        lock (_playbackSurfaceGate)
        {
            if (_playbackBitmap is null || _playbackBuffer == IntPtr.Zero || _playbackBufferSize <= 0)
            {
                return;
            }

            frameCopy = ArrayPool<byte>.Shared.Rent(_playbackBufferSize);
            Marshal.Copy(_playbackBuffer, frameCopy, 0, _playbackBufferSize);
            frameWidth = _playbackBufferWidth;
            frameHeight = _playbackBufferHeight;
            framePitch = _playbackBufferPitch;
            frameSize = _playbackBufferSize;
        }

        lock (_playbackRenderGate)
        {
            if (_pendingPlaybackFrame is not null)
            {
                ArrayPool<byte>.Shared.Return(_pendingPlaybackFrame.Value.Buffer);
            }

            _pendingPlaybackFrame = new PlaybackFrameBuffer(frameCopy!, frameWidth, frameHeight, framePitch, frameSize);
            if (_playbackRenderQueued)
            {
                return;
            }

            _playbackRenderQueued = true;
        }

        Dispatcher.BeginInvoke(ProcessPlaybackRenderQueue, DispatcherPriority.Render);
    }

    private void ProcessPlaybackRenderQueue()
    {
        while (true)
        {
            PlaybackFrameBuffer? frame;

            lock (_playbackRenderGate)
            {
                frame = _pendingPlaybackFrame;
                _pendingPlaybackFrame = null;
                if (frame is null)
                {
                    _playbackRenderQueued = false;
                    return;
                }
            }

            try
            {
                var bitmap = _playbackBitmap;
                if (bitmap is not null && frame.Value.Size > 0)
                {
                    var rect = new Int32Rect(0, 0, frame.Value.Width, frame.Value.Height);
                    bitmap.WritePixels(rect, frame.Value.Buffer, frame.Value.Pitch, 0);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(frame.Value.Buffer);
            }
        }
    }

    private void MediaPlayer_OnTimeChanged(object? sender, MediaPlayerTimeChangedEventArgs e)
    {
        if (_uiMode is not UiMode.PlaybackPlaying and not UiMode.PlaybackPaused)
        {
            return;
        }

        _playbackPositionMilliseconds = Math.Max(e.Time, 0);
        if (_playbackDurationMilliseconds > 0 && _playbackPositionMilliseconds < _playbackDurationMilliseconds)
        {
            _playbackReachedEnd = false;
        }

        _ = Dispatcher.BeginInvoke(UpdateStateMetrics);
    }

    private void MediaPlayer_OnLengthChanged(object? sender, MediaPlayerLengthChangedEventArgs e)
    {
        if (_uiMode is not UiMode.PlaybackPlaying and not UiMode.PlaybackPaused)
        {
            return;
        }

        _playbackDurationMilliseconds = Math.Max(e.Length, 0);
        _ = Dispatcher.BeginInvoke(UpdateStateMetrics);
    }

    private void MediaPlayer_OnEndReached(object? sender, EventArgs e)
    {
        if (_capturedTake is null)
        {
            return;
        }

        _ = Dispatcher.BeginInvoke(() =>
        {
            _playbackPositionMilliseconds = _playbackDurationMilliseconds;
            _playbackReachedEnd = true;
            _uiMode = UiMode.PlaybackPaused;
            UpdateUiState();
        });
    }

    private void BuildButtonContent()
    {
        RecordButton.Content = CreateButtonContent(CreateIcon(PackIconBootstrapIconsKind.CameraVideoFill), "Record");
        SettingsButton.Content = CreateButtonContent(CreateIcon(PackIconBootstrapIconsKind.GearFill), "Settings");
        DoneButton.Content = CreateButtonContent(CreateIcon(PackIconBootstrapIconsKind.CheckLg), "Done");
        PauseResumeButton.Content = CreateButtonContent(CreateIcon(PackIconBootstrapIconsKind.PauseFill), "Pause");
        RemoveButton.Content = CreateButtonContent(CreateIcon(PackIconBootstrapIconsKind.Trash3Fill), "Remove");
        BackwardButton.Content = CreateButtonContent(CreateCounterArrowIcon(PackIconBootstrapIconsKind.ArrowCounterclockwise, "5"), "Backward");
        PlayPauseButton.Content = CreateButtonContent(CreateIcon(PackIconBootstrapIconsKind.PlayFill), "Play");
        ForwardButton.Content = CreateButtonContent(CreateCounterArrowIcon(PackIconBootstrapIconsKind.ArrowClockwise, "5"), "Forward");
        EncodeButton.Content = CreateButtonContent(CreateIcon(PackIconBootstrapIconsKind.FloppyFill), "Save");
        CancelButton.Content = CreateButtonContent(CreateIcon(PackIconBootstrapIconsKind.XLg), "Cancel");
        ContinueButton.Content = CreateButtonContent(CreateIcon(PackIconBootstrapIconsKind.CheckLg), "Continue");
    }

    private static UIElement CreateButtonContent(UIElement icon, string label)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Vertical,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        panel.Children.Add(icon);

        panel.Children.Add(new TextBlock
        {
            Text = label,
            Margin = new Thickness(0, 4, 0, 0),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center
        });

        return panel;
    }

    private void UpdateUiState()
    {
        var isInitial = _uiMode is UiMode.NoSignal or UiMode.Receiving;
        InitialMetricsPanel.Visibility = isInitial ? Visibility.Visible : Visibility.Collapsed;
        StateMetricsPanel.Visibility = isInitial ? Visibility.Collapsed : Visibility.Visible;

        InitialActionsPanel.Visibility = isInitial ? Visibility.Visible : Visibility.Collapsed;
        RecordingActionsPanel.Visibility = _uiMode is UiMode.Recording or UiMode.RecordingPaused
            ? Visibility.Visible
            : Visibility.Collapsed;
        PlaybackActionsPanel.Visibility = _uiMode is UiMode.PlaybackPlaying or UiMode.PlaybackPaused
            ? Visibility.Visible
            : Visibility.Collapsed;
        EncodingActionsPanel.Visibility = _uiMode == UiMode.Encoding
            ? Visibility.Visible
            : Visibility.Collapsed;
        EncodedActionsPanel.Visibility = Visibility.Collapsed;

        var connected = _latestStatus?.IsConnected == true;
        RecordButton.IsEnabled = isInitial && connected && _capturedTake is null && _recordingSession is null;
        SettingsButton.IsEnabled = isInitial;

        DoneButton.IsEnabled = _uiMode is UiMode.Recording or UiMode.RecordingPaused;
        PauseResumeButton.IsEnabled = DoneButton.IsEnabled;
        RemoveButton.IsEnabled = _capturedTake is not null && _uiMode is UiMode.PlaybackPlaying or UiMode.PlaybackPaused;
        BackwardButton.IsEnabled = RemoveButton.IsEnabled;
        PlayPauseButton.IsEnabled = RemoveButton.IsEnabled;
        ForwardButton.IsEnabled = RemoveButton.IsEnabled;
        EncodeButton.IsEnabled = RemoveButton.IsEnabled && _uiMode != UiMode.Encoding;
        CancelButton.IsEnabled = _uiMode == UiMode.Encoding;
        ContinueButton.IsEnabled = false;

        PauseResumeButton.Content = CreateButtonContent(
            CreateIcon(_uiMode == UiMode.RecordingPaused
                ? PackIconBootstrapIconsKind.PlayFill
                : PackIconBootstrapIconsKind.PauseFill),
            _uiMode == UiMode.RecordingPaused ? "Resume" : "Pause");

        PlayPauseButton.Content = CreateButtonContent(
            CreateIcon(_uiMode == UiMode.PlaybackPlaying
                ? PackIconBootstrapIconsKind.PauseFill
                : PackIconBootstrapIconsKind.PlayFill),
            _uiMode == UiMode.PlaybackPlaying ? "Pause" : "Play");

        UpdateInitialMetrics();
        UpdateStateMetrics();
        UpdateStageVisibility();
        UpdateEncodingProgressVisual();
        _recordingTimer.IsEnabled = _uiMode is UiMode.Recording or UiMode.RecordingPaused or UiMode.PlaybackPlaying or UiMode.PlaybackPaused;
    }

    private void UpdateInitialMetrics()
    {
        if (_uiMode is not UiMode.NoSignal and not UiMode.Receiving)
        {
            return;
        }

        if (_latestStatus is null || !_latestStatus.IsConnected)
        {
            ResolutionValueTextBlock.Text = "---";
            FrameRateValueTextBlock.Text = "---";
            SenderValueTextBlock.Text = "---";
            return;
        }

        ResolutionValueTextBlock.Text = _latestStatus.Width > 0 && _latestStatus.Height > 0
            ? $"{_latestStatus.Width}x{_latestStatus.Height}"
            : "---";
        FrameRateValueTextBlock.Text = _latestStatus.SenderFps > 0
            ? _latestStatus.SenderFps.ToString("0.##", CultureInfo.InvariantCulture)
            : "---";
        SenderValueTextBlock.Text = string.IsNullOrWhiteSpace(_latestStatus.SenderName)
            ? "---"
            : _latestStatus.SenderName;
    }

    private void UpdateStateMetrics()
    {
        if (StateMetricsPanel.Visibility != Visibility.Visible)
        {
            return;
        }

        switch (_uiMode)
        {
            case UiMode.Recording:
            case UiMode.RecordingPaused:
                StateValueTextBlock.Text = _uiMode == UiMode.Recording ? "Recording" : "Paused";
                SecondaryLabelTextBlock.Text = "Duration";
                SecondaryValueTextBlock.Text = FormatTime(GetRecordingElapsed());
                break;
            case UiMode.PlaybackPlaying:
            case UiMode.PlaybackPaused:
                StateValueTextBlock.Text = "Playback";
                SecondaryLabelTextBlock.Text = "Duration";
                SecondaryValueTextBlock.Text = FormatTime(TimeSpan.FromMilliseconds(GetPlaybackPositionMilliseconds()));
                break;
            case UiMode.Encoding:
            case UiMode.Encoded:
                StateValueTextBlock.Text = _uiMode == UiMode.Encoding ? "Encoding" : "Encoded";
                SecondaryLabelTextBlock.Text = "Progress";
                SecondaryValueTextBlock.Text = $"{_encodingProgressPercent}%";
                break;
        }
    }

    private void UpdateStageVisibility()
    {
        var showLivePreview = _uiMode == UiMode.Receiving && _liveBitmap is not null;
        LivePreviewImage.Visibility = showLivePreview ? Visibility.Visible : Visibility.Collapsed;
        PlaybackImage.Visibility = _uiMode is UiMode.PlaybackPlaying or UiMode.PlaybackPaused
            ? Visibility.Visible
            : Visibility.Collapsed;
        NoSignalOverlayTextBlock.Visibility = _uiMode == UiMode.NoSignal ? Visibility.Visible : Visibility.Collapsed;
        RecordingOverlayPanel.Visibility = _uiMode is UiMode.Recording or UiMode.RecordingPaused
            ? Visibility.Visible
            : Visibility.Collapsed;
        EncodingOverlayPanel.Visibility = _uiMode == UiMode.Encoding ? Visibility.Visible : Visibility.Collapsed;
        DoneOverlayTextBlock.Visibility = Visibility.Collapsed;

        if (_uiMode is UiMode.Recording or UiMode.RecordingPaused)
        {
            LivePreviewImage.Visibility = Visibility.Collapsed;
            PlaybackImage.Visibility = Visibility.Collapsed;
            NoSignalOverlayTextBlock.Visibility = Visibility.Collapsed;
            DoneOverlayTextBlock.Visibility = Visibility.Collapsed;
            RecordingOverlayTitleTextBlock.Text = _uiMode == UiMode.RecordingPaused
                ? "Recording paused"
                : "Recording in progress";
            RecordingOverlayBodyTextBlock.Text = "Live preview is stopped while recording.";
        }
    }

    private void PreviewViewportBorder_OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is not FrameworkElement element)
        {
            return;
        }

        var radius = 24.0;
        element.Clip = new RectangleGeometry(new Rect(0, 0, element.ActualWidth, element.ActualHeight), radius, radius);
    }

    private void UpdateEncodingProgressVisual()
    {
        EncodingProgressFill.Width = 360 * Math.Clamp(_encodingProgressPercent, 0, 100) / 100.0;
    }

    private void HandleEncodeProgress(EncodeProgress progress)
    {
        _encodingProgressPercent = progress.Percent;
        if (_uiMode == UiMode.Encoding)
        {
            UpdateStateMetrics();
            UpdateEncodingProgressVisual();
        }
    }

    private void UpdateRecordingElapsed()
    {
        if (_uiMode is UiMode.Recording or UiMode.RecordingPaused)
        {
            SecondaryValueTextBlock.Text = FormatTime(GetRecordingElapsed());
            return;
        }

        if (_uiMode is not UiMode.PlaybackPlaying and not UiMode.PlaybackPaused)
        {
            return;
        }

        _playbackPositionMilliseconds = GetPlaybackPositionMilliseconds();
        _playbackDurationMilliseconds = GetPlaybackLengthMilliseconds();

        if (_uiMode == UiMode.PlaybackPlaying &&
            _playbackDurationMilliseconds > 0 &&
            _playbackPositionMilliseconds >= _playbackDurationMilliseconds - 250 &&
            _mediaPlayer is not null &&
            !_mediaPlayer.IsPlaying)
        {
            _playbackReachedEnd = true;
            _uiMode = UiMode.PlaybackPaused;
            UpdateUiState();
            return;
        }

        UpdateStateMetrics();
    }

    private TimeSpan GetRecordingElapsed()
    {
        if (_recordingStartedAt is null)
        {
            return TimeSpan.Zero;
        }

        var now = _recordingPausedAt ?? DateTimeOffset.UtcNow;
        var elapsed = now - _recordingStartedAt.Value - _recordingPausedAccumulated;
        return elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed;
    }

    private void SetRecordingInputActive(bool enabled)
    {
        _spoutPollingService.FrameArrived -= SpoutPollingService_OnFrameArrived;
        if (enabled)
        {
            _spoutPollingService.FrameArrived += SpoutPollingService_OnFrameArrived;
            _spoutPollingService.SetRecordingMode(true, _selectedEncoderOption.UsesRealtimeRgbIntermediate);
        }
        else
        {
            _spoutPollingService.SetRecordingMode(false, false);
        }
    }

    private void BeginRecordingLatencyScope()
    {
        if (_recordingLatencyModeRestore is not null)
        {
            return;
        }

        _recordingLatencyModeRestore = GCSettings.LatencyMode;
        GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
    }

    private void EndRecordingLatencyScope()
    {
        if (_recordingLatencyModeRestore is null)
        {
            return;
        }

        GCSettings.LatencyMode = _recordingLatencyModeRestore.Value;
        _recordingLatencyModeRestore = null;
    }

    private void ClearRecordingState()
    {
        _recordingStartedAt = null;
        _recordingPausedAt = null;
        _recordingPausedAccumulated = TimeSpan.Zero;
    }

    private async Task DisposeCapturedTakeAsync()
    {
        var take = _capturedTake;
        _capturedTake = null;

        StopPreviewPlayback();

        if (take is not null)
        {
            try
            {
                await take.DisposeAsync().ConfigureAwait(true);
            }
            catch
            {
                // Cleanup is best-effort.
            }
        }
    }

    private static async Task TryDisposeSessionAsync(RecordingSession session)
    {
        try
        {
            await session.DisposeAsync().ConfigureAwait(true);
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return;
        }

        try
        {
            Directory.Delete(path, true);
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }

    private static string BuildTakeDirectory()
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        return Path.Combine(AppDataPaths.CacheRootDirectory, "takes", $"{stamp}_{suffix}");
    }

    private static string BuildAlphaSidecarPath(string previewPath)
    {
        var directory = Path.GetDirectoryName(previewPath) ?? string.Empty;
        var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(previewPath);
        return Path.Combine(directory, $"{fileNameWithoutExtension}.alpha.mp4");
    }

    private static string BuildSuggestedOutputName(EncoderOption option)
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        return $"spout_capture_{stamp}{option.Extension}";
    }

    private async Task SeekPlaybackToAsync(long targetMilliseconds, bool shouldPlay)
    {
        if (_mediaPlayer is null || _previewMedia is null || _capturedTake is null)
        {
            return;
        }

        var length = GetPlaybackLengthMilliseconds();
        if (length <= 0)
        {
            return;
        }

        var clampedTarget = Math.Clamp(targetMilliseconds, 0, length);
        var effectiveTarget = Math.Min(clampedTarget, Math.Max(length - 1, 0));

        _playbackReachedEnd = false;
        _playbackPositionMilliseconds = clampedTarget;

        _mediaPlayer.SeekTo(TimeSpan.FromMilliseconds(effectiveTarget));
        await Task.Delay(40).ConfigureAwait(true);

        if (shouldPlay)
        {
            _mediaPlayer.Play();
            _uiMode = UiMode.PlaybackPlaying;
        }
        else
        {
            if (_mediaPlayer.IsPlaying)
            {
                _mediaPlayer.Pause();
            }

            _uiMode = UiMode.PlaybackPaused;
        }

        UpdateUiState();
    }

    private long GetPlaybackPositionMilliseconds()
    {
        if (_playbackReachedEnd)
        {
            return GetPlaybackLengthMilliseconds();
        }

        return Math.Max(_playbackPositionMilliseconds, Math.Max(_mediaPlayer?.Time ?? 0, 0));
    }

    private long GetPlaybackLengthMilliseconds()
    {
        return Math.Max(_playbackDurationMilliseconds, Math.Max(_mediaPlayer?.Length ?? 0, 0));
    }

    private bool IsPlaybackAtEnd()
    {
        var length = GetPlaybackLengthMilliseconds();
        if (length <= 0)
        {
            return false;
        }

        return GetPlaybackPositionMilliseconds() >= length - 250;
    }

    private static IntPtr AlignPointer(IntPtr pointer, int alignment)
    {
        var value = pointer.ToInt64();
        var aligned = (value + alignment - 1) & -alignment;
        return new IntPtr(aligned);
    }

    private static UIElement CreateIcon(PackIconBootstrapIconsKind kind, double size = 42)
    {
        return new PackIconBootstrapIcons
        {
            Kind = kind,
            Width = size,
            Height = size,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    private static UIElement CreateCounterArrowIcon(PackIconBootstrapIconsKind kind, string counterText)
    {
        var grid = new Grid
        {
            Width = 48,
            Height = 48
        };

        grid.Children.Add(new PackIconBootstrapIcons
        {
            Kind = kind,
            Width = 40,
            Height = 40,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });

        grid.Children.Add(new TextBlock
        {
            Text = counterText,
            Margin = new Thickness(0, 1, 0, 0),
            FontSize = 15,
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center
        });

        return grid;
    }

    private EncoderOption ResolveEncoderOption(EncoderProfileKind kind)
    {
        foreach (var option in _encoderOptions)
        {
            if (option.Kind == kind)
            {
                return option;
            }
        }

        return _encoderOptions[0];
    }

    private static string FormatTime(TimeSpan value)
    {
        if (value < TimeSpan.Zero)
        {
            value = TimeSpan.Zero;
        }

        return value.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
    }
}
