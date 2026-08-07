# JellySTRMprobe - Project Context

## Project Overview

A standalone Jellyfin plugin that extracts media information (codec, resolution, duration, audio) from STRM file targets by probing remote streams. Jellyfin never probes STRM files during library scans — this plugin fills that gap. It also generates trickplay scrubbing previews for STRM items, which Jellyfin skips by design.

## Project Structure

```
JellySTRMprobe/
├── JellySTRMprobe/                          # Main plugin project
│   ├── JellySTRMprobe.csproj
│   ├── Plugin.cs                            # Plugin entry point
│   ├── PluginConfiguration.cs               # Settings model
│   ├── PluginServiceRegistrator.cs          # DI registration
│   ├── Service/
│   │   ├── IProbeService.cs / ProbeService.cs      # Core probing logic
│   │   ├── ProbeResult.cs                          # Batch probe outcome
│   │   └── ITrickplayService.cs / TrickplayService.cs  # Core trickplay logic
│   ├── Tasks/
│   │   ├── ProbeStrmTask.cs                 # Scheduled task (daily 4 AM)
│   │   └── StrmTrickplayTask.cs             # Scheduled task (manual, no trigger)
│   ├── EntryPoint/
│   │   └── CatchUpEntryPoint.cs             # Auto-probe new items
│   └── Configuration/Web/
│       ├── strmprobe.html                   # Config UI
│       └── strmprobe.js                     # Config UI logic
├── JellySTRMprobe.Tests/                    # Unit tests
│   ├── JellySTRMprobe.Tests.csproj
│   ├── Service/
│   │   ├── ProbeServiceTests.cs
│   │   └── TrickplayServiceTests.cs
│   └── Tasks/
│       ├── ProbeStrmTaskTests.cs
│       └── StrmTrickplayTaskTests.cs
├── docs/                                    # Documentation
│   ├── REQUIREMENTS.md
│   ├── ARCHITECTURE.md
│   └── TESTCASES.md
├── jellyfin.ruleset                         # Code analysis rules
└── JellySTRMprobe.sln                       # Solution file
```

## Build & Test

```bash
# Build
dotnet build -c Release

# Run tests
dotnet test -c Release

# Publish for deployment
dotnet publish JellySTRMprobe -c Release -o /tmp/jellystrm-release
```

## Release Process

### 1. Update Version
Edit `JellySTRMprobe/JellySTRMprobe.csproj`:
```xml
<AssemblyVersion>X.Y.Z.0</AssemblyVersion>
<FileVersion>X.Y.Z.0</FileVersion>
```

### 2. Build Release
```bash
dotnet publish JellySTRMprobe -c Release -o /tmp/jellystrm-release
cd /tmp/jellystrm-release
zip -j /tmp/jellystrm-probe_X.Y.Z.0.zip JellySTRMprobe.dll
```

### 3. Deploy to Jellyfin
Copy `JellySTRMprobe.dll` to Jellyfin's plugin directory and restart.

## Core Mechanism

### Probing
The plugin calls `IProviderManager.RefreshSingleItem()` with `EnableRemoteContentProbe = true` — the same flag Jellyfin sets during playback. This triggers ffprobe against the STRM target URL without requiring the user to play the content first.

### Trickplay
`BaseVideoResolver` marks every `.strm` as `IsShortcut`, and `TrickplayManager.CanGenerateTrickplay()` rejects shortcuts — the first check inside `RefreshTrickplayDataAsync()`. `TrickplayService` calls the same pipeline directly (`IMediaEncoder.ExtractVideoImagesOnIntervalAccelerated` → `ITrickplayManager.CreateTiles` → `SaveTrickplayInfo`), bypassing that gate. Two non-obvious details:

- `BaseItem.GetVersionInfo()` already rewrites a shortcut's `MediaSourceInfo.Path` to `Video.ShortcutPath` (the stream URL) with the protocol set — so ffmpeg gets a URL, never the `.strm` path, and no ffmpeg wrapper is needed.
- `CreateTiles()` does **not** set `TrickplayInfo.ItemId`. It must be assigned before `SaveTrickplayInfo()`, or the tiles are orphaned on disk.

## Gotchas

- **`InternalItemsQuery` on 10.11.x:** `TopParentIds` and `MediaTypes` filters silently return 0 results (EF Core). Use `AncestorIds`, which goes through a join table and works. Both services do this.
- **Analyzers:** `TreatWarningsAsErrors` + `AnalysisMode=AllEnabledByDefault` + StyleCop, and there is no `NoWarn`. No file headers (SA1633 off), XML docs on every public member (CS1591), constant log templates with PascalCase placeholders (CA2254/CA1727 are errors), `.ConfigureAwait(false)` everywhere, `CultureInfo.InvariantCulture` on formatting (CA1305 is an error).
- **Never log stream URLs** — STRM targets routinely embed provider credentials.

## Target Framework
- .NET 9.0
- Jellyfin 10.11.0+

## Key Dependencies
- Jellyfin.Controller 10.11.0
- Jellyfin.Model 10.11.0

## Related Projects

| Project | Purpose |
|---------|---------|
| [Jellyfin-Xtream-Library](../Jellyfin-Xtream-Library/) | Sibling plugin — generates the STRM files this plugin probes |
| [StrmAssistant](https://github.com/sjtuross/StrmAssistant) | Emby equivalent (inspiration, not compatible) |
| [JellyfinStrmExtract](https://github.com/gauthier-th/JellyfinStrmExtract) | Basic Jellyfin predecessor (targets 10.9.x) |
