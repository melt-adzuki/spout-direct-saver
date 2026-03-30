# AGENTS

## Mission

This repository is a Windows-focused Spout capture tool for recording a sender as an asset.

Treat it as:

- a direct recorder, not a scene compositor
- transparency-aware by design
- optimized around practical recording throughput and review loops

## Read Order

Read these first, in order:

1. `README.md`
2. `docs/README.md`
3. `docs/ImplementationMap.md`
4. `docs/DecisionHistory.md`

## Repository Layout

- `SpoutDirectSaver.App`
  - production app
- `SpoutDirectSaver.E2E`
  - end-to-end harness
- `SpoutDirectSaver.TestSender`
  - synthetic sender
- `SpoutDirectSaver.Benchmarks`
  - benchmark project
- `docs`
  - engineering documentation

Local comparison/reference trees:

- `.tmp-obs-studio`
- `.tmp-obs-spout2-plugin`
- `.tmp-spout2`

These are reference sources, not product code.

## Core Components

- `MainWindow.xaml.cs`
  - UI orchestration
- `SpoutPollingService.cs`
  - sender discovery, polling, frame delivery, recording-mode switching
- `D3D11SpoutSharedTextureReader.cs`
  - shared-handle open, GPU capture, CPU readback, alpha extraction
- `RecordingSession.cs`
  - timeline, frame duration accounting, writer dispatch, finalization
- `RealtimeRgbNvencWriter.cs`
  - realtime RGB queue and GPU copy staging
- `MediaFoundationHevcWriter.cs`
  - HEVC writer and sample submission
- `D3D11Nv12TextureConverter.cs`
  - GPU color conversion for encode input
- `VideoExportService.cs`
  - export/finalization path

## Project-Wide Invariants

### Keep the README user-facing

`README.md` should explain:

- what the tool is for
- how to run it
- how to use it

Implementation details belong in `docs/`, not in `README.md`.

### Keep current-state engineering guidance in `docs/README.md`

The main engineering guide lives in:

- `docs/README.md`

Do not re-split high-level engineering guidance across multiple overview files unless there is a clear need.

### Keep historical rationale in one place

Historical reasoning and architectural tradeoff history belong in:

- `docs/DecisionHistory.md`

Do not spread design history across multiple docs again.

### Keep preview and recording separate

Preview is for usability.  
Recording is for correctness and throughput.

Do not let preview requirements force extra work into the recording hot path.

### Shared-texture senders should prefer the GPU path

For suitable senders, the intended direction is:

- keep RGB texture-native as long as possible
- avoid per-frame parent-process full BGRA readback as the main route
- read back only what must be on the CPU side

### RGB and alpha are allowed to take different paths

Do not assume they must share the same representation or storage strategy.

That separation is intentional and central to the project design.

### Orientation and color are fragile and path-specific

If output looks wrong, inspect first:

- `D3D11SpoutSharedTextureReader.cs`
- `D3D11Nv12TextureConverter.cs`
- `MediaFoundationHevcWriter.cs`

Common failure classes:

- fullscreen-blit UV mistakes
- wrong assumptions about texture origin
- missing BT.709 / nominal-range metadata
- RGB/YUV contract mismatches

### Timing issues often begin before the final writer

`RecordingSession` decides frame durations and repeat counts.

If the realtime path falls behind:

- bounded queues can stall capture cadence
- sparse source sampling can later look like temporal disorder

Do not assume Media Foundation is the first cause just because the final output looks wrong.

### Synthetic sender results are necessary but not sufficient

`SpoutDirectSaver.TestSender` is excellent for reproducible tests, but real senders can still behave differently.

After changes affecting:

- shared-texture initialization
- pacing
- orientation
- color
- timing under load

validate on both synthetic and real sender conditions when possible.

### App and E2E share source files explicitly

`SpoutDirectSaver.E2E` links files from `SpoutDirectSaver.App`.

If you add or move shared services/models used by E2E, update the E2E `.csproj` as needed.

### Visual validation is mandatory for media changes

If a change touches:

- color
- orientation
- alpha behavior
- frame pacing
- finalization behavior

do not rely on build success or counters alone.  
Inspect an actual output file.

## Validation Baseline

After meaningful recording-path changes:

1. build `SpoutDirectSaver.App`
2. build `SpoutDirectSaver.E2E`
3. run a short E2E pass
4. visually inspect output if media behavior changed

Useful command:

```powershell
dotnet run --project .\SpoutDirectSaver.E2E\SpoutDirectSaver.E2E.csproj -- --launch-test-sender --test-sender-scene AlphaStress --seconds 5
```

## Useful Environment Variables

- `SPOUT_DIRECT_SAVER_TRACE_PATH`
- `SPOUT_DIRECT_SAVER_KEEP_TEMP`
- `SPOUT_DIRECT_SAVER_CACHE_ROOT`
- `SPOUT_DIRECT_SAVER_SPOOL_COMPRESSION`
- `SPOUT_DIRECT_SAVER_DISABLE_HYBRID_RGB`
- `SPOUT_DIRECT_SAVER_DISABLE_HYBRID_ALPHA`
- `SPOUT_DIRECT_SAVER_DISABLE_MAIN_WRITER`
- `SPOUT_DIRECT_SAVER_DISABLE_ALPHA_WRITER`

Use them for targeted debugging, not as permanent behavioral assumptions.

## Documentation Maintenance Rule

If you change the current engineering mental model, update:

- `docs/README.md`
- `docs/ImplementationMap.md`

If you change why the architecture exists, update:

- `docs/DecisionHistory.md`
