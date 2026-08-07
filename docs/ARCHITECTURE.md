# JellySTRMprobe — Architecture

## Component Overview

```
┌─────────────────────────────────────────────────────────────┐
│                    Jellyfin Server                          │
│                                                             │
│  ┌───────────────────────────────────────────────────────┐  │
│  │              JellySTRMprobe Plugin                     │  │
│  │                                                       │  │
│  │  ┌──────────────┐    ┌─────────────────────────────┐  │  │
│  │  │   Plugin.cs   │    │  PluginConfiguration.cs     │  │  │
│  │  │  Entry point   │    │  Settings model             │  │  │
│  │  └──────────────┘    └─────────────────────────────┘  │  │
│  │                                                       │  │
│  │  ┌──────────────────────────────────────────────────┐ │  │
│  │  │              ProbeService.cs                      │ │  │
│  │  │  - GetUnprobedItems() — query + filter            │ │  │
│  │  │  - ProbeItemAsync() — single item probe           │ │  │
│  │  │  - ProbeBatchAsync() — parallel batch probe       │ │  │
│  │  └───────────────────┬──────────────────────────────┘ │  │
│  │                      │                                │  │
│  │       ┌──────────────┼────────────────┐               │  │
│  │       │              │                │               │  │
│  │  ┌────▼────┐  ┌──────▼──────┐  ┌─────▼──────────┐    │  │
│  │  │Scheduled│  │  Catch-up   │  │  Config UI     │    │  │
│  │  │  Task   │  │ EntryPoint  │  │  (HTML + JS)   │    │  │
│  │  └─────────┘  └─────────────┘  └────────────────┘    │  │
│  │                                                       │  │
│  └───────────────────────────────────────────────────────┘  │
│                                                             │
│  Jellyfin APIs used:                                        │
│  - IProviderManager.RefreshSingleItem()                     │
│  - ILibraryManager.GetItemList()                            │
│  - ILibraryManager.ItemAdded event                          │
│  - IFileSystem (DirectoryService)                           │
│  - BaseItem.GetMediaStreams()                                │
└─────────────────────────────────────────────────────────────┘
```

## Project Structure

```
JellySTRMprobe/
├── JellySTRMprobe/                          # Main plugin project
│   ├── JellySTRMprobe.csproj
│   ├── Plugin.cs                            # BasePlugin<PluginConfiguration>, IHasWebPages
│   ├── PluginConfiguration.cs               # Settings: parallelism, timeout, cooldown, libraries
│   ├── PluginServiceRegistrator.cs          # DI: services + scheduled tasks
│   ├── Service/
│   │   ├── IProbeService.cs                 # Probe service interface
│   │   ├── ProbeService.cs                  # Core probing logic
│   │   ├── ProbeResult.cs                   # Batch probe outcome
│   │   ├── ITrickplayService.cs             # Trickplay service interface
│   │   └── TrickplayService.cs              # Core trickplay generation logic
│   ├── Tasks/
│   │   ├── ProbeStrmTask.cs                 # IScheduledTask implementation
│   │   └── StrmTrickplayTask.cs             # IScheduledTask implementation
│   ├── EntryPoint/
│   │   └── CatchUpEntryPoint.cs             # IHostedService, ItemAdded handler
│   └── Configuration/Web/
│       ├── strmprobe.html                   # Plugin settings page
│       └── strmprobe.js                     # Settings form logic
├── JellySTRMprobe.Tests/                    # Unit tests
│   ├── JellySTRMprobe.Tests.csproj
│   ├── Service/
│   │   ├── ProbeServiceTests.cs
│   │   └── TrickplayServiceTests.cs
│   └── Tasks/
│       ├── ProbeStrmTaskTests.cs
│       └── StrmTrickplayTaskTests.cs
├── jellyfin.ruleset                         # Code analysis rules
├── JellySTRMprobe.sln                       # Solution file
├── CLAUDE.md                                # Project context for Claude
├── README.md                                # User-facing documentation
└── docs/
    ├── REQUIREMENTS.md                      # Functional & non-functional requirements
    ├── ARCHITECTURE.md                      # This file
    └── TESTCASES.md                         # Test specifications
```

## Core Mechanism

### How Jellyfin Blocks STRM Probing

In Jellyfin's `ProbeProvider.cs`:
```csharp
if (!item.IsShortcut || options.EnableRemoteContentProbe)
```

- STRM files → `IsShortcut = true`
- Library scan → `EnableRemoteContentProbe = false` (default)
- Result: STRM files are skipped during library scan probing

### How This Plugin Bypasses the Gate

