# Spout Direct Saver

Spout Direct Saver is a Windows desktop tool for recording a Spout sender as a reusable asset.

It is designed for workflows where you want to:

- capture a single Spout source directly
- preserve transparency when needed
- review the take immediately after recording
- iterate quickly with record / stop / re-record

This tool is not trying to replace OBS as a full production studio.  
It is focused on direct asset capture from Spout senders, especially when transparent output matters.

## Requirements

- Windows
- .NET 9 SDK
- `ffmpeg.exe` available on `PATH`
- NVIDIA GPU with NVENC support for the recommended recording mode

## Quick Start

Launch the app:

```powershell
dotnet run --project .\SpoutDirectSaver.App\SpoutDirectSaver.App.csproj
```

## Typical Workflow

1. Start your Spout sender.
2. Launch Spout Direct Saver.
3. Wait for the sender to appear in the app.
4. Click `Start Recording`.
5. Click `Stop Recording` when the take is done.
6. Review the recorded output in the built-in preview.
7. Click `Re-record` for the next take.

## Output

The app supports multiple recording formats.  
For most users, the recommended mode is the default one exposed by the app.

Depending on the selected mode, the recording may be saved as:

- a main video file
- a main video file plus an alpha sidecar file
- a single lossless file

## Validation Tools

The repository also includes:

- `SpoutDirectSaver.TestSender`
  - a synthetic Spout sender for local testing
- `SpoutDirectSaver.E2E`
  - an end-to-end test harness

Example:

```powershell
dotnet run --project .\SpoutDirectSaver.E2E\SpoutDirectSaver.E2E.csproj -- --launch-test-sender --test-sender-scene AlphaStress --seconds 5
```

## Internal Documentation

Implementation and contributor-facing documentation lives under [docs](./docs/README.md).
