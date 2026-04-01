using System;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using Microsoft.Win32;
using SpoutDirectSaver.App.Models;
using SpoutDirectSaver.App.Services;

namespace SpoutDirectSaver.App;

public partial class MainWindow : Window
{
    private readonly SpoutPollingService _spoutPollingService = new();
    private readonly VideoExportService _videoExportService = new();
    private readonly EncoderSettingsStore _encoderSettingsStore = new();
    private readonly DispatcherTimer _recordingTimer;
    private readonly EncoderOption[] _encoderOptions = EncoderOption.CreateDefaults();

    private LibVLC? _libVlc;
    private LibVLCSharp.Shared.MediaPlayer? _mediaPlayer;
    private Media? _previewMedia;
    private WriteableBitmap? _liveBitmap;

    private RecordingSession? _recordingSession;
    private CaptureStatus? _latestStatus;
    private string? _outputPath;
    private string? _lastRecordedFilePath;
    private DateTimeOffset? _recordingStartedAt;
    private bool _isStopping;
    private bool _isSeekingPreview;
    private long _lastRenderedPreviewTicks;
    private GCLatencyMode? _recordingLatencyModeRestore;
    private string? _lastRecordingFaultMessage;
    private bool _recordingCpuFallbackActive;
    private volatile bool _recordingFrameAcceptanceEnabled;
    private EncoderSettingsRoot _encoderSettings = EncoderSettingsRoot.CreateDefaults();
    private EncoderSettingsRoot? _sessionEncoderSettings;
    private bool _isUiInitialized;

    public MainWindow()
    {
        InitializeComponent();

        Core.Initialize();

        _libVlc = new LibVLC("--no-video-title-show");
        _mediaPlayer = new LibVLCSharp.Shared.MediaPlayer(_libVlc);
        _mediaPlayer.TimeChanged += MediaPlayer_OnTimeChanged;
        _mediaPlayer.LengthChanged += MediaPlayer_OnLengthChanged;
        _mediaPlayer.EndReached += MediaPlayer_OnEndReached;
        PreviewVideoView.MediaPlayer = _mediaPlayer;

        FormatComboBox.ItemsSource = _encoderOptions;
        FormatComboBox.SelectedIndex = 0;
        RgbRateControlComboBox.ItemsSource = Enum.GetValues<RgbMediaFoundationRateControlMode>();
        RgbContentTypeComboBox.ItemsSource = Enum.GetValues<RgbMediaFoundationContentTypeHint>();
        AlphaTuneComboBox.ItemsSource = Enum.GetValues<AlphaNvencTune>();
        AlphaRateControlComboBox.ItemsSource = Enum.GetValues<AlphaNvencRateControlMode>();
        AlphaProfileComboBox.ItemsSource = Enum.GetValues<AlphaNvencProfile>();
        AlphaLevelComboBox.ItemsSource = Enum.GetValues<AlphaNvencLevel>();

        _encoderSettings = _encoderSettingsStore.Load();
        LoadEncoderSettingsIntoUi();

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

        _isUiInitialized = true;
        UpdateEncoderSettingsReadouts();
        ApplyEncoderSettingsVisibility();
        Loaded += MainWindow_OnLoaded;
        UpdateUiState();
    }

    private async void MainWindow_OnLoaded(object sender, RoutedEventArgs e)
    {
        await _spoutPollingService.StartAsync();
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);

        _recordingTimer.Stop();
        StopPreviewPlayback();
        _encoderSettings = CaptureEncoderSettingsFromUi();
        _encoderSettingsStore.Save(_encoderSettings);

        if (_recordingSession is not null)
        {
            await _recordingSession.DisposeAsync();
            EndRecordingLatencyScope();
        }

        CompositionTarget.Rendering -= CompositionTarget_OnRendering;
        await _spoutPollingService.DisposeAsync();

        if (_mediaPlayer is not null)
        {
            _mediaPlayer.TimeChanged -= MediaPlayer_OnTimeChanged;
            _mediaPlayer.LengthChanged -= MediaPlayer_OnLengthChanged;
            _mediaPlayer.EndReached -= MediaPlayer_OnEndReached;
            _mediaPlayer.Dispose();
        }

