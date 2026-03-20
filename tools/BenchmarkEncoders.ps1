param(
    [Parameter(Mandatory = $true)]
    [string]$SourcePath,

    [string]$OutputRoot = "",

    [switch]$IncludeSlow,

    [ValidateSet("container", "raw-bgra")]
    [string]$SourceKind = "container",

    [int]$Width = 0,

    [int]$Height = 0,

    [double]$FrameRate = 60.0,

    [int]$LoopCount = 0,

    [string[]]$OnlyCases = @()
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $SourcePath)) {
    throw "Source file not found: $SourcePath"
}

if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $env:LOCALAPPDATA ("SpoutDirectSaver\EncoderBench\" + (Get-Date -Format "yyyyMMdd_HHmmss"))
}

New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null

$loopArgs = @()
if ($LoopCount -gt 0) {
    $loopArgs = @("-stream_loop", $LoopCount.ToString())
}

if ($SourceKind -eq "container") {
    $duration = [double](& ffprobe -v error -show_entries format=duration -of default=nk=1:nw=1 $SourcePath)
    if ($duration -le 0) {
        throw "Unable to probe source duration."
    }

    $duration *= ($LoopCount + 1)
    $inputArgs = @($loopArgs + @("-i", $SourcePath))
}
else {
    if ($Width -le 0 -or $Height -le 0 -or $FrameRate -le 0) {
        throw "Width, Height, and FrameRate are required for raw-bgra input."
    }

    $frameBytes = [double]$Width * [double]$Height * 4.0
    $duration = ((Get-Item -LiteralPath $SourcePath).Length / $frameBytes) / $FrameRate
    $duration *= ($LoopCount + 1)
    $inputArgs = @(
        $loopArgs +
        @(
            "-f", "rawvideo",
            "-pixel_format", "bgra",
            "-video_size", ("{0}x{1}" -f $Width, $Height),
            "-framerate", $FrameRate.ToString([System.Globalization.CultureInfo]::InvariantCulture),
            "-i", $SourcePath
        )
    )
}

