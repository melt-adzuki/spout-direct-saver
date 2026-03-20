# Realtime Encoding Benchmarks

This note summarizes local experiments around replacing the current heavy temporary spool with realtime FFmpeg encoding for 4K / 60fps RGBA capture.

## Why the current spool path is fragile

RGBA 8-bit at 3840x2160 is about 33 MB per frame.

- 3840 x 2160 x 4 bytes = 33,177,600 bytes/frame
- 60 fps = about 1.99 GB/s of raw write traffic

That is too close to or beyond what many SSDs can sustain once compression, file metadata, and other app work are included. Even when the cache drive is fast enough, it is still an unnecessarily tight design.

## Candidate families

### Single-file alpha codecs

- `PNG / MOV`
- `FFV1 / MKV`
- `ProRes 4444 / MOV`
- `Hap Alpha / MOV`
- `CineForm / MOV`
- `VP9 alpha / WebM`

### Split RGB + alpha

- RGB main stream: `hevc_nvenc` or `h264_nvenc`
- Alpha stream: grayscale `ffv1`
- Packaging:
  - one `mkv` with two video streams
  - or one preview-friendly RGB file plus one alpha sidecar file

The split design is attractive because the alpha plane is only 1 channel, and because the expensive color stream can move to NVENC.

## Benchmark method

Tools:

- `tools/BenchmarkEncoders.ps1`

Input sample:

- 5.85-second 3840x2160 RGBA capture taken from the live `VRCSender1` game sender

Two measurement modes were used:

1. Container transcode
   - Input was the captured `png/mov` sample.
   - This includes source decode overhead.
2. Raw BGRA loop
   - The same sample was decoded once to raw `bgra`.
   - The raw sample was then looped to 29.25 seconds.
   - This is much closer to the real app architecture, where raw BGRA frames are already in memory and can be streamed straight into FFmpeg.

## Results

### Container transcode, 5.85 seconds

| Codec path | Alpha preserved | Realtime factor | Output size |
| --- | --- | ---: | ---: |
| `PNG / MOV` | yes | `0.150x` | `2560.03 MB` |
| `FFV1 / MKV` | yes | `0.239x` | `1674.20 MB` |
| `ProRes 4444 / MOV` | yes | `0.139x` | `2867.97 MB` |
| `Hap Alpha / MOV` | yes | `0.526x` | `1244.29 MB` |
| `CineForm / MOV` | yes | `0.396x` | `1023.51 MB` |
| `HEVC NVENC + FFV1 alpha / MKV` | yes | `0.815x` | `51.74 MB` |
| `H264 NVENC + FFV1 alpha / MKV` | yes | `0.817x` | `112.53 MB` |

Takeaway:

- The split RGB/alpha design was already much better even before removing input decode cost.
- The traditional single-file alpha codecs were all far from 4K/60 realtime on this machine.

### Raw BGRA loop, 29.25 seconds

| Codec path | Alpha preserved | Realtime factor | Output size |
| --- | --- | ---: | ---: |
| `Hap Alpha / MOV` | yes | `0.579x` | `6221.46 MB` |
| `CineForm / MOV` | yes | `0.418x` | `5117.53 MB` |
| `HEVC 4:4:4 NVENC + FFV1 alpha / MKV` | yes | `0.935x` | `255.10 MB` |
| `HEVC 4:2:0 NVENC + FFV1 alpha / MKV` | yes | `0.966x` | `254.61 MB` |
| `H264 4:4:4 NVENC + FFV1 alpha / MKV` | yes | `0.935x` | `555.98 MB` |
| `H264 4:2:0 NVENC + FFV1 alpha / MKV` | yes | `0.966x` | `550.03 MB` |

Notes:

- FFmpeg progress logs for the 29.25-second raw run reached about `0.96x` for `HEVC 4:4:4`, about `0.999x` for `HEVC 4:2:0`, and about `1.0x` for `H264 4:2:0`.
- The small gap between the FFmpeg `speed=` value and the summary `realtime_factor` comes mostly from process startup/shutdown overhead.
- A long-lived FFmpeg process that stays open for the whole recording session should behave closer to the in-process `speed=` value than the short-run summary value.

### VP9 alpha

`VP9 alpha / WebM` was also tested from raw BGRA on the 5.85-second sample.

- Realtime factor: `0.413x`
- Output size: `54.49 MB`
- FFmpeg muxed the file as `yuva420p`
- `ffprobe` reported `pix_fmt=yuv420p` with `alpha_mode=1`

Takeaway:

- VP9 alpha is attractive as a compact single-file distribution format.
- It is not a realistic 4K/60 live capture target in this environment.

## Recommendation

The most promising architecture is:

1. Keep a single long-lived FFmpeg process open during recording.
2. Stream raw `bgra` frames directly into FFmpeg over stdin.
3. Split the input inside FFmpeg:
   - color stream -> `hevc_nvenc` or `h264_nvenc`
   - alpha stream -> grayscale `ffv1`
4. Stop writing raw or lightly compressed intermediate frame spools during capture.

Recommended presets:

- Quality-first:
  - `hevc_nvenc` main RGB stream in `yuv444p`
  - grayscale `ffv1` alpha stream
- Safety-first realtime:
  - `hevc_nvenc` or `h264_nvenc` main RGB stream in `yuv420p`
  - grayscale `ffv1` alpha stream

## Packaging tradeoffs

### One MKV with two video streams

Pros:

- single artifact
- simplest FFmpeg pipeline
- preserves alpha cleanly

Cons:

- playback support in generic players and WPF preview paths is less predictable

### RGB file + alpha sidecar

Pros:

- the RGB file can be preview-friendly
- alpha can stay lossless and separate
- easier to keep app preview simple

Cons:

- two files instead of one
- requires a small metadata convention for reconstruction

## Likely next implementation step

If realtime capture performance is the priority, the next practical change is to add an export mode based on:

- RGB main: `HEVC NVENC`
- Alpha sidecar: grayscale `FFV1`

That path removes the storage-bandwidth bottleneck almost entirely while still preserving alpha.
