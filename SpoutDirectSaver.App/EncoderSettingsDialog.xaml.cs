using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using SpoutDirectSaver.App.Models;

namespace SpoutDirectSaver.App;

internal partial class EncoderSettingsDialog : Window
{
    private readonly EncoderOption[] _encoderOptions;
    private bool _isInitialized;
    private EncoderSettingsRoot _encoderSettings;

    internal EncoderSettingsDialog(
        EncoderOption[] encoderOptions,
        EncoderOption selectedEncoderOption,
        EncoderSettingsRoot settings)
    {
        InitializeComponent();

        _encoderOptions = encoderOptions;
        _encoderSettings = settings.Clone();

        FormatComboBox.ItemsSource = _encoderOptions;
        RgbRateControlComboBox.ItemsSource = Enum.GetValues<RgbMediaFoundationRateControlMode>();
        RgbContentTypeComboBox.ItemsSource = Enum.GetValues<RgbMediaFoundationContentTypeHint>();
        AlphaTuneComboBox.ItemsSource = Enum.GetValues<AlphaNvencTune>();
        AlphaRateControlComboBox.ItemsSource = Enum.GetValues<AlphaNvencRateControlMode>();
        AlphaProfileComboBox.ItemsSource = Enum.GetValues<AlphaNvencProfile>();
        AlphaLevelComboBox.ItemsSource = Enum.GetValues<AlphaNvencLevel>();

        FormatComboBox.SelectedItem = ResolveEncoderOption(selectedEncoderOption.Kind);
        LoadEncoderSettingsIntoUi();

        _isInitialized = true;
        UpdateEncoderSettingsReadouts();
        ApplyEncoderSettingsVisibility();
    }

    internal EncoderOption SelectedEncoderOption => (EncoderOption)(FormatComboBox.SelectedItem ?? _encoderOptions[0]);

    internal EncoderSettingsRoot Settings => _encoderSettings.Clone();

    private void ApplyButton_OnClick(object sender, RoutedEventArgs e)
    {
        _encoderSettings = CaptureEncoderSettingsFromUi();
        _encoderSettings.SelectedEncoderKind = SelectedEncoderOption.Kind;
        DialogResult = true;
        Close();
    }

    private void CancelButton_OnClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void ResetButton_OnClick(object sender, RoutedEventArgs e) => ResetToDefaults();

    private void ResetEncoderSettingsButton_OnClick(object sender, RoutedEventArgs e) => ResetToDefaults();

    private void FormatComboBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isInitialized)
        {
            return;
        }

        var option = SelectedEncoderOption;
        FormatDescriptionTextBlock.Text = option.Description;
        ApplyEncoderSettingsVisibility();
    }

    private void EncoderSettingsSelection_OnChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isInitialized)
        {
            return;
        }

        ApplyEncoderSettingsVisibility();
    }

    private void EncoderSettingsToggle_OnChanged(object sender, RoutedEventArgs e)
    {
        if (!_isInitialized)
        {
            return;
        }

        ApplyEncoderSettingsVisibility();
    }

    private void EncoderSettingsSlider_OnChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_isInitialized)
        {
            return;
        }

        UpdateEncoderSettingsReadouts();
        ApplyEncoderSettingsVisibility();
    }

    private void ResetToDefaults()
    {
        _encoderSettings = EncoderSettingsRoot.CreateDefaults();
        FormatComboBox.SelectedItem = ResolveEncoderOption(_encoderSettings.SelectedEncoderKind);
        LoadEncoderSettingsIntoUi();
        UpdateEncoderSettingsReadouts();
        ApplyEncoderSettingsVisibility();
    }

    private void LoadEncoderSettingsIntoUi()
    {
        FormatDescriptionTextBlock.Text = SelectedEncoderOption.Description;

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
        EncoderSettingsSectionBorder.Visibility = showEncoderSettings ? Visibility.Visible : Visibility.Collapsed;
        PngMovNoteBorder.Visibility = showEncoderSettings ? Visibility.Collapsed : Visibility.Visible;

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
        AlphaTargetBitratePanel.Visibility = alphaRateControl is AlphaNvencRateControlMode.Vbr or AlphaNvencRateControlMode.Cbr
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
        if (!_isInitialized)
        {
            return;
        }

        RgbQualityVsSpeedValueTextBlock.Text = ReadSlider(RgbQualityVsSpeedSlider, 16).ToString(CultureInfo.InvariantCulture);
        RgbQualityValueTextBlock.Text = ReadSlider(RgbQualitySlider, 70).ToString(CultureInfo.InvariantCulture);
        AlphaPresetValueTextBlock.Text = $"P{ReadSlider(AlphaPresetSlider, 3)} / {DescribeAlphaPreset(ReadSlider(AlphaPresetSlider, 3))}";
        AlphaConstantQualityValueTextBlock.Text = ReadSlider(AlphaConstantQualitySlider, 19).ToString(CultureInfo.InvariantCulture);
        AlphaConstantQpValueTextBlock.Text = ReadSlider(AlphaConstantQpSlider, 23).ToString(CultureInfo.InvariantCulture);
    }

    private static int ReadInt(TextBox textBox, int fallback)
    {
        var text = textBox.Text?.Trim();
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;
    }

    private static int ReadSlider(Slider slider, int fallback)
    {
        var value = (int)Math.Round(slider.Value);
        return value >= 0 ? value : fallback;
    }

    private static TEnum SelectedEnum<TEnum>(ComboBox comboBox, TEnum fallback)
        where TEnum : struct, Enum
    {
        return comboBox.SelectedItem is TEnum selected ? selected : fallback;
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
}