$cases = @(
    [pscustomobject]@{
        Name = "png_mov"
        RelativeOutput = "png.mov"
        Args = @(
            "-y"
        ) + $inputArgs + @(
            "-an",
            "-c:v", "png",
            "-pred", "mixed",
            "-pix_fmt", "rgba",
            "-movflags", "+faststart"
        )
    },
    [pscustomobject]@{
        Name = "ffv1_mkv"
        RelativeOutput = "ffv1.mkv"
        Args = @(
            "-y"
        ) + $inputArgs + @(
            "-an",
            "-c:v", "ffv1",
            "-level", "3",
            "-coder", "1",
            "-context", "1",
            "-g", "1",
            "-slicecrc", "1",
            "-pix_fmt", "bgra",
            "-cues_to_front", "1"
        )
    },
    [pscustomobject]@{
        Name = "prores_4444_mov"
        RelativeOutput = "prores4444.mov"
        Args = @(
            "-y"
        ) + $inputArgs + @(
            "-an",
            "-c:v", "prores_ks",
            "-profile:v", "4444",
            "-pix_fmt", "yuva444p10le",
            "-alpha_bits", "8"
        )
    },
    [pscustomobject]@{
        Name = "hap_alpha_mov"
        RelativeOutput = "hap_alpha.mov"
        Args = @(
            "-y"
        ) + $inputArgs + @(
            "-an",
            "-c:v", "hap",
            "-format", "hap_alpha",
            "-chunks", "8",
            "-compressor", "snappy"
        )
    },
    [pscustomobject]@{
        Name = "cfhd_mov"
        RelativeOutput = "cfhd.mov"
        Args = @(
            "-y"
        ) + $inputArgs + @(
            "-an",
            "-c:v", "cfhd",
            "-quality", "high",
            "-pix_fmt", "gbrap12le"
        )
    },
    [pscustomobject]@{
        Name = "split_hevc_nvenc_ffv1alpha_mkv"
        RelativeOutput = "split_hevc_ffv1alpha.mkv"
        Args = @(
            "-y"
        ) + $inputArgs + @(
            "-an",
            "-filter_complex", "[0:v]split=2[rgb][alpha];[alpha]alphaextract,format=gray[aout]",
            "-map", "[rgb]",
            "-c:v:0", "hevc_nvenc",
            "-preset:v:0", "p1",
            "-tune:v:0", "ll",
            "-rc:v:0", "vbr",
            "-cq:v:0", "23",
            "-b:v:0", "0",
            "-g:v:0", "60",
            "-pix_fmt:v:0", "yuv444p",
            "-profile:v:0", "rext",
            "-map", "[aout]",
            "-c:v:1", "ffv1",
            "-level:v:1", "3",
            "-coder:v:1", "1",
            "-context:v:1", "1",
            "-g:v:1", "1",
            "-slicecrc:v:1", "1",
            "-pix_fmt:v:1", "gray"
        )
    },
    [pscustomobject]@{
        Name = "split_hevc420_nvenc_ffv1alpha_mkv"
        RelativeOutput = "split_hevc420_ffv1alpha.mkv"
        Args = @(
            "-y"
        ) + $inputArgs + @(
            "-an",
            "-filter_complex", "[0:v]split=2[rgb][alpha];[alpha]alphaextract,format=gray[aout]",
            "-map", "[rgb]",
            "-c:v:0", "hevc_nvenc",
            "-preset:v:0", "p1",
            "-tune:v:0", "ll",
            "-rc:v:0", "vbr",
            "-cq:v:0", "21",
            "-b:v:0", "0",
            "-g:v:0", "60",
            "-pix_fmt:v:0", "yuv420p",
            "-profile:v:0", "main",
            "-map", "[aout]",
            "-c:v:1", "ffv1",
            "-level:v:1", "3",
            "-coder:v:1", "1",
            "-context:v:1", "1",
            "-g:v:1", "1",
            "-slicecrc:v:1", "1",
            "-pix_fmt:v:1", "gray"
        )
    },
    [pscustomobject]@{
        Name = "split_h264_nvenc_ffv1alpha_mkv"
        RelativeOutput = "split_h264_ffv1alpha.mkv"
        Args = @(
            "-y"
        ) + $inputArgs + @(
            "-an",
            "-filter_complex", "[0:v]split=2[rgb][alpha];[alpha]alphaextract,format=gray[aout]",
            "-map", "[rgb]",
            "-c:v:0", "h264_nvenc",
            "-preset:v:0", "p1",
            "-tune:v:0", "ll",
            "-rc:v:0", "vbr",
            "-cq:v:0", "20",
            "-b:v:0", "0",
            "-g:v:0", "60",
            "-pix_fmt:v:0", "yuv444p",
            "-profile:v:0", "high444p",
            "-map", "[aout]",
            "-c:v:1", "ffv1",
            "-level:v:1", "3",
            "-coder:v:1", "1",
            "-context:v:1", "1",
            "-g:v:1", "1",
            "-slicecrc:v:1", "1",
            "-pix_fmt:v:1", "gray"
        )
    }
    ,
    [pscustomobject]@{
        Name = "split_h264420_nvenc_ffv1alpha_mkv"
        RelativeOutput = "split_h264420_ffv1alpha.mkv"
        Args = @(
            "-y"
        ) + $inputArgs + @(
            "-an",
            "-filter_complex", "[0:v]split=2[rgb][alpha];[alpha]alphaextract,format=gray[aout]",
            "-map", "[rgb]",
            "-c:v:0", "h264_nvenc",
            "-preset:v:0", "p1",
            "-tune:v:0", "ll",
            "-rc:v:0", "vbr",
            "-cq:v:0", "19",
            "-b:v:0", "0",
            "-g:v:0", "60",
            "-pix_fmt:v:0", "yuv420p",
            "-profile:v:0", "high",
            "-map", "[aout]",
            "-c:v:1", "ffv1",
            "-level:v:1", "3",
            "-coder:v:1", "1",
            "-context:v:1", "1",
            "-g:v:1", "1",
            "-slicecrc:v:1", "1",
            "-pix_fmt:v:1", "gray"
        )
    }
)

