# Decision History

## Purpose

This file centralizes architectural rationale, major tradeoffs, and notable documentation-worthy changes.

Use it when you need to answer:

- why the current recording model exists
- why a seemingly simpler alternative was not kept
- which large changes materially reshaped the codebase

## Product-Level Direction

### Direct Spout Asset Capture Instead of Scene Composition

The project is intentionally not centered around scene graph composition or general broadcasting workflows.

Reason:

- the primary use case is direct capture of a single Spout sender as an asset
- transparency handling and fast retake loops matter more than broad production features

## Output Strategy

### Hybrid RGB + Alpha Output Model

The project keeps RGB and alpha logically separate in the preferred path.

Reason:

- RGB and alpha have different bandwidth and storage characteristics
- preserving alpha in a practical way is easier when it is not forced through the same realtime path as RGB
- this model aligns better with review and playback needs than forcing everything through one representation

### Single-File Alpha Formats Were Not Chosen as the Main Path

Multiple alpha-preserving single-file formats were considered.

Why they were not made the default:

- they did not provide the same balance of realtime behavior, usability, and output handling for the primary workflow

## Capture Strategy

### Shared-Texture-First Recording for Suitable Senders

The preferred direction is to keep shared-texture senders on a GPU-friendly route.

Reason:

- per-frame full BGRA readback was a major cost center
- preview and recording had been sharing too much work
- recording should not depend on converting every frame into a CPU-owned full-resolution BGRA buffer

### Preview and Recording Path Separation

The project explicitly separates preview work from recording work.

Reason:

- preview introduced unnecessary copy/readback work into the recording path
- stopping preview-only work during recording materially reduced avoidable load

## Encoding Strategy

### Raw Spool as the Main Strategy Was Not Viable

At 4K / 60fps RGBA, raw frame transport and storage pressure were too high for the intended workflow.

Consequence:

- the project moved toward lighter or more GPU-native RGB paths
- alpha and RGB were allowed to diverge architecturally

### RGB GPU Path Through Media Foundation

The current GPU RGB path uses Media Foundation rather than a custom direct NVENC implementation.

Reason at the time:

- it was the shortest practical path away from parent-process raw BGRA pipe transport
- it allowed GPU texture submission without first building a full custom NVENC session manager

Important note:

- this was a pragmatic implementation choice, not a claim that Media Foundation is inherently superior to an OBS-style direct NVENC path

## Color and Orientation

### Explicit Color-Space and Range Configuration Became Necessary

The GPU recording path now explicitly sets color-space assumptions for conversion and encode input.

Reason:

- relying on defaults caused visible color errors
- the fix required coordinating both the GPU `BGRA -> NV12` conversion step and Media Foundation metadata

### Orientation Must Be Corrected at the Shared-Texture Blit Stage

A later regression showed that orientation errors can appear only in RGB output while preview and alpha appear correct.

Reason:

- preview, RGB, and alpha do not all traverse the same number of fullscreen-blit stages
- correcting orientation at the wrong stage can fix one path while double-flipping another

Resulting rule:

- prefer fixing orientation at the earliest shared-texture-to-recording-texture stage

## Timing and Backpressure

### Apparent Final-Video Timing Problems Often Start Upstream

The final writer is not always the root cause of visible temporal problems.

Reason:

- `RecordingSession` owns frame duration accounting and repeat-count generation
- bounded realtime queues can stall capture cadence
- sparse source sampling can later look like final-output timeline disorder

This is why "the output timeline looks wrong" should not automatically be treated as a final-writer-only problem.

## Benchmark and Validation Conclusions

### Synthetic Validation Is Necessary but Not Sufficient

The synthetic sender and E2E harness are essential for reproducibility.

Why this still is not enough:

- real senders can differ in pacing, share-mode behavior, and practical timing
- some bugs only appear in the GUI lifecycle or with real senders

### Counters Alone Are Not the Final Truth

The repository uses receive-side, record-side, and content-analysis metrics.

Reason this matters:

- counters can miss visible media issues
- color, orientation, and some timing regressions still require visual verification

## Documentation Changes

### Documentation Was Split into User Docs, Engineering Docs, and Agent Onboarding

The repository moved to:

- user-facing `README.md`
- engineering docs in `docs/`
- `AGENTS.md` for fast agent onboarding

Reason:

- previous documentation mixed user guidance, implementation details, and historical rationale
- coding agents need a fast path to the real architecture
- historical reasoning is more useful when centralized

### Historical Rationale Was Centralized

Architectural history and benchmark-driven rationale were consolidated into this file.

Reason:

- rationale had been scattered across operational and architecture docs
- docs work better when current-state guidance and historical tradeoffs are clearly separated