```csharp
var refreshOptions = new MetadataRefreshOptions(new DirectoryService(_fileSystem))
{
    EnableRemoteContentProbe = true,       // KEY: enables ffprobe for STRM
    MetadataRefreshMode = MetadataRefreshMode.ValidationOnly,
    ImageRefreshMode = MetadataRefreshMode.ValidationOnly,
    ReplaceAllMetadata = false,            // Don't touch existing metadata
    ReplaceAllImages = false,              // Don't touch existing images
};
await _providerManager.RefreshSingleItem(item, refreshOptions, cancellationToken);
```

This triggers Jellyfin's internal ffprobe pipeline:
1. Reads the URL from the STRM file
2. Runs ffprobe against the remote URL
3. Parses video/audio/subtitle stream info
4. Stores MediaStreams in the database
5. Item now shows codec, resolution, duration, audio channels in the UI

### How Jellyfin Blocks STRM Trickplay

A separate, unrelated gate. In `TrickplayManager.CanGenerateTrickplay()`:

```csharp
if (video.IsShortcut)
{
    return false;
}
```

`CanGenerateTrickplay()` is the first condition inside `RefreshTrickplayDataAsync()`, and
`BaseVideoResolver` sets `IsShortcut = true` for every `.strm`. Consequences:

- The library option *Extract trickplay images* is inert for STRM — ffmpeg is never invoked
- Pre-placed tiles are not imported either: `DiscoverExistingTrickplayAsync()` only runs past that return
- Fixing the duration does not help — the `RunTimeTicks` gate sits **behind** the `IsShortcut` gate

A second gate follows inside `RefreshTrickplayDataInternal`: `if (!File.Exists(mediaPath)) return;`,
which rejects the HTTP URL a STRM resolves to even if the first gate were passed.

### Switching Trickplay Off

`EnableStrmTrickplay` is off by default. Three separate switches are needed, because each
covers a different way the task could start:

| Entry point | Guard | Why it is not redundant |
|---|---|---|
| Shows up in Scheduled Tasks | `IsHidden` | `ScheduledTasksController.GetTasks()` re-reads it on **every** request and `jellyfin-web` asks for `isHidden: false`, so the toggle applies without a restart |
| A trigger the admin added fires | `IsEnabled` | `ScheduledTaskWorker.OnTriggerTriggered()` returns early when false |
| Manual run / direct API call | Guard in `ExecuteAsync` | Goes straight to `QueueScheduledTask`, which consults neither of the above |

`IsHidden`/`IsEnabled` read through `Plugin.InstanceOrNull`, not `Plugin.Instance`. They are
evaluated while the server assembles the task list; a throw there would break the whole
`/ScheduledTasks` endpoint rather than just this one task.

### Two Different Timeouts

Jellyfin already guards against a **stalled** encoder. `ExtractVideoImagesOnIntervalInternal`
polls the output directory every `ImageExtractionTimeoutMs` (server config, default 0 → 20 s):

```csharp
var jpegCount = _fileSystem.GetFilePaths(targetDirectory).Count();
isResponsive = jpegCount > lastCount;
```

No new image in that window → "Trickplay process unresponsive", ffmpeg is stopped, the temp
directory is deleted by `MediaEncoder` itself, and an `FfmpegException` is thrown.

What that does **not** bound is total runtime — an item producing one image every 19 seconds
stays "responsive" forever. `TrickplayTimeoutMinutes` covers that: a linked
`CancellationTokenSource` with `CancelAfter` per item, checked in `GenerateAsync`.

Both the timeout and the task's cancellation handling test the **token**, not the exception type.
Killing ffmpeg raises `FfmpegException`, so a cancellation never arrives as
`OperationCanceledException` and would otherwise be logged as a generation failure.

### How This Plugin Bypasses the Trickplay Gate

`TrickplayService` calls the same pipeline the server uses, without the gates in front of it:

```csharp
imgTempDir = await _mediaEncoder.ExtractVideoImagesOnIntervalAccelerated(
    inputPath, mediaSource.Container, mediaSource, mediaSource.VideoStream, actualWidth, ...);

var info = _trickplayManager.CreateTiles(images, actualWidth, options, outputDir);
info.ItemId = video.Id;                              // CreateTiles does not set this
await _trickplayManager.SaveTrickplayInfo(info);
```

Two details matter:

- **`inputPath` is a URL, not the `.strm` file.** `BaseItem.GetVersionInfo()` already replaces a
  shortcut's media source path with `Video.ShortcutPath` and sets `Protocol` accordingly, so
  `mediaSource.Path` is the stream URL. The `.strm` file is only read as a fallback when
  `ShortcutPath` was never populated. No ffmpeg wrapper is required.