if ($IncludeSlow) {
    $cases += [pscustomobject]@{
        Name = "vp9_alpha_webm"
        RelativeOutput = "vp9_alpha.webm"
        Args = @(
            "-y"
        ) + $inputArgs + @(
            "-an",
            "-c:v", "libvpx-vp9",
            "-pix_fmt", "yuva420p",
            "-row-mt", "1",
            "-tile-columns", "2",
            "-frame-parallel", "1",
            "-deadline", "realtime",
            "-cpu-used", "8",
            "-b:v", "0",
            "-crf", "30"
        )
    }
}

if ($OnlyCases.Count -gt 0) {
    $requested = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($name in $OnlyCases) {
        [void]$requested.Add($name)
    }

    $cases = @($cases | Where-Object { $requested.Contains($_.Name) })
    if ($cases.Count -eq 0) {
        throw "No benchmark cases matched OnlyCases."
    }
}

$results = @()

foreach ($case in $cases) {
    $outputPath = Join-Path $OutputRoot $case.RelativeOutput
    $logPath = Join-Path $OutputRoot ($case.Name + ".log")
    $stdoutPath = Join-Path $OutputRoot ($case.Name + ".stdout.log")
    $stderrPath = Join-Path $OutputRoot ($case.Name + ".stderr.log")

    if (Test-Path -LiteralPath $outputPath) {
        Remove-Item -LiteralPath $outputPath -Force
    }
    if (Test-Path -LiteralPath $logPath) {
        Remove-Item -LiteralPath $logPath -Force
    }
    if (Test-Path -LiteralPath $stdoutPath) {
        Remove-Item -LiteralPath $stdoutPath -Force
    }
    if (Test-Path -LiteralPath $stderrPath) {
        Remove-Item -LiteralPath $stderrPath -Force
    }

    Write-Host ("Running {0} -> {1}" -f $case.Name, $outputPath)
    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    $process = Start-Process `
        -FilePath "ffmpeg" `
        -ArgumentList ($case.Args + @($outputPath)) `
        -NoNewWindow `
        -Wait `
        -PassThru `
        -RedirectStandardOutput $stdoutPath `
        -RedirectStandardError $stderrPath
    $stopwatch.Stop()
    $exitCode = $process.ExitCode
    $stderr = if (Test-Path -LiteralPath $stderrPath) { Get-Content -LiteralPath $stderrPath -Raw } else { "" }
    $stdout = if (Test-Path -LiteralPath $stdoutPath) { Get-Content -LiteralPath $stdoutPath -Raw } else { "" }
    ($stderr + [Environment]::NewLine + $stdout) | Set-Content -LiteralPath $logPath -Encoding UTF8

    $result = [ordered]@{
        name = $case.Name
        output = $outputPath
        log = $logPath
        exit_code = $exitCode
        elapsed_seconds = [math]::Round($stopwatch.Elapsed.TotalSeconds, 3)
        realtime_factor = [math]::Round($duration / $stopwatch.Elapsed.TotalSeconds, 3)
    }

    if ($exitCode -eq 0 -and (Test-Path -LiteralPath $outputPath)) {
        $file = Get-Item -LiteralPath $outputPath
        $probe = & ffprobe -v error `
            -show_entries stream=index,codec_name,pix_fmt,avg_frame_rate,width,height:format=duration,size `
            -of json $outputPath | ConvertFrom-Json

        $result.file_bytes = $file.Length
        $result.file_megabytes = [math]::Round($file.Length / 1MB, 2)
        $result.duration_seconds = [double]$probe.format.duration
        $result.streams = @(
            $probe.streams | ForEach-Object {
                [ordered]@{
                    index = $_.index
                    codec = $_.codec_name
                    pix_fmt = $_.pix_fmt
                    width = $_.width
                    height = $_.height
                    avg_frame_rate = $_.avg_frame_rate
                }
            }
        )
    }
    else {
        $result.error_tail = (Get-Content -LiteralPath $logPath -Tail 20) -join "`n"
    }

    $results += [pscustomobject]$result
}

$summaryPath = Join-Path $OutputRoot "summary.json"
$results | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $summaryPath -Encoding UTF8

$results | Format-Table -AutoSize | Out-String | Write-Host
Write-Host "SUMMARY_JSON=$summaryPath"
