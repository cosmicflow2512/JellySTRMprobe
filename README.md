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
| Libraries | All | Multi-select | Which libraries to probe |

## Installation

### From Plugin Repository (Recommended)

1. In Jellyfin, go to **Dashboard > Plugins > Repositories**
2. Click **+** and add this repository URL:
   ```
   https://raw.githubusercontent.com/cosmicflow2512/JellySTRMprobe/claude/jellystrmprobe-runtimeticks-vn8m4l/manifest.json
   ```
3. Go to **Catalog**, find **JellySTRMprobe**, and install
4. Restart Jellyfin

Updates then arrive automatically through the catalog — bump the version, rebuild
the release ZIP, and add a new entry to `manifest.json` (see
[Publishing a new version](#publishing-a-new-version)).

> **Note:** The manifest is served straight from this branch via
> `raw.githubusercontent.com`. Once this branch is merged, update the URL above
> (and the `sourceUrl` in `manifest.json`) to point at the branch the manifest
> lives on (e.g. `master`).

### Manual Installation

1. Download `dist/JellySTRMprobe_1.4.0.0.zip` from this repository
2. Extract it into a versioned folder in your Jellyfin plugins directory so that
   `JellySTRMprobe.dll` and `meta.json` sit side by side:
   ```
   <jellyfin-data>/plugins/JellySTRMprobe_1.4.0.0/JellySTRMprobe.dll
   <jellyfin-data>/plugins/JellySTRMprobe_1.4.0.0/meta.json
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