- **`CreateTiles()` leaves `TrickplayInfo.ItemId` empty.** Jellyfin's own code assigns it right
  after the call; so does this plugin. Without it the tiles exist on disk but no client finds them.

All tuning (interval, widths, tile size, quality, hardware acceleration) is read from the server's
`TrickplayOptions`, so there is no second set of settings. The options are copied before use —
an out-of-range interval is raised to 1000 ms on the copy rather than mutating live server config.

## Data Flow

### Scheduled Task Flow

```
ProbeStrmTask.ExecuteAsync()
    │
    ├─ ProbeService.GetUnprobedItems()
    │   ├─ ILibraryManager.GetItemList(MediaType.Video, Recursive)
    │   ├─ Filter: path.EndsWith(".strm")
    │   └─ Filter: GetMediaStreams().Count == 0
    │
    ├─ ProbeService.ProbeBatchAsync(items, parallelism, timeout, cooldown)
    │   ├─ Parallel.ForEachAsync(parallelism) controls concurrency
    │   ├─ For each item (in parallel):
    │   │   ├─ ProbeService.ProbeItemCoreAsync(item, timeout, directoryService)
    │   │   │   ├─ CancellationTokenSource with timeout
    │   │   │   ├─ IProviderManager.RefreshSingleItem(item, options)
    │   │   │   │   └─ Jellyfin internally runs ffprobe
    │   │   │   └─ Returns success/failure
    │   │   ├─ Increment probed/failed counter
    │   │   ├─ Report progress
    │   │   └─ Cooldown delay
    │   └─ Return (probed, failed)
    │
    └─ Log results
```

### Trickplay Task Flow

```
StrmTrickplayTask.ExecuteAsync()
    │
    ├─ Guard: EnableStrmTrickplay off → log, report 100%, return
    │
    ├─ TrickplayService.GetCandidatesAsync(libraryIds, maxItems)
    │   ├─ ILibraryManager.GetItemIds(InternalItemsQuery { AncestorIds })
    │   ├─ ITrickplayManager.GetTrickplayItemsAsync() — paged, builds the already-done set
    │   ├─ Filter: item is Video && video.IsShortcut
    │   ├─ Filter: RunTimeTicks is not null/0  (probe task must have run)
    │   ├─ Filter: not in the already-done set  ← must precede the cap
    │   └─ Stop at maxItems (0 = unlimited)
    │
    ├─ For each candidate (sequential — each run streams a whole file):
    │   └─ TrickplayService.GenerateAsync(video, timeoutMinutes)
    │       ├─ Linked CTS with CancelAfter(timeoutMinutes) — abandons a slow item
    │       ├─ Video.GetMediaSources(false), matched on the video's own ID
    │       ├─ Resolve input: mediaSource.Path (URL), else read the .strm file
    │       ├─ ITrickplayManager.GetTrickplayResolutions() — skip widths already done
    │       └─ For each configured width:
    │           ├─ Normalize width (even, clamped to the video width)
    │           ├─ IMediaEncoder.ExtractVideoImagesOnIntervalAccelerated() → temp JPEGs
    │           ├─ ITrickplayManager.GetTrickplayDirectory()
    │           ├─ ITrickplayManager.CreateTiles() → TrickplayInfo
    │           ├─ info.ItemId = video.Id
    │           ├─ ITrickplayManager.SaveTrickplayInfo()
    │           └─ finally: delete temp directory
    │
    └─ Log results (generated / skipped / failed)
```

### Catch-up Flow

```
CatchUpEntryPoint.StartAsync()
    │
    ├─ Create CancellationTokenSource for shutdown
    ├─ Subscribe to ILibraryManager.ItemAdded
    │
    └─ On each ItemAdded event:
        ├─ Check EnableCatchUpMode (supports hot-reload)
        ├─ Filter: path.EndsWith(".strm")
        ├─ Enqueue item to ConcurrentQueue
        └─ Reset debounce timer (30 seconds)
            │
            └─ Timer fires:
                ├─ Drain queue to List
                ├─ Filter: GetMediaStreams().Count == 0
                └─ ProbeService.ProbeBatchAsync(items, ...)
```

## Dependency Injection

### Registered via `PluginServiceRegistrator`

| Service | Lifetime | Description |
|---------|----------|-------------|
| `IProbeService` → `ProbeService` | Singleton | Core probing logic |
| `ITrickplayService` → `TrickplayService` | Singleton | Core trickplay generation logic |
| `ProbeStrmTask` | Singleton (as `IScheduledTask`) | Scheduled task |
| `StrmTrickplayTask` | Singleton (as `IScheduledTask`) | Scheduled task |
| `CatchUpEntryPoint` | Hosted Service | IHostedService, starts on server boot |