        _previewMedia?.Dispose();
        _libVlc?.Dispose();
    }

    private async void BrowseOutputButton_OnClick(object sender, RoutedEventArgs e)
    {
        await SelectOutputPathAsync(forcePrompt: true);
    }

    private void FormatComboBox_OnSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        var option = SelectedEncoderOption;
        FormatDescriptionTextBlock.Text = option.Description;
        ApplyEncoderSettingsVisibility();

        if (!string.IsNullOrWhiteSpace(_outputPath) &&
            !string.Equals(Path.GetExtension(_outputPath), option.Extension, StringComparison.OrdinalIgnoreCase))
        {
            _outputPath = null;
            OutputPathTextBox.Text = string.Empty;
        }
    }

    private async void StartRecordingButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_recordingSession is not null || _isStopping)
        {
            return;
        }

        if (!await SelectOutputPathAsync(forcePrompt: string.IsNullOrWhiteSpace(_outputPath)))
        {
            return;
        }

        var encoderSettings = CaptureEncoderSettingsFromUi();
        _encoderSettings = encoderSettings.Clone();
        _encoderSettingsStore.Save(_encoderSettings);
        _sessionEncoderSettings = encoderSettings.Clone();
        ResetPreviewArea();

        _recordingSession = new RecordingSession(SelectedEncoderOption, _outputPath!, encoderSettings);
        _lastRecordingFaultMessage = null;
        _recordingCpuFallbackActive = false;
        _recordingFrameAcceptanceEnabled = false;
        _spoutPollingService.FrameArrived += SpoutPollingService_OnFrameArrived;
        _spoutPollingService.SetRecordingMode(true, SelectedEncoderOption.UsesRealtimeRgbIntermediate);
        _recordingFrameAcceptanceEnabled = true;
        DebugTrace.WriteLine(
            "MainWindow",
            $"start recording output={_outputPath} preferGpu={SelectedEncoderOption.UsesRealtimeRgbIntermediate}");
        BeginRecordingLatencyScope();
        _recordingStartedAt = DateTimeOffset.UtcNow;
        _recordingTimer.Start();

        RecorderStatusTextBlock.Text = SelectedEncoderOption.UsesRealtimeRgbIntermediate
            ? "録画を開始しました。RGB をリアルタイム圧縮し、alpha sidecar を一時保存しています。"
            : SelectedEncoderOption.RequiresRealtimeEncoding
                ? "録画を開始しました。ffmpeg へ直接エンコードしています。"
            : "録画を開始しました。変化フレームのみを一時保存しています。";
        PreviewStatusTextBlock.Text = "録画中はライブ入力プレビューを停止して録画を優先します。";
        HeaderStatusTextBlock.Text = "録画中";

        UpdateUiState();
    }

    private async void StopRecordingButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_recordingSession is null || _isStopping)
        {
            return;
        }

        _isStopping = true;
        _recordingTimer.Stop();
        UpdateUiState();

        try
        {
            DebugTrace.WriteLine("MainWindow", "stop recording clicked");
            HeaderStatusTextBlock.Text = "エンコード中";
            RecorderStatusTextBlock.Text = SelectedEncoderOption.UsesRealtimeRgbIntermediate
                ? "録画を停止しました。RGB を確定し、alpha sidecar を書き出しています。"
                : SelectedEncoderOption.RequiresRealtimeEncoding
                    ? "録画を停止しました。エンコーダーを終了して最終フレームを確定しています。"
                : "録画を停止しました。可変fps動画を書き出しています。";

            var outputPath = await _recordingSession.FinalizeAsync(_videoExportService, CancellationToken.None);
            _lastRecordedFilePath = outputPath;
            _recordingSession = null;
            _lastRecordingFaultMessage = null;
            _recordingCpuFallbackActive = false;
            _recordingFrameAcceptanceEnabled = false;
            _spoutPollingService.FrameArrived -= SpoutPollingService_OnFrameArrived;
            _spoutPollingService.SetRecordingMode(false, false);

            LoadPreview(outputPath);
            RecorderStatusTextBlock.Text = $"保存完了: {outputPath}";
            PreviewStatusTextBlock.Text = "プレビュー再生できます。";
            HeaderStatusTextBlock.Text = "プレビュー可能";
        }
        catch (Exception ex)
        {
            var failureMessage =
                ex.Message == "録画中にフレームを受信できませんでした。" &&
                !string.IsNullOrWhiteSpace(_lastRecordingFaultMessage)
                    ? _lastRecordingFaultMessage
                    : ex.Message;
            RecorderStatusTextBlock.Text = $"保存に失敗しました: {failureMessage}";
            PreviewStatusTextBlock.Text = "保存失敗のためプレビューを開始できませんでした。";
            HeaderStatusTextBlock.Text = "エラー";

            if (_recordingSession is not null)
            {
                await _recordingSession.DisposeAsync();
                _recordingSession = null;
            }

            _recordingCpuFallbackActive = false;
            _recordingFrameAcceptanceEnabled = false;

            _spoutPollingService.FrameArrived -= SpoutPollingService_OnFrameArrived;
            _spoutPollingService.SetRecordingMode(false, false);
        }
        finally
        {
            EndRecordingLatencyScope();
            _recordingStartedAt = null;
            _isStopping = false;
            if (_recordingSession is null)
            {
                _recordingFrameAcceptanceEnabled = false;
                _spoutPollingService.SetRecordingMode(false, false);
            }
            UpdateRecordingElapsed();
            UpdateUiState();
        }
    }

    private void RerecordButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_recordingSession is not null || _isStopping)
        {
            return;
        }

        StopPreviewPlayback();
        ResetPreviewArea();

        RecorderStatusTextBlock.Text = "再録画の準備ができました。録画開始を押すと新しいファイルを書き出します。";
        PreviewStatusTextBlock.Text = "プレビュー待機中です。";
        HeaderStatusTextBlock.Text = _latestStatus?.IsConnected == true ? "受信中" : "入力待ち";
        UpdateUiState();
    }

    private void PlayPreviewButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_previewMedia is null || _mediaPlayer is null)
        {
            return;
        }

        _mediaPlayer.Play();
        PreviewStatusTextBlock.Text = "プレビュー再生中です。";
    }

    private void PausePreviewButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_mediaPlayer is null)
        {
            return;
        }

        _mediaPlayer.Pause();
        PreviewStatusTextBlock.Text = "プレビューを一時停止しました。";
    }

    private void SeekSlider_OnPreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _isSeekingPreview = true;
    }

    private void SeekSlider_OnPreviewMouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_mediaPlayer is not null && _mediaPlayer.IsSeekable)
        {
            _mediaPlayer.Time = (long)SeekSlider.Value;
        }

        _isSeekingPreview = false;
    }

    private void MediaPlayer_OnLengthChanged(object? sender, MediaPlayerLengthChangedEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            SeekSlider.Maximum = Math.Max(e.Length, 1);
            PreviewTimeTextBlock.Text = $"{FormatTime(_mediaPlayer?.Time ?? 0)} / {FormatTime(e.Length)}";
        });
    }

    private void MediaPlayer_OnTimeChanged(object? sender, MediaPlayerTimeChangedEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            if (!_isSeekingPreview)
            {
                SeekSlider.Value = e.Time;
            }

            var total = _mediaPlayer?.Length ?? 0;
            PreviewTimeTextBlock.Text = $"{FormatTime(e.Time)} / {FormatTime(total)}";
        });
    }

    private void MediaPlayer_OnEndReached(object? sender, EventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            PreviewStatusTextBlock.Text = "プレビューの最後まで再生しました。";
            SeekSlider.Value = SeekSlider.Maximum;
        });
    }

    private void SpoutPollingService_OnStatusChanged(object? sender, CaptureStatus status)
    {
        _latestStatus = status;

        _ = Dispatcher.BeginInvoke(() =>
        {
            SenderNameTextBlock.Text = string.IsNullOrWhiteSpace(status.SenderName) ? "-" : status.SenderName;
            SenderMetricsTextBlock.Text = status.Width > 0
                ? $"{status.Width} x {status.Height} / {status.SenderFps:0.##} fps"
                : "- / -";

            LivePreviewBadgeTextBlock.Text = status.IsConnected ? "受信中" : "入力待ち";
            HeaderStatusTextBlock.Text = _recordingSession is not null
                ? "録画中"
                : status.IsConnected
                    ? "受信中"
                    : "入力待ち";

            if (_recordingSession is not null && !_isStopping && LooksLikeCaptureFault(status.Message))
            {
                _lastRecordingFaultMessage = status.Message;
                RecorderStatusTextBlock.Text = status.Message;
                HeaderStatusTextBlock.Text = "エラー";
            }
            else if (_recordingSession is null && !_isStopping)
            {
                RecorderStatusTextBlock.Text = status.Message;
            }
        });
    }

    private void SpoutPollingService_OnFrameArrived(object? sender, FramePacket frame)
    {
        try
        {
            DebugTrace.WriteLine(
                "MainWindow",
                $"frame arrived stopping={_isStopping} sessionPresent={_recordingSession is not null} gpu={frame.GpuTexture is not null} cpu={frame.PixelBuffer is not null}");
            if (!_isStopping && _recordingSession is not null && _recordingFrameAcceptanceEnabled)
            {
                DebugTrace.WriteLine("MainWindow", "append frame");
                _recordingSession.AppendFrame(frame);
                return;
            }

            DebugTrace.WriteLine(
                "MainWindow",
                $"dispose frame without append acceptanceEnabled={_recordingFrameAcceptanceEnabled}");
            frame.Dispose();
        }
        catch (Exception ex)
        {
            DebugTrace.WriteLine("MainWindow", $"append exception {ex.Message}");
            frame.Dispose();
            if (TryFallbackRecordingToCpu(frame, ex))
            {
                return;
            }

            _lastRecordingFaultMessage = ex.Message;
            _ = Dispatcher.BeginInvoke(() =>
            {
                RecorderStatusTextBlock.Text = $"録画を継続できませんでした: {ex.Message}";
                HeaderStatusTextBlock.Text = "エラー";
            });
        }
    }

    private void CompositionTarget_OnRendering(object? sender, EventArgs e)
    {
        if (_recordingSession is not null || _isStopping)
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
            return;
        }
    }

    private void RenderLivePreview(LivePreviewFrame frame)
    {
        var width = (int)frame.Width;
        var height = (int)frame.Height;
        var stride = width * 4;

        if (_liveBitmap is null || _liveBitmap.PixelWidth != width || _liveBitmap.PixelHeight != height)
        {
            _liveBitmap = new WriteableBitmap(width, height, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null);
            LivePreviewImage.Source = _liveBitmap;
        }

        _liveBitmap.WritePixels(new Int32Rect(0, 0, width, height), frame.PixelData, stride * height, stride);

        LivePreviewPlaceholderBorder.Visibility = Visibility.Collapsed;
        SenderNameTextBlock.Text = string.IsNullOrWhiteSpace(frame.SenderName) ? "-" : frame.SenderName;
        SenderMetricsTextBlock.Text = $"{frame.Width} x {frame.Height} / {frame.SenderFps:0.##} fps";
    }

    private Task<bool> SelectOutputPathAsync(bool forcePrompt)
    {
        var option = SelectedEncoderOption;
        if (!forcePrompt &&
            !string.IsNullOrWhiteSpace(_outputPath) &&
            string.Equals(Path.GetExtension(_outputPath), option.Extension, StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(true);
        }

        var dialog = new SaveFileDialog
        {
            Title = "保存先を選択",
            Filter = option.FileDialogFilter,
            DefaultExt = option.Extension,
            AddExtension = true,
            OverwritePrompt = true,
            FileName = BuildSuggestedOutputName(option)
        };

        if (!string.IsNullOrWhiteSpace(_outputPath))
        {
            dialog.InitialDirectory = Path.GetDirectoryName(_outputPath);
            dialog.FileName = Path.GetFileNameWithoutExtension(_outputPath);
        }

        var result = dialog.ShowDialog(this);
        if (result != true)
        {
            return Task.FromResult(false);
        }

        _outputPath = dialog.FileName;
        OutputPathTextBox.Text = _outputPath;
        return Task.FromResult(true);
    }

    private void LoadPreview(string outputPath)
    {
        if (_mediaPlayer is null || _libVlc is null)
        {
            return;
        }

        StopPreviewPlayback();

        _previewMedia = new Media(_libVlc, new Uri(outputPath));
        _mediaPlayer.Play(_previewMedia);

        PreviewPlaceholderBorder.Visibility = Visibility.Collapsed;
        PreviewFileTextBlock.Text = outputPath;
        SeekSlider.Value = 0;
        PreviewStatusTextBlock.Text = "プレビューを読み込みました。";
    }

    private void StopPreviewPlayback()
    {
        _mediaPlayer?.Stop();
        _previewMedia?.Dispose();
        _previewMedia = null;
    }

    private void ResetPreviewArea()
    {
        StopPreviewPlayback();

        PreviewPlaceholderBorder.Visibility = Visibility.Visible;
        PreviewFileTextBlock.Text = _lastRecordedFilePath is null
            ? "まだ録画されていません"
            : $"前回保存: {_lastRecordedFilePath}";
        PreviewStatusTextBlock.Text = "プレビュー待機中です。";
        PreviewTimeTextBlock.Text = "00:00:00.000 / 00:00:00.000";
        SeekSlider.Maximum = 1;
        SeekSlider.Value = 0;
    }

    private bool TryFallbackRecordingToCpu(FramePacket frame, Exception exception)
    {
        if (_isStopping ||
            _recordingSession is null ||
            _recordingCpuFallbackActive ||
            !SelectedEncoderOption.UsesRealtimeRgbIntermediate ||
            frame.GpuTexture is null ||
            string.IsNullOrWhiteSpace(_outputPath))
        {
            return false;
        }

        var failedSession = _recordingSession;
        _recordingSession = new RecordingSession(
            SelectedEncoderOption,
            _outputPath!,
            _sessionEncoderSettings ?? _encoderSettings);
        _recordingCpuFallbackActive = true;
        _lastRecordingFaultMessage = $"GPU 録画経路が利用できなかったため CPU 受信へフォールバックしました: {exception.Message}";
        _spoutPollingService.SetRecordingMode(true, false);

        _ = Dispatcher.BeginInvoke(() =>
        {
            RecorderStatusTextBlock.Text = "GPU 録画経路が利用できなかったため、CPU 受信経由で録画を継続しています。";
            PreviewStatusTextBlock.Text = "録画は継続中です。GPU texture 入力が使えない環境向けにフォールバックしました。";
            HeaderStatusTextBlock.Text = "録画中";
        });

        _ = DisposeFailedSessionAsync(failedSession);
        return true;
    }

    private void UpdateUiState()
    {
        var hasPreview = _previewMedia is not null || !string.IsNullOrWhiteSpace(_lastRecordedFilePath);
        var recording = _recordingSession is not null;

        StartRecordingButton.IsEnabled = !recording && !_isStopping;
        StopRecordingButton.IsEnabled = recording && !_isStopping;
        RerecordButton.IsEnabled = !recording && !_isStopping;
        BrowseOutputButton.IsEnabled = !recording && !_isStopping;
        FormatComboBox.IsEnabled = !recording && !_isStopping;
        PlayPreviewButton.IsEnabled = !recording && !_isStopping && hasPreview;
        PausePreviewButton.IsEnabled = !recording && !_isStopping && hasPreview;
        SeekSlider.IsEnabled = !recording && !_isStopping && hasPreview;
        EncoderSettingsRootPanel.IsEnabled = !recording && !_isStopping;
        ApplyEncoderSettingsVisibility();
    }

    private void UpdateRecordingElapsed()
    {
        if (_recordingStartedAt is null)
        {
            RecordingElapsedTextBlock.Text = "00:00:00.000";
            return;
        }

        var elapsed = DateTimeOffset.UtcNow - _recordingStartedAt.Value;
        RecordingElapsedTextBlock.Text = $"{elapsed:hh\\:mm\\:ss\\.fff}";
    }

    private EncoderOption SelectedEncoderOption => (EncoderOption)(FormatComboBox.SelectedItem ?? _encoderOptions[0]);

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

    private static string BuildSuggestedOutputName(EncoderOption option)
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        return $"spout_capture_{stamp}{option.Extension}";
    }

    private void EncoderSettingsSelection_OnChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!_isUiInitialized)
        {
            return;
        }

        ApplyEncoderSettingsVisibility();
    }

    private void EncoderSettingsToggle_OnChanged(object sender, RoutedEventArgs e)
    {
        if (!_isUiInitialized)
        {
            return;
        }

        ApplyEncoderSettingsVisibility();
    }

    private void EncoderSettingsSlider_OnChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_isUiInitialized)
        {
            return;
        }

        UpdateEncoderSettingsReadouts();
        ApplyEncoderSettingsVisibility();
    }

    private void LoadEncoderSettingsIntoUi()
    {
        RgbRateControlComboBox.SelectedItem = _encoderSettings.Rgb.RateControlMode;
        RgbQualityVsSpeedSlider.Value = _encoderSettings.Rgb.QualityVsSpeed;
        RgbQualitySlider.Value = _encoderSettings.Rgb.Quality;
        RgbTargetBitrateTextBox.Text = _encoderSettings.Rgb.TargetBitrateMbps.ToString(CultureInfo.InvariantCulture);
        RgbBufferSizeTextBox.Text = _encoderSettings.Rgb.BufferSizeMb.ToString(CultureInfo.InvariantCulture);
        RgbLowLatencyCheckBox.IsChecked = _encoderSettings.Rgb.LowLatency;
        RgbUseConstantQpCheckBox.IsChecked = _encoderSettings.Rgb.UseConstantQp;
        RgbConstantQpTextBox.Text = _encoderSettings.Rgb.ConstantQp.ToString(CultureInfo.InvariantCulture);
        RgbMinQpTextBox.Text = _encoderSettings.Rgb.MinQp.ToString(CultureInfo.InvariantCulture);
        RgbMaxQpTextBox.Text = _encoderSettings.Rgb.MaxQp.ToString(CultureInfo.InvariantCulture);
        RgbGopSizeTextBox.Text = _encoderSettings.Rgb.GopSize.ToString(CultureInfo.InvariantCulture);
        RgbContentTypeComboBox.SelectedItem = _encoderSettings.Rgb.ContentTypeHint;
        RgbWorkerThreadsTextBox.Text = _encoderSettings.Rgb.WorkerThreads.ToString(CultureInfo.InvariantCulture);

        AlphaPresetSlider.Value = _encoderSettings.Alpha.Preset.ToUiPresetLevel();
        AlphaTuneComboBox.SelectedItem = _encoderSettings.Alpha.Tune;
        AlphaRateControlComboBox.SelectedItem = _encoderSettings.Alpha.RateControlMode;
        AlphaTargetBitrateTextBox.Text = _encoderSettings.Alpha.TargetBitrateMbps.ToString(CultureInfo.InvariantCulture);
        AlphaConstantQualitySlider.Value = _encoderSettings.Alpha.ConstantQuality;
        AlphaConstantQpSlider.Value = _encoderSettings.Alpha.ConstantQp;
        AlphaMinQpTextBox.Text = _encoderSettings.Alpha.MinQp.ToString(CultureInfo.InvariantCulture);
        AlphaMaxQpTextBox.Text = _encoderSettings.Alpha.MaxQp.ToString(CultureInfo.InvariantCulture);
        AlphaLookaheadTextBox.Text = _encoderSettings.Alpha.LookaheadFrames.ToString(CultureInfo.InvariantCulture);
        AlphaSpatialAqCheckBox.IsChecked = _encoderSettings.Alpha.SpatialAq;
        AlphaTemporalAqCheckBox.IsChecked = _encoderSettings.Alpha.TemporalAq;
        AlphaAqStrengthTextBox.Text = _encoderSettings.Alpha.AqStrength.ToString(CultureInfo.InvariantCulture);
        AlphaZeroLatencyCheckBox.IsChecked = _encoderSettings.Alpha.ZeroLatency;
        AlphaBFramesTextBox.Text = _encoderSettings.Alpha.BFrames.ToString(CultureInfo.InvariantCulture);
        AlphaGopSizeTextBox.Text = _encoderSettings.Alpha.GopSize.ToString(CultureInfo.InvariantCulture);
        AlphaProfileComboBox.SelectedItem = _encoderSettings.Alpha.Profile;
        AlphaLevelComboBox.SelectedItem = _encoderSettings.Alpha.Level;

        UpdateEncoderSettingsReadouts();
        ApplyEncoderSettingsVisibility();
    }

    private EncoderSettingsRoot CaptureEncoderSettingsFromUi()
    {
        var settings = _encoderSettings.Clone();

        settings.Rgb.RateControlMode = SelectedEnum(RgbRateControlComboBox, settings.Rgb.RateControlMode);
        settings.Rgb.QualityVsSpeed = ReadSlider(RgbQualityVsSpeedSlider, settings.Rgb.QualityVsSpeed);
        settings.Rgb.Quality = ReadSlider(RgbQualitySlider, settings.Rgb.Quality);
        settings.Rgb.TargetBitrateMbps = ReadInt(RgbTargetBitrateTextBox, settings.Rgb.TargetBitrateMbps);
        settings.Rgb.BufferSizeMb = ReadInt(RgbBufferSizeTextBox, settings.Rgb.BufferSizeMb);
        settings.Rgb.LowLatency = RgbLowLatencyCheckBox.IsChecked ?? settings.Rgb.LowLatency;
        settings.Rgb.UseConstantQp = RgbUseConstantQpCheckBox.IsChecked ?? settings.Rgb.UseConstantQp;
        settings.Rgb.ConstantQp = ReadInt(RgbConstantQpTextBox, settings.Rgb.ConstantQp);
        settings.Rgb.MinQp = ReadInt(RgbMinQpTextBox, settings.Rgb.MinQp);
        settings.Rgb.MaxQp = ReadInt(RgbMaxQpTextBox, settings.Rgb.MaxQp);
        settings.Rgb.GopSize = ReadInt(RgbGopSizeTextBox, settings.Rgb.GopSize);
        settings.Rgb.ContentTypeHint = SelectedEnum(RgbContentTypeComboBox, settings.Rgb.ContentTypeHint);
        settings.Rgb.WorkerThreads = ReadInt(RgbWorkerThreadsTextBox, settings.Rgb.WorkerThreads);

        settings.Alpha.Preset = AlphaNvencValueExtensions.FromUiPresetLevel(
            ReadSlider(AlphaPresetSlider, settings.Alpha.Preset.ToUiPresetLevel()));
        settings.Alpha.Tune = SelectedEnum(AlphaTuneComboBox, settings.Alpha.Tune);
        settings.Alpha.RateControlMode = SelectedEnum(AlphaRateControlComboBox, settings.Alpha.RateControlMode);
        settings.Alpha.TargetBitrateMbps = ReadInt(AlphaTargetBitrateTextBox, settings.Alpha.TargetBitrateMbps);
        settings.Alpha.ConstantQuality = ReadSlider(AlphaConstantQualitySlider, settings.Alpha.ConstantQuality);
        settings.Alpha.ConstantQp = ReadSlider(AlphaConstantQpSlider, settings.Alpha.ConstantQp);
        settings.Alpha.MinQp = ReadInt(AlphaMinQpTextBox, settings.Alpha.MinQp);
        settings.Alpha.MaxQp = ReadInt(AlphaMaxQpTextBox, settings.Alpha.MaxQp);
        settings.Alpha.LookaheadFrames = ReadInt(AlphaLookaheadTextBox, settings.Alpha.LookaheadFrames);
        settings.Alpha.SpatialAq = AlphaSpatialAqCheckBox.IsChecked ?? settings.Alpha.SpatialAq;
        settings.Alpha.TemporalAq = AlphaTemporalAqCheckBox.IsChecked ?? settings.Alpha.TemporalAq;
        settings.Alpha.AqStrength = ReadInt(AlphaAqStrengthTextBox, settings.Alpha.AqStrength);
        settings.Alpha.ZeroLatency = AlphaZeroLatencyCheckBox.IsChecked ?? settings.Alpha.ZeroLatency;
        settings.Alpha.BFrames = ReadInt(AlphaBFramesTextBox, settings.Alpha.BFrames);
        settings.Alpha.GopSize = ReadInt(AlphaGopSizeTextBox, settings.Alpha.GopSize);
        settings.Alpha.Profile = SelectedEnum(AlphaProfileComboBox, settings.Alpha.Profile);
        settings.Alpha.Level = SelectedEnum(AlphaLevelComboBox, settings.Alpha.Level);

        settings.Normalize();
        return settings;
    }

    private void ApplyEncoderSettingsVisibility()
    {
        var showEncoderSettings = SelectedEncoderOption.Kind == EncoderProfileKind.HevcNvencMp4AlphaMp4;
        EncoderSettingsSectionBorder.Visibility = showEncoderSettings
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (!showEncoderSettings)
        {
            return;
        }

        UpdateEncoderSettingsReadouts();

        var rgbRateControl = SelectedEnum(RgbRateControlComboBox, RgbMediaFoundationRateControlMode.Quality);
        RgbQualityPanel.Visibility = rgbRateControl == RgbMediaFoundationRateControlMode.Quality
            ? Visibility.Visible
            : Visibility.Collapsed;
        RgbTargetBitratePanel.Visibility = rgbRateControl == RgbMediaFoundationRateControlMode.Cbr
            ? Visibility.Visible
            : Visibility.Collapsed;
        RgbConstantQpPanel.Visibility = RgbUseConstantQpCheckBox.IsChecked == true
            ? Visibility.Visible
            : Visibility.Collapsed;

        var alphaRateControl = SelectedEnum(AlphaRateControlComboBox, AlphaNvencRateControlMode.Vbr);
        AlphaTargetBitratePanel.Visibility =
            alphaRateControl is AlphaNvencRateControlMode.Vbr or AlphaNvencRateControlMode.Cbr
                ? Visibility.Visible
                : Visibility.Collapsed;
        AlphaConstantQualityPanel.Visibility = alphaRateControl == AlphaNvencRateControlMode.Vbr
            ? Visibility.Visible
            : Visibility.Collapsed;
        AlphaConstantQpPanel.Visibility = alphaRateControl == AlphaNvencRateControlMode.ConstQp
            ? Visibility.Visible
            : Visibility.Collapsed;

        var useAq = AlphaSpatialAqCheckBox.IsChecked == true || AlphaTemporalAqCheckBox.IsChecked == true;
        AlphaAqStrengthPanel.Visibility = useAq
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void UpdateEncoderSettingsReadouts()
    {
        if (!_isUiInitialized)
        {
            return;
        }

        RgbQualityVsSpeedValueTextBlock.Text = ReadSlider(RgbQualityVsSpeedSlider, 16).ToString(CultureInfo.InvariantCulture);
        RgbQualityValueTextBlock.Text = ReadSlider(RgbQualitySlider, 70).ToString(CultureInfo.InvariantCulture);
        AlphaPresetValueTextBlock.Text = $"P{ReadSlider(AlphaPresetSlider, 3)} / {DescribeAlphaPreset(ReadSlider(AlphaPresetSlider, 3))}";
        AlphaConstantQualityValueTextBlock.Text = ReadSlider(AlphaConstantQualitySlider, 19).ToString(CultureInfo.InvariantCulture);
        AlphaConstantQpValueTextBlock.Text = ReadSlider(AlphaConstantQpSlider, 23).ToString(CultureInfo.InvariantCulture);
    }

    private static int ReadInt(System.Windows.Controls.TextBox textBox, int fallback)
    {
        var text = textBox.Text?.Trim();
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;
    }

    private static int ReadSlider(System.Windows.Controls.Slider slider, int fallback)
    {
        var value = (int)Math.Round(slider.Value);
        return value >= 0 ? value : fallback;
    }

    private static string DescribeAlphaPreset(int value)
    {
        return value switch
        {
            1 => "Fastest",
            2 => "Faster",
            3 => "Fast",
            4 => "Medium",
            5 => "Good quality",
            6 => "Better quality",
            _ => "Best quality"
        };
    }

    private static TEnum SelectedEnum<TEnum>(System.Windows.Controls.ComboBox comboBox, TEnum fallback)
        where TEnum : struct, Enum
    {
        return comboBox.SelectedItem is TEnum selected ? selected : fallback;
    }

    private static string FormatTime(long milliseconds)
    {
        if (milliseconds < 0)
        {
            milliseconds = 0;
        }

        return TimeSpan.FromMilliseconds(milliseconds).ToString(@"hh\:mm\:ss\.fff");
    }

    private static bool LooksLikeCaptureFault(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        return message.Contains("失敗", StringComparison.Ordinal) ||
               message.Contains("エラー", StringComparison.Ordinal) ||
               message.Contains("フォールバック", StringComparison.Ordinal);
    }

    private static async Task DisposeFailedSessionAsync(RecordingSession failedSession)
    {
        try
        {
            await failedSession.DisposeAsync();
        }
        catch
        {
            // Ignore fallback cleanup failures so the replacement session can continue.
        }
    }
}
