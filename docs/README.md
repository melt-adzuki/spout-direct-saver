# Engineering Docs

This directory is for coding agents and maintainers.  
It is intentionally implementation-focused.

## Read Order

1. [ImplementationMap.md](./ImplementationMap.md)
2. [DecisionHistory.md](./DecisionHistory.md)

Use this file as the main high-level guide.  
Use `ImplementationMap.md` for file ownership and code navigation.  
Use `DecisionHistory.md` for rationale and major historical tradeoffs.

## Project Purpose

Spout Direct Saver is a Windows tool for recording a Spout sender as an asset rather than as part of a scene-composition workflow.

The codebase is optimized around:

- direct capture of a single sender
- transparency-aware output workflows
- fast record / stop / review / re-record loops
- minimizing unnecessary work in the parent process during recording

## Repository Layout

- `SpoutDirectSaver.App`
  - production GUI and recording implementation
- `SpoutDirectSaver.E2E`
  - end-to-end validation harness
- `SpoutDirectSaver.TestSender`
  - synthetic sender with reproducible scenes
- `SpoutDirectSaver.Benchmarks`
  - focused benchmark project
- `docs`
  - engineering documentation

Local comparison/reference trees:

- `.tmp-obs-studio`
- `.tmp-obs-spout2-plugin`
- `.tmp-spout2`

These are reference sources for local comparison and should not be treated as product code.

## Runtime Modes

### Idle / Live Preview

The app is connected to a sender and showing preview output.

Primary concerns:

- sender discovery and state updates
- keeping preview responsive
- avoiding needless heavy work when preview does not require it

### Recording

The app is actively capturing and writing output.

Primary concerns:

- preferring the shared-texture GPU path for suitable senders
- avoiding preview-only work in the recording hot path
- keeping RGB and alpha handling separated where that reduces cost
- applying Windows-specific scheduling and D3D11 latency hints on a best-effort basis

### Post-Record Review

The app has stopped recording and is previewing the produced file(s).

Primary concerns:

- correct finalization
- stable playback
- fast turnaround into the next take

## Main Pipeline Overview

### Preview Path

`SpoutPollingService`
-> preview frame acquisition
-> `LivePreviewFrame`
-> `MainWindow`

### Shared-Texture Recording Path

`SpoutPollingService`
-> `D3D11SpoutSharedTextureReader`
-> `FramePacket`
-> `RecordingSession`
-> RGB writer
-> alpha HEVC writer / spool

### CPU Recording Path

`SpoutPollingService`
-> CPU frame
-> `RecordingSession`
-> realtime writer or spool/export path

## Core Architectural Points

### Preview and recording must stay separate

Preview is a usability feature.  
Recording is the throughput- and correctness-sensitive path.

Do not let preview requirements dictate the recording hot path.

### Shared-texture senders should stay GPU-friendly

For suitable senders, the preferred direction is:

- keep RGB texture-native as long as possible
- avoid per-frame parent-process full BGRA readback as the main recording route
- read back only what must exist on the CPU side

### RGB and alpha are intentionally allowed to diverge

They do not need to share the same representation or storage strategy.

This separation is one of the main design characteristics of the project.

### Orientation and color are path-sensitive

If media output looks wrong, inspect first:

- `D3D11SpoutSharedTextureReader.cs`
- `D3D11Nv12TextureConverter.cs`
- `MediaFoundationHevcWriter.cs`

Common failure classes:

- fullscreen-blit UV mistakes
- wrong texture-origin assumptions
- missing BT.709 / nominal-range metadata
- RGB/YUV contract mismatches

### Timing issues often start before the final writer

`RecordingSession` owns frame duration accounting and repeat-count generation.

If realtime encode falls behind:

- bounded queues can stall capture cadence
- sparse sampling can later look like temporal disorder in the output

Do not assume the final writer is the first cause just because the symptom is visible in the final file.

