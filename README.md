# JellySTRMprobe

A Jellyfin plugin that extracts media information from STRM files by probing remote streams.

## The Problem

Jellyfin treats STRM files as "shortcuts" and **never probes them during library scans**. This means movies and episodes sourced from STRM files show up with no duration, codec, resolution, or audio information — even after a full library scan. Media info is only populated on first playback.

**Without this plugin:**

- No duration, codec, resolution, or audio info in the UI
- Movies may be marked as "played" after a few seconds (no duration known)
- Filter/search by resolution or codec doesn't work
- The version picker can't distinguish between quality variants

## How It Works

The plugin calls Jellyfin's internal `RefreshSingleItem()` with `EnableRemoteContentProbe = true` — the same flag Jellyfin sets during playback. This triggers ffprobe against the STRM target URL without requiring the user to play every item first.

## Features

### Scheduled Task
A scheduled task (Dashboard > Scheduled Tasks > **Probe STRM Media Info**) that:
- Finds all STRM items with no media stream data **or a missing duration** (see [Fork Changes](#fork-changes))
- Probes them in parallel with configurable concurrency
- Runs daily at 4:00 AM by default (customizable)
- Reports progress and is cancellable from the Dashboard

### Trickplay Task (optional, off by default)
A second scheduled task (Dashboard > Scheduled Tasks > **Generate STRM Trickplay**) that creates
the scrubbing preview tiles Jellyfin refuses to generate for STRM:
- Jellyfin's resolver marks every `.strm` as `IsShortcut`, and `TrickplayManager.CanGenerateTrickplay()`
  returns `false` for shortcuts — so the library option *Extract trickplay images* silently does nothing
- This task calls the same pipeline directly
  (`IMediaEncoder.ExtractVideoImagesOnIntervalAccelerated` → `ITrickplayManager.CreateTiles` →
  `SaveTrickplayInfo`), bypassing that gate
- Uses the server's own **Dashboard > Playback > Trickplay** settings (interval, widths, tile size,
  hardware acceleration), so there is nothing to configure twice
- Skips items that already have tiles, so re-running is cheap
- **No default trigger** — run it manually, or add a schedule once you're happy with it
- With *Maximum items per run* set, each run picks up where the last one left off, so a
  nightly schedule works through a large library incrementally

> **Switched off out of the box.** Enable *Enable STRM trickplay* in the plugin settings first.
> While it is off the task is hidden from Scheduled Tasks and refuses to run even if started
> through the API. Toggling it takes effect immediately — no server restart.

> **Run the probe task first.** Trickplay generation needs a duration and a video stream, which is
> exactly what **Probe STRM Media Info** supplies. Items without them are skipped and logged.

#### Why it is slow, and what actually helps

Every item is streamed **in full** through your provider, and the task runs sequentially on
purpose — several concurrent full-file transfers would saturate the link rather than finish
sooner. On a slow connection the network, not the CPU, is the limit.

The useful levers all live in **Dashboard > Playback > Trickplay** and are picked up automatically:

| Setting | Effect |
|---------|--------|
| *Extract keyframes only* | Biggest win — skips decoding every frame |
| *FFmpeg threads* | Default is 1; raising it to 4 helps a lot while you are still CPU-bound |
| A single width resolution | Each additional width is another complete pass over the same file |
| A longer interval | Fewer thumbnails per file |
| Hardware acceleration | Only if the CPU is the bottleneck — see below |

**Measure before enabling hardware acceleration.** Run `docker stats` on the Jellyfin container
during a run. 100% means one full core. If ffmpeg sits well below the thread count you gave it
(e.g. 50% with `-threads 4`), it is waiting on the network and a GPU has nothing to accelerate.

A real measurement from a 2160p HEVC Dolby Vision remux behind a Usenet-backed mount:

| | Defaults | Keyframes only + 4 threads |
|---|---|---|
| One film | >127 min, never finished | ~37 min |
| Bottleneck | CPU — 1 thread decoding every frame | Network |

Once keyframe-only extraction removes the bulk of the decoding, there is little left for a GPU to
do. Start with one library selected and a small *Maximum items per run*, then let repeated runs
work through the rest.

### Catch-Up Mode
Automatically probes new STRM items as they're added during library scans:
- Subscribes to Jellyfin's `ItemAdded` event
- Debounces for 30 seconds to batch process items
- Enabled by default (can be disabled in settings)

### Configuration
All settings are accessible from Dashboard > Plugins > JellySTRMprobe:

| Setting | Default | Range | Description |
|---------|---------|-------|-------------|
| Catch-Up Mode | Enabled | On/Off | Auto-probe new STRM items when added |
| Parallelism | 5 | 1–20 | Concurrent probe operations |
| Timeout | 60s | 10–300s | Per-item probe timeout |
| Cooldown | 200ms | 0–5000ms | Delay between probes (prevents upstream overload) |
| Enable STRM trickplay | Disabled | On/Off | Makes the trickplay task available; hidden and inert while off |
| Trickplay max items per run | 50 | 0–100000 | Cap per trickplay run; the next run continues with the remaining items (0 = unlimited) |
| Trickplay timeout per item | 60 min | 0–1440 | Time budget per item; on expiry the item is abandoned and the run continues (0 = no limit) |
| Libraries | All | Multi-select | Which libraries both tasks work on |

## Installation

### From Plugin Repository (Recommended)

1. In Jellyfin, go to **Dashboard > Plugins > Repositories**
2. Click **+** and add this repository URL:
   ```
   https://raw.githubusercontent.com/cosmicflow2512/JellySTRMprobe/master/manifest.json
   ```
3. Go to **Catalog**, find **JellySTRMprobe**, and install
4. Restart Jellyfin

Updates then arrive automatically through the catalog — bump the version, rebuild
the release ZIP, and add a new entry to `manifest.json` (see
[Publishing a new version](#publishing-a-new-version)).

> **Note:** The manifest and release archives are served straight from the
> `master` branch via `raw.githubusercontent.com` — the raw URL always includes
> the branch segment (`.../<repo>/master/...`), there is no branch-less form.

### Manual Installation

1. Download `dist/JellySTRMprobe_1.6.1.0.zip` from this repository
2. Extract it into a versioned folder in your Jellyfin plugins directory so that
   `JellySTRMprobe.dll` and `meta.json` sit side by side:
   ```
   <jellyfin-data>/plugins/JellySTRMprobe_1.6.1.0/JellySTRMprobe.dll
   <jellyfin-data>/plugins/JellySTRMprobe_1.6.1.0/meta.json
   ```
   (On the official Docker image `<jellyfin-data>` is `/config`.)
3. Restart Jellyfin

## Requirements

- Jellyfin **10.11.0** or later
- .NET 9.0

## Building from Source

```bash
dotnet build -c Release
dotnet test -c Release
dotnet publish JellySTRMprobe -c Release -o ./publish
```

## Plugin Repository / Publishing a new version

This fork ships its own Jellyfin plugin repository. The catalog file
(`manifest.json`) and the release archives (`dist/*.zip`) are hosted **directly
from the git repo** via `raw.githubusercontent.com` — no separate GitHub Pages
site or GitHub Release is required. Users only enter the `manifest.json` raw URL
once (see [Installation](#from-plugin-repository-recommended)).

Each release ZIP is what Jellyfin downloads and unpacks into
`{pluginsPath}/JellySTRMprobe_{version}/`. It contains the built `JellySTRMprobe.dll`
plus a `meta.json` whose `assemblies` list names that DLL. Jellyfin verifies the
archive against the **MD5** `checksum` recorded in `manifest.json` (the core
uses MD5, not SHA), and marks the plugin `NotSupported` if `targetAbi` is newer
than the running server — so `targetAbi` stays at the build ABI `10.11.0.0`. The
manifest `guid` must equal `Plugin.Id` (`b8f5e3a1-d4c7-4f2e-9a6b-1c8d3e5f7a9b`),
otherwise Jellyfin will not match the catalog entry to the installed plugin.

To cut a new version:

```bash
# 1. Bump <AssemblyVersion>/<FileVersion> in JellySTRMprobe.csproj and build.yaml,
#    then publish the DLL:
dotnet publish JellySTRMprobe -c Release -o ./publish

# 2. Write publish/meta.json (same fields as the manifest version entry, plus
#    "assemblies": ["JellySTRMprobe.dll"]), then package DLL + meta.json flat:
cd publish && zip -j ../dist/JellySTRMprobe_<version>.zip JellySTRMprobe.dll meta.json && cd ..

# 3. Compute the MD5 that goes into manifest.json -> versions[].checksum:
md5sum dist/JellySTRMprobe_<version>.zip

# 4. Prepend a new object to the "versions" array in manifest.json with the new
#    version, changelog, targetAbi, sourceUrl (raw URL of the new ZIP), checksum
#    (the MD5 above) and an ISO-8601 timestamp. Commit dist/ + manifest.json and push.
```

> Prefer a real GitHub Release instead of committing the ZIP? Upload the same
> `dist/JellySTRMprobe_<version>.zip` as a release asset and set `sourceUrl` to
> the asset download URL. The MD5 `checksum` is unchanged because the bytes are
> identical.

## Prior Art

| Project | Platform | Notes |
|---------|----------|-------|
| [StrmAssistant](https://github.com/sjtuross/StrmAssistant) | Emby | Emby-only, incompatible with Jellyfin |
| [JellyfinStrmExtract](https://github.com/gauthier-th/JellyfinStrmExtract) | Jellyfin | Sequential processing, targets Jellyfin 10.9.x |

## Fork Changes

This is a fork of [firestaerter3/JellySTRMprobe](https://github.com/firestaerter3/JellySTRMprobe), maintained by [cosmicflow2512](https://github.com/cosmicflow2512).

### v1.6.1 — Per-item timeout for trickplay

**What changed:** New setting `TrickplayTimeoutMinutes` (default 60, 0 = no limit).
`GenerateAsync()` now runs under a linked `CancellationTokenSource` with `CancelAfter`; when the
budget is spent the item is abandoned with a warning and the run continues with the next one.
Cancelling the task is also no longer reported as a generation failure.

**Why:** Jellyfin already guards against a *stalled* encoder — `ExtractVideoImagesOnIntervalInternal`
polls the output directory every `ImageExtractionTimeoutMs` (default 20 s) and kills ffmpeg if no
new image appeared. What it does not bound is total runtime: an item producing one image every
19 seconds stays "responsive" indefinitely. In practice a 2160p remux behind a Usenet-backed mount
ran for 127 minutes without finishing and had to be cancelled by hand.

Both the timeout and the cancellation path have to catch on the token rather than the exception
type. Killing ffmpeg makes `MediaEncoder` throw `FfmpegException`, not `OperationCanceledException`
— which is why a manual cancel previously showed up in the log as `Trickplay generation failed`.

### v1.6.0 — Trickplay is opt-in

**What changed:** New setting `EnableStrmTrickplay`, off by default. While it is off,
`StrmTrickplayTask.IsHidden` returns `true` so the task disappears from Scheduled Tasks,
`IsEnabled` returns `false` so any trigger an admin added stops firing, and `ExecuteAsync`
returns immediately with a log line. The plugin settings page hides the task from its schedule
panel too, and refreshes that panel right after saving. The capped-run fix below ships in this
release as well.

**Why:** Trickplay works, but it is slow — each item is streamed in full through the provider,
and the task runs sequentially by design. That is a fair trade for someone who wants scrubbing
previews and a poor one for everyone else, so it should not sit in the task list inviting a click.
Three separate switches are needed because they cover three different entry points:
`IsHidden` only affects the listing (`ScheduledTasksController.GetTasks()` re-reads it on every
request, which is why no restart is needed), `IsEnabled` is checked in
`ScheduledTaskWorker.OnTriggerTriggered()` and so only stops scheduled runs, and neither one
blocks a manual start — that goes straight to `QueueScheduledTask`, hence the guard inside
`ExecuteAsync`. `IsHidden`/`IsEnabled` read through `Plugin.InstanceOrNull` rather than
`Plugin.Instance`: they are evaluated while the server builds the task list, where a throw would
break the whole endpoint instead of just this task. Covered by tests in
`JellySTRMprobe.Tests/Tasks/StrmTrickplayTaskTests.cs`.

### v1.6.0 — Capped trickplay runs now advance through the library

**What changed:** `TrickplayService.GetCandidates()` selected items by "is a STRM with a duration"
and then truncated at `TrickplayMaxItemsPerRun`. The "already has trickplay" check lived one level
down in `GenerateAsync()`. With a limit of 3 that meant: run 1 processed items A, B, C; run 2
selected A, B, C again, skipped all three (`0 generated, 3 skipped`), and items D, E, F were never
reached. The cap pinned the task to the same items forever. Completed items are now filtered out
**before** the limit is applied — the method became `GetCandidatesAsync()` and builds the
already-done set in one pass via `ITrickplayManager.GetTrickplayItemsAsync()`. Regression test:
`GetCandidates_CappedRun_AdvancesPastCompletedItems`.

**Why:** A per-run cap is only useful if repeated runs make progress; otherwise it is a permanent
ceiling. An item counts as done once it has any trickplay row. Comparing against the configured
widths instead would stall a capped run for a different reason: videos narrower than a configured
width get their tiles at the clamped width, would never match the expected set, and would be
re-selected on every run while producing nothing. The trade-off is that adding a new width profile
later does not retroactively revisit finished items — delete their trickplay data to force a rebuild.

### v1.5.0 — Trickplay for STRM items

**What changed:** A second scheduled task, **Generate STRM Trickplay**, now produces the scrubbing
preview tiles that Jellyfin will not generate for STRM. The new
`JellySTRMprobe/Service/TrickplayService.cs` collects shortcut videos that already carry a duration
and drives the server's own pipeline directly —
`IMediaEncoder.ExtractVideoImagesOnIntervalAccelerated` → `ITrickplayManager.CreateTiles` →
`SaveTrickplayInfo` — using the widths, interval, tile size and hardware settings from
**Dashboard > Playback > Trickplay**. `JellySTRMprobe/Tasks/StrmTrickplayTask.cs` wraps it as a
scheduled task with no default trigger, and a new `TrickplayMaxItemsPerRun` setting (default 50)
caps a single run. Covered by tests in `JellySTRMprobe.Tests/Service/TrickplayServiceTests.cs` and
`JellySTRMprobe.Tests/Tasks/StrmTrickplayTaskTests.cs`.

**Why:** The exclusion is hard-coded in the server and not configurable.
`BaseVideoResolver` sets `video.IsShortcut = true` for every `.strm`, and `IsShortcut` is the third
check inside `TrickplayManager.CanGenerateTrickplay()` — which is the *first* condition in
`RefreshTrickplayDataAsync()`. So the library switch *Extract trickplay images* is inert for STRM,
ffmpeg is never invoked, and pre-placed tiles are never imported because
`DiscoverExistingTrickplayAsync()` only runs past that return. Fixing the duration (what v1.3.0 did)
does not help: the `RunTimeTicks` gate sits *behind* the `IsShortcut` gate. Calling the pipeline
from a plugin skips both, with no server fork or patch.

Two details make this work on a stock server. `BaseItem.GetVersionInfo()` already rewrites a
shortcut's media source to `Video.ShortcutPath` — the actual stream URL, with the protocol set
accordingly — so the task hands ffmpeg a URL and never the `.strm` file, and no ffmpeg wrapper is
required. And `CreateTiles()` does not populate `TrickplayInfo.ItemId`; the task sets it before
saving, otherwise the tiles land on disk and no client ever finds them.

### v1.4.0 — Catch-up mode reprobes half-items too

**What changed:** Catch-up mode (the `ItemAdded` auto-probe) applied the same
"missing duration" logic from v1.3.0. Previously it queued newly added STRM
items but then filtered them with `GetMediaStreams().Count == 0`, so an item
added with streams already present but no duration (`RunTimeTicks` null/0) was
dropped. That filter now also keeps items missing a usable duration, matching
the scheduled task's selection.

**Why:** Consistency — the same half-item that the scheduled task now reprobes
should also be caught the moment it is added, without waiting for the next
daily run. The `ItemAdded` subscription, 30-second debounce, and probe path
(`RefreshSingleItem` with `EnableRemoteContentProbe = true`) are unchanged; only
the post-debounce filter was widened. The change lives in
`JellySTRMprobe/EntryPoint/CatchUpEntryPoint.cs` (`ProcessQueueAsync`) and is
covered by tests in `JellySTRMprobe.Tests/EntryPoint/CatchUpEntryPointTests.cs`.

### v1.3.0 — Reprobe items with a missing duration

**What changed:** The scheduled task's item-selection query was widened. The
upstream task selects a STRM item only when it has *no media stream data*
(`GetMediaStreams().Count == 0`). This fork also selects STRM items that
**do** have media streams but are missing a usable duration
(`RunTimeTicks` is `null` or `0`).

**Why:** On real libraries (e.g. STRM files from NzbDAV), Jellyfin can persist
`MediaStreams` for an item — video, audio, subtitle tracks — while leaving
`RunTimeTicks` empty. Such items already have stream data, so the upstream
selection skipped them, and they stayed without a duration indefinitely.
Empirically, 181 of 618 STRM episodes on one server were affected. Jellyfin's
own core (`Emby.Server.Implementations/Library/MediaSourceManager.cs`,
`GetPlaybackMediaSources`) force-reprobes any `.strm` on playback for the same
reason; this fork mirrors that behaviour for the bulk task. The probe mechanism
itself (`RefreshSingleItem` with `EnableRemoteContentProbe = true`) is
unchanged — only the selection criterion was extended.

The change lives in `JellySTRMprobe/Service/ProbeService.cs`
(`GetUnprobedItems`) and is covered by unit tests in
`JellySTRMprobe.Tests/Service/ProbeServiceTests.cs`.

## License

Licensed under the GPL-3.0 License. See [LICENSE](LICENSE) for details.

This fork remains under **GPL-3.0**, consistent with the upstream project.
Copyright © the JellySTRMprobe contributors and, for the changes in this fork,
© 2026 cosmicflow2512. Modifications are described under
[Fork Changes](#fork-changes) above.
