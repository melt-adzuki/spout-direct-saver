using System;
using System.ComponentModel;
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
        base.OnClosing(e);

        _recordingTimer.Stop();
        StopPreviewPlayback();

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

        ResetPreviewArea();

        _recordingSession = new RecordingSession(SelectedEncoderOption, _outputPath!);
        _spoutPollingService.SetRecordingMode(true);
        _spoutPollingService.FrameArrived += SpoutPollingService_OnFrameArrived;
        BeginRecordingLatencyScope();
        _recordingStartedAt = DateTimeOffset.UtcNow;
        _recordingTimer.Start();

        RecorderStatusTextBlock.Text = SelectedEncoderOption.RequiresRealtimeEncoding
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
            HeaderStatusTextBlock.Text = "エンコード中";
            RecorderStatusTextBlock.Text = SelectedEncoderOption.RequiresRealtimeEncoding
                ? "録画を停止しました。エンコーダーを終了して最終フレームを確定しています。"
                : "録画を停止しました。可変fps動画を書き出しています。";

            var outputPath = await _recordingSession.FinalizeAsync(_videoExportService, CancellationToken.None);
            _lastRecordedFilePath = outputPath;
            _recordingSession = null;
            _spoutPollingService.FrameArrived -= SpoutPollingService_OnFrameArrived;
            _spoutPollingService.SetRecordingMode(false);

            LoadPreview(outputPath);
            RecorderStatusTextBlock.Text = $"保存完了: {outputPath}";
            PreviewStatusTextBlock.Text = "プレビュー再生できます。";
            HeaderStatusTextBlock.Text = "プレビュー可能";
        }
        catch (Exception ex)
        {
            RecorderStatusTextBlock.Text = $"保存に失敗しました: {ex.Message}";
            PreviewStatusTextBlock.Text = "保存失敗のためプレビューを開始できませんでした。";
            HeaderStatusTextBlock.Text = "エラー";

            if (_recordingSession is not null)
            {
                await _recordingSession.DisposeAsync();
                _recordingSession = null;
            }

            _spoutPollingService.FrameArrived -= SpoutPollingService_OnFrameArrived;
            _spoutPollingService.SetRecordingMode(false);
        }
        finally
        {
            EndRecordingLatencyScope();
            _recordingStartedAt = null;
            _isStopping = false;
            if (_recordingSession is null)
            {
                _spoutPollingService.SetRecordingMode(false);
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

            if (_recordingSession is null && !_isStopping)
            {
                RecorderStatusTextBlock.Text = status.Message;
            }
        });
    }

    private void SpoutPollingService_OnFrameArrived(object? sender, FramePacket frame)
    {
        try
        {
            if (!_isStopping && _recordingSession is not null)
            {
                _recordingSession.AppendFrame(frame);
                return;
            }

            frame.PixelBuffer.Dispose();
        }
        catch (Exception ex)
        {
            frame.PixelBuffer.Dispose();
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

    private static string FormatTime(long milliseconds)
    {
        if (milliseconds < 0)
        {
            milliseconds = 0;
        }

        return TimeSpan.FromMilliseconds(milliseconds).ToString(@"hh\:mm\:ss\.fff");
    }
}