### Auto-discovered by Jellyfin

| Service | Interface | Description |
|---------|-----------|-------------|
| `Plugin` | `BasePlugin<T>` | Auto-discovered plugin entry point |

### Injected from Jellyfin

| Service | Used By | Purpose |
|---------|---------|---------|
| `IProviderManager` | ProbeService | `RefreshSingleItem()` — triggers ffprobe |
| `ILibraryManager` | ProbeService, TrickplayService, CatchUpEntryPoint | Query items, library options, subscribe to events |
| `IFileSystem` | ProbeService | Create `DirectoryService` for `MetadataRefreshOptions` |
| `ITrickplayManager` | TrickplayService | `CreateTiles()`, `SaveTrickplayInfo()`, tile directory + existing resolutions |
| `IMediaEncoder` | TrickplayService | `ExtractVideoImagesOnIntervalAccelerated()` — runs ffmpeg |
| `EncodingHelper` | TrickplayService | Builds the ffmpeg input/filter arguments (registered in `ApplicationHost`) |
| `IServerConfigurationManager` | TrickplayService | Reads the server's `TrickplayOptions` |
| `ILogger<T>` | All components | Structured logging |

## Configuration

### Settings Model (`PluginConfiguration`)

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `EnableCatchUpMode` | bool | true | Auto-probe new STRM items |
| `ProbeParallelism` | int | 5 | Max concurrent probes |
| `ProbeTimeoutSeconds` | int | 60 | Per-item timeout |
| `ProbeCooldownMs` | int | 200 | Delay between probes |
| `SelectedLibraryIds` | Guid[] | empty | Libraries both tasks work on (empty = all) |
| `EnableStrmTrickplay` | bool | false | Makes the trickplay task available |
| `TrickplayTimeoutMinutes` | int | 60 | Per-item time budget (0 = no limit) |
| `TrickplayMaxItemsPerRun` | int | 50 | Cap for the trickplay task (0 = unlimited) |

### Configuration UI

The config page is embedded as a resource and registered via `IHasWebPages`. It uses Jellyfin's standard plugin configuration API:

- `GET /Plugins/{PluginId}/Configuration` — load settings
- `POST /Plugins/{PluginId}/Configuration` — save settings
- Library list from `GET /Library/VirtualFolders`

## Error Handling Strategy

| Error | Handling | Recovery |
|-------|----------|----------|
| ffprobe timeout | `CancellationTokenSource.CancelAfter()` | Log warning, skip item, retry next run |
| ffprobe failure (bad URL, 404) | Catch `Exception` in `ProbeItemAsync` | Log warning, skip item, retry next run |
| Task cancellation | `OperationCanceledException` propagates | Task stops cleanly, resume on next run |
| Plugin not initialized | Check `Plugin.Instance` | Return early from task/catch-up |
| No items to probe | Early exit with 100% progress | No-op |

## Concurrency Model

```
Parallel.ForEachAsync(MaxDegreeOfParallelism = ProbeParallelism)
    │
    ├─ Worker 1: probe item A ──── ffprobe ──── cooldown ──── next item
    ├─ Worker 2: probe item B ──── ffprobe ──── cooldown ──── next item
    ├─ Worker 3: probe item C ──── ffprobe ──── cooldown ──── next item
    └─ Items are lazily consumed from the source list

Each worker:
1. Receives next item from source
2. Runs ffprobe (via RefreshSingleItem) with per-item timeout
3. Increments success/failure counter (Interlocked)
4. Reports progress
5. Waits cooldown
6. Receives next item (or completes if none remain)

A shared DirectoryService instance is reused across all workers
to avoid per-item allocation overhead.
```

The trickplay task is deliberately **sequential**. Probing reads a few seconds of each stream,
but trickplay extraction transfers the whole file — running several of those at once would
saturate the provider link rather than finish sooner. `TrickplayMaxItemsPerRun` bounds a run
instead.

The cap only works because completed items are filtered out **before** it is applied. Filtering
them afterwards would make every run re-select the same first N items, and the library would
never be worked through. An item counts as done once it has any trickplay row — matching on the
configured widths would stall for videos narrower than a configured width, whose tiles are stored
at the clamped width and would never match.

## Target Platform

| Component | Version |
|-----------|---------|
| .NET | 9.0 |
| Jellyfin.Controller | 10.11.0 |
| Jellyfin.Model | 10.11.0 |
| StyleCop.Analyzers | 1.2.0-beta.556 |
