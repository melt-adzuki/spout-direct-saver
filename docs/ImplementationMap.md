# Implementation Map

## Application Layer

### `SpoutDirectSaver.App/MainWindow.xaml`
### `SpoutDirectSaver.App/MainWindow.xaml.cs`

Owns:

- sender-facing UI
- preview display
- record / stop / re-record flow
- wiring `SpoutPollingService` to `RecordingSession`

Important notes:

- preview lifecycle and recording lifecycle are not the same thing
- ordering around record start/stop matters because event subscriptions and mode switches can race

## Capture Control

### `SpoutDirectSaver.App/Services/SpoutPollingService.cs`

Owns:

- sender discovery and polling loop
- preview frame delivery
- recording frame delivery
- recording-mode transitions
- shared-texture vs CPU-path selection

When debugging:

- missing frames during recording often start here
- unexpected fallback behavior often starts here

### `SpoutDirectSaver.App/Services/WindowsScheduling.cs`

Owns:

- process-priority promotion
- MMCSS enrollment for capture / writer threads
- process-lifetime timer-resolution hints
- privilege enablement needed for optional GPU scheduling requests

### `SpoutDirectSaver.App/Services/WindowsGraphicsScheduling.cs`

Owns:

- D3D11 `SetMaximumFrameLatency(16)` requests
- DXGI GPU thread priority requests
- D3DKMT process GPU scheduling-class requests

These hints are best-effort and should be treated as support code around the hot path, not the hot path itself.

## Shared-Texture Bridge

### `SpoutDirectSaver.App/Services/D3D11SpoutSharedTextureReader.cs`

Owns:

- opening the shared handle as `ID3D11Texture2D`
- CPU readback path
- GPU capture path for recording
- alpha extraction path
- orientation-sensitive fullscreen blits

This file is the first place to inspect for:

- texture orientation problems
- format mismatches
- shared-handle initialization failures

## Timeline and Session Logic

### `SpoutDirectSaver.App/Services/RecordingSession.cs`

Owns:

- recording session state
- timeline duration accounting
- frame deduplication / reuse behavior
- dispatch to RGB and alpha writers
- spool bookkeeping
- finalization and export coordination

This is the main place where:

- frame duration is finalized
- repeat counts are computed
- backpressure side effects become visible at the output timeline level

## RGB Writers

### `SpoutDirectSaver.App/Services/RealtimeRgbNvencWriter.cs`

Owns:

- bounded realtime RGB queue
- GPU copy staging ring
- dispatch to the HEVC writer

### `SpoutDirectSaver.App/Services/MediaFoundationHevcWriter.cs`

Owns:

- Media Foundation sink writer setup
- CPU BGRA submission path
- GPU NV12 submission path
- sample time generation for CFR output submission

### `SpoutDirectSaver.App/Services/D3D11Nv12TextureConverter.cs`

Owns:

- GPU `BGRA -> NV12` conversion
- D3D11 video-processor setup
- color-space configuration for GPU encode input

Inspect these first for:

- color shifts
- range mismatches
- GPU encode contract failures
- output-orientation issues after the shared-texture stage

## Alpha Path

### `SpoutDirectSaver.App/Services/RealtimeGrayFfv1Writer.cs`

Owns:

- realtime grayscale alpha writer

### `SpoutDirectSaver.App/Services/VideoExportService.cs`

Owns:

- spool reading
- decompression and reconstruction
- final export steps

## Shared Model Types

### `SpoutDirectSaver.App/Models/FramePacket.cs`

- transport object from capture to recording

### `SpoutDirectSaver.App/Models/GpuTextureFrame.cs`

- GPU frame lease and release callback wrapper

### `SpoutDirectSaver.App/Models/PixelBufferLease.cs`

- ref-counted CPU buffer

### `SpoutDirectSaver.App/Models/RecordedFrame.cs`

- session timeline metadata

## E2E and Synthetic Sender

### `SpoutDirectSaver.E2E`

- drives the app pipeline through reproducible end-to-end scenarios

### `SpoutDirectSaver.TestSender`

- provides controlled scenes such as `Simple`, `Complex`, and `AlphaStress`

Use `AlphaStress` when validating:

- alpha behavior
- color handling
- orientation
- frame-timing regressions under motion

## Cross-Project Code Sharing

`SpoutDirectSaver.E2E` compiles selected files from `SpoutDirectSaver.App` via linked `Compile Include` entries.

That means:

- adding a new shared service can require updating the E2E `.csproj`
- build success in `SpoutDirectSaver.App` alone is not enough after shared-service changes
