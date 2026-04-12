using System;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LibVLCSharp.Shared;
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

    private LibVLC? _libVlc;
    private VLCMediaPlayer? _mediaPlayer;
    private Media? _previewMedia;
    private WriteableBitmap? _liveBitmap;
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

    public MainWindow()
    {
        InitializeComponent();

        Core.Initialize();

        _encoderSettings = _encoderSettingsStore.Load();
        _selectedEncoderOption = ResolveEncoderOption(_encoderSettings.SelectedEncoderKind);

        _libVlc = new LibVLC("--no-video-title-show");
        _mediaPlayer = new VLCMediaPlayer(_libVlc);
        _mediaPlayer.TimeChanged += MediaPlayer_OnTimeChanged;
        _mediaPlayer.LengthChanged += MediaPlayer_OnLengthChanged;
        _mediaPlayer.EndReached += MediaPlayer_OnEndReached;
        PlaybackVideoView.MediaPlayer = _mediaPlayer;

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

    private void BackwardButton_OnClick(object sender, RoutedEventArgs e) => StepPlayback(-5000);

    private void PlayPauseButton_OnClick(object sender, RoutedEventArgs e) => TogglePlayback();

    private void ForwardButton_OnClick(object sender, RoutedEventArgs e) => StepPlayback(5000);

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

            var finalizedPreviewPath = await session.FinalizeAsync(_videoExportService, CancellationToken.None).ConfigureAwait(true);
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
                sidecarPath);

            _pendingTakeDirectory = null;
            _pendingPreviewPath = null;
            _uiMode = UiMode.PlaybackPaused;
            LoadCapturedTakePreview(finalizedPreviewPath);
            _lastExportPath = null;
        }
        catch (Exception ex)
        {
            await AbortRecordingAsync(showError: true, errorMessage: ex.Message).ConfigureAwait(true);
            return;
        }
        finally
        {
            await TryDisposeSessionAsync(session).ConfigureAwait(true);
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

    private async Task BeginEncodingAsync()
    {
        if (_capturedTake is null || _uiMode is UiMode.Encoding)
        {
            return;
        }

        PausePlayback();

        var dialog = new SaveFileDialog
        {
            Title = "Save Encoded File",
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

        _encodeCancellationSource?.Dispose();
        _encodeCancellationSource = new CancellationTokenSource();
        var progress = new Progress<EncodeProgress>(HandleEncodeProgress);

        _encodeTask = EncodeCapturedTakeAsync(dialog.FileName, progress, _encodeCancellationSource.Token);

        try
        {
            await _encodeTask.ConfigureAwait(true);
            _uiMode = UiMode.Encoded;
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

    private void TogglePlayback()
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

        _mediaPlayer.Pause();
        _uiMode = UiMode.PlaybackPaused;
        UpdateUiState();
    }

    private void StepPlayback(int deltaMilliseconds)
    {
        if (_mediaPlayer is null || _capturedTake is null)
        {
            return;
        }

        var current = Math.Max(_mediaPlayer.Time, 0);
        var length = Math.Max(_mediaPlayer.Length, 0);
        if (length <= 0)
        {
            return;
        }

        var target = Math.Clamp(current + deltaMilliseconds, 0, length);
        _mediaPlayer.Time = target;
        if (_uiMode is UiMode.PlaybackPlaying or UiMode.PlaybackPaused)
        {
            SecondaryValueTextBlock.Text = FormatTime(TimeSpan.FromMilliseconds(target));
        }
    }

    private void LoadCapturedTakePreview(string previewPath)
    {
        if (_mediaPlayer is null || _libVlc is null)
        {
            return;
        }

        StopPreviewPlayback();

        _previewMedia = new Media(_libVlc, new Uri(previewPath, UriKind.Absolute));
        _mediaPlayer.Play(_previewMedia);
        _mediaPlayer.Pause();
        _mediaPlayer.Time = 0;
    }

    private void StopPreviewPlayback()
    {
        if (_mediaPlayer is not null)
        {
            _mediaPlayer.Stop();
        }

        _previewMedia?.Dispose();
        _previewMedia = null;
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

    private void MediaPlayer_OnTimeChanged(object? sender, MediaPlayerTimeChangedEventArgs e)
    {
        if (_uiMode is not UiMode.PlaybackPlaying and not UiMode.PlaybackPaused)
        {
            return;
        }

        _ = Dispatcher.BeginInvoke(UpdateStateMetrics);
    }

    private void MediaPlayer_OnLengthChanged(object? sender, MediaPlayerLengthChangedEventArgs e)
    {
        if (_uiMode is not UiMode.PlaybackPlaying and not UiMode.PlaybackPaused)
        {
            return;
        }

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
            _uiMode = UiMode.PlaybackPaused;
            UpdateUiState();
        });
    }

    private void BuildButtonContent()
    {
        RecordButton.Content = CreateButtonContent("⏺", "Record");
        SettingsButton.Content = CreateButtonContent("⚙", "Settings");
        DoneButton.Content = CreateButtonContent("✓", "Done");
        PauseResumeButton.Content = CreateButtonContent("⏸", "Pause");
        RemoveButton.Content = CreateButtonContent("⌦", "Remove");
        BackwardButton.Content = CreateButtonContent("↺5", "Backward", 36);
        PlayPauseButton.Content = CreateButtonContent("▶", "Play");
        ForwardButton.Content = CreateButtonContent("5↻", "Forward", 36);
        EncodeButton.Content = CreateButtonContent("↪", "Encode");
        CancelButton.Content = CreateButtonContent("✕", "Cancel");
        ContinueButton.Content = CreateButtonContent("✓", "Continue");
    }

    private static UIElement CreateButtonContent(string icon, string label, double iconFontSize = 42)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Vertical,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        panel.Children.Add(new TextBlock
        {
            Text = icon,
            FontFamily = new FontFamily("Segoe UI Symbol"),
            FontSize = iconFontSize,
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center
        });

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
        EncodedActionsPanel.Visibility = _uiMode == UiMode.Encoded
            ? Visibility.Visible
            : Visibility.Collapsed;

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
        ContinueButton.IsEnabled = _uiMode == UiMode.Encoded;

        PauseResumeButton.Content = CreateButtonContent(
            _uiMode == UiMode.RecordingPaused ? "▶" : "⏸",
            _uiMode == UiMode.RecordingPaused ? "Resume" : "Pause");

        PlayPauseButton.Content = CreateButtonContent(
            _uiMode == UiMode.PlaybackPlaying ? "⏸" : "▶",
            _uiMode == UiMode.PlaybackPlaying ? "Pause" : "Play");

        UpdateInitialMetrics();
        UpdateStateMetrics();
        UpdateStageVisibility();
        UpdateEncodingProgressVisual();
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
                SecondaryValueTextBlock.Text = FormatTime(TimeSpan.FromMilliseconds(Math.Max(_mediaPlayer?.Time ?? 0, 0)));
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
        PlaybackVideoView.Visibility = _uiMode is UiMode.PlaybackPlaying or UiMode.PlaybackPaused
            ? Visibility.Visible
            : Visibility.Collapsed;
        NoSignalOverlayTextBlock.Visibility = _uiMode == UiMode.NoSignal ? Visibility.Visible : Visibility.Collapsed;
        EncodingOverlayPanel.Visibility = _uiMode == UiMode.Encoding ? Visibility.Visible : Visibility.Collapsed;
        DoneOverlayTextBlock.Visibility = _uiMode == UiMode.Encoded ? Visibility.Visible : Visibility.Collapsed;

        if (_uiMode is UiMode.Recording or UiMode.RecordingPaused)
        {
            LivePreviewImage.Visibility = Visibility.Collapsed;
            PlaybackVideoView.Visibility = Visibility.Collapsed;
            NoSignalOverlayTextBlock.Visibility = Visibility.Collapsed;
            EncodingOverlayPanel.Visibility = Visibility.Collapsed;
            DoneOverlayTextBlock.Visibility = Visibility.Collapsed;
        }
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
        if (_uiMode is not UiMode.Recording and not UiMode.RecordingPaused)
        {
            return;
        }

        SecondaryValueTextBlock.Text = FormatTime(GetRecordingElapsed());
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