### Windows scheduling hints are supportive, not the media pipeline itself

The app now applies best-effort Windows hints such as:

- process and thread priority promotion
- MMCSS task enrollment
- `timeBeginPeriod(1)`
- D3D11 `SetMaximumFrameLatency(16)`
- DXGI / D3DKMT GPU-priority requests

These are intended to reduce avoidable stalls around the hot path.

They do not replace pipeline-level fixes when capture cadence or GPU handoff design is the true bottleneck.

## Build and Run

### Build the app

```powershell
dotnet build .\SpoutDirectSaver.App\SpoutDirectSaver.App.csproj
```

### Build E2E

```powershell
dotnet build .\SpoutDirectSaver.E2E\SpoutDirectSaver.E2E.csproj
```

### Run the GUI

```powershell
dotnet run --project .\SpoutDirectSaver.App\SpoutDirectSaver.App.csproj
```

### Run E2E

```powershell
dotnet run --project .\SpoutDirectSaver.E2E\SpoutDirectSaver.E2E.csproj -- --launch-test-sender --test-sender-scene AlphaStress --seconds 5
```

### Run the synthetic sender directly

```powershell
dotnet run --project .\SpoutDirectSaver.TestSender\SpoutDirectSaver.TestSender.csproj -- --scene AlphaStress --seconds 10
```

## Validation Workflow

After meaningful capture or writer changes:

1. build `SpoutDirectSaver.App`
2. build `SpoutDirectSaver.E2E`
3. run a short E2E pass
4. visually inspect output if the change touched:
   - color
   - orientation
   - alpha
   - frame pacing
   - finalization

Synthetic-sender validation is necessary but not sufficient.  
When possible, also validate against a real sender.

## E2E Metrics

### `receive_only_*`

Use these to estimate the receive-side ceiling before recording pressure dominates.

### `record_*`

Use these to estimate what the recording path saw.

Do not treat them as the final visual truth.

### `content_analysis_exact_signature_*`

Use these as a strict content-based cadence signal.

### `content_analysis_motion_stutter_*`

Use these as a motion-aware cadence signal.

### `content_analysis_experimental_perceptual_*`

Treat these as exploratory only.

## Debugging Aids

### Common environment variables

- `SPOUT_DIRECT_SAVER_TRACE_PATH`
- `SPOUT_DIRECT_SAVER_KEEP_TEMP`
- `SPOUT_DIRECT_SAVER_CACHE_ROOT`
- `SPOUT_DIRECT_SAVER_SPOOL_COMPRESSION`
- `SPOUT_DIRECT_SAVER_DISABLE_HYBRID_RGB`
- `SPOUT_DIRECT_SAVER_DISABLE_HYBRID_ALPHA`
- `SPOUT_DIRECT_SAVER_DISABLE_MAIN_WRITER`
- `SPOUT_DIRECT_SAVER_DISABLE_ALPHA_WRITER`

### Trace session example

```powershell
$env:SPOUT_DIRECT_SAVER_TRACE_PATH = 'D:\Dev\spout-direct-saver\.tmp-gui-probe\gui-trace.log'
$env:SPOUT_DIRECT_SAVER_KEEP_TEMP = '1'
dotnet run --project .\SpoutDirectSaver.App\SpoutDirectSaver.App.csproj
```

## Common First Files to Inspect

### Sender discovery and frame delivery

- `SpoutPollingService.cs`

### Shared-handle and GPU capture issues

- `D3D11SpoutSharedTextureReader.cs`

### RGB writer issues

- `RealtimeRgbNvencWriter.cs`
- `MediaFoundationHevcWriter.cs`
- `D3D11Nv12TextureConverter.cs`

### Export and finalization issues

- `RecordingSession.cs`
- `VideoExportService.cs`

## Documentation Rules

- Keep `README.md` user-facing.
- Keep implementation and operations guidance in this directory.
- Keep historical rationale centralized in `DecisionHistory.md`.
