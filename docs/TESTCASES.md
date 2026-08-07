# JellySTRMprobe — Test Cases

## Test Framework

- **Framework**: xUnit
- **Mocking**: Moq
- **Assertions**: xUnit built-in + FluentAssertions (optional)
- **Pattern**: AAA (Arrange, Act, Assert)

## Unit Tests

### 1. ProbeService Tests

#### 1.1 GetUnprobedItems

| ID | Test Case | Setup | Expected |
|----|-----------|-------|----------|
| TC-1.1.1 | Returns only STRM items with no media streams | 3 items: .strm/0 streams, .strm/2 streams, .mkv/0 streams | Returns only the first item |
| TC-1.1.2 | Filters by selected library IDs | 2 items in lib A, 1 item in lib B, selected = [A] | Returns only 2 items from lib A |
| TC-1.1.3 | Returns all libraries when selection is empty | Items in multiple libraries, selection = [] | Returns all unprobed STRM items |
| TC-1.1.4 | Returns empty list when no unprobed items | All items have media streams | Returns empty list |
| TC-1.1.5 | Handles null path gracefully | Item with null path in results | Skips item, no exception |

#### 1.2 ProbeItemAsync

| ID | Test Case | Setup | Expected |
|----|-----------|-------|----------|
| TC-1.2.1 | Successful probe returns true | Mock RefreshSingleItem succeeds | Returns true |
| TC-1.2.2 | Timeout returns false and logs warning | RefreshSingleItem hangs beyond timeout | Returns false, log contains "timed out" |
| TC-1.2.3 | Exception returns false and logs warning | RefreshSingleItem throws HttpRequestException | Returns false, log contains "failed" |
| TC-1.2.4 | Cancellation propagates when parent token cancelled | Parent CancellationToken is cancelled | Throws OperationCanceledException |
| TC-1.2.5 | Correct MetadataRefreshOptions passed | Capture options passed to RefreshSingleItem | EnableRemoteContentProbe=true, ReplaceAllMetadata=false, ReplaceAllImages=false |

#### 1.3 ProbeBatchAsync

| ID | Test Case | Setup | Expected |
|----|-----------|-------|----------|
| TC-1.3.1 | Probes all items and reports correct counts | 5 items, all succeed | (Probed=5, Failed=0, Skipped=0) |
| TC-1.3.2 | Mixed success/failure counts | 3 succeed, 2 fail | (Probed=3, Failed=2, Skipped=0) |
| TC-1.3.3 | Respects parallelism limit | 10 items, parallelism=2 | Max 2 concurrent probes at any time |
| TC-1.3.4 | Reports progress incrementally | 4 items | Progress reported at ~25%, ~50%, ~75%, ~100% |
| TC-1.3.5 | Applies cooldown between probes | 3 items, cooldown=100ms | Total time >= 300ms (3 * 100ms cooldown) |
| TC-1.3.6 | Cancellation stops batch processing | Cancel after 2nd item | Returns partial results, no more probes |
| TC-1.3.7 | Empty list returns zero counts | 0 items | (Probed=0, Failed=0, Skipped=0) |

### 2. ProbeStrmTask Tests

| ID | Test Case | Setup | Expected |
|----|-----------|-------|----------|
| TC-2.1 | Task has correct metadata | — | Name="Probe STRM Media Info", Category="STRM Probe" |
| TC-2.2 | Default trigger is daily at 4 AM | — | DailyTrigger with TimeOfDayTicks = 4 hours |
| TC-2.3 | Execute probes unprobed items | 3 unprobed items | ProbeBatchAsync called with 3 items |
| TC-2.4 | Execute skips when no items found | 0 unprobed items | Progress reports 100%, no ProbeBatchAsync call |
| TC-2.5 | Execute passes config values | Config: parallelism=10, timeout=120, cooldown=500 | ProbeBatchAsync called with matching params |

### 2b. TrickplayService Tests

Only `GetCandidates()` is unit tested. `GenerateAsync()` depends on `IMediaEncoder` and a real
ffmpeg process and is covered by the manual scenario IT-6 instead.

| ID | Test Case | Setup | Expected |
|----|-----------|-------|----------|
| TC-2b.1 | Returns shortcut videos with a duration | 1 shortcut Video, RunTimeTicks set | Item returned |
| TC-2b.2 | Skips non-video items | BaseItem that is not a Video | Empty result |
| TC-2b.3 | Skips non-shortcut videos | Video with IsShortcut=false | Empty result |
| TC-2b.4 | Skips videos without a usable duration | RunTimeTicks null / 0 | Empty result |
| TC-2b.5 | Skips unresolvable items | GetItemById throws for one ID | Other item still returned |
| TC-2b.6 | Respects maxItems | 5 candidates, maxItems=2 | 2 items returned |
| TC-2b.7 | maxItems=0 means unlimited | 5 candidates, maxItems=0 | 5 items returned |
| TC-2b.10 | Excludes items that already have trickplay | 2 items, 1 already done | Only the pending item returned |
| TC-2b.11 | Capped run advances past completed items (regression) | 5 items, first 2 done, maxItems=2 | Items 3 and 4 returned, not 1 and 2 |
| TC-2b.12 | Pages through all trickplay rows | Full first page, item on second page | Second page requested, item excluded |
| TC-2b.8 | No library IDs leaves AncestorIds unset | Empty Guid[] | Query.AncestorIds empty |
| TC-2b.9 | Library IDs set AncestorIds | One library ID | Query.AncestorIds contains it |

### 2c. StrmTrickplayTask Tests

| ID | Test Case | Setup | Expected |
|----|-----------|-------|----------|
| TC-2c.1 | Task has correct metadata | — | Name="Generate STRM Trickplay", Key="StrmTrickplayGeneration", Category="STRM Probe" |
| TC-2c.8 | Visible and enabled when trickplay is on | EnableStrmTrickplay=true | IsHidden=false, IsEnabled=true |
| TC-2c.9 | Hidden and disabled when trickplay is off | EnableStrmTrickplay=false | IsHidden=true, IsEnabled=false |
| TC-2c.10 | Execute does nothing when trickplay is off | EnableStrmTrickplay=false, 1 candidate | Progress 100%, GetCandidatesAsync and GenerateAsync never called |
| TC-2c.11 | Passes the configured timeout | TrickplayTimeoutMinutes=25 | GenerateAsync called with 25 |
| TC-2c.12 | ffmpeg kill on cancellation becomes a cancellation | Token cancelled, GenerateAsync throws a plain exception | OperationCanceledException propagates, not logged as a failure |
| TC-2c.2 | No default trigger | — | GetDefaultTriggers() is empty |
| TC-2c.3 | Execute skips when no candidates | 0 candidates | Progress reports 100%, GenerateAsync never called |
| TC-2c.4 | Execute generates for every candidate | 3 candidates | GenerateAsync called once per item |
| TC-2c.5 | Execute passes configured limit and libraries | Config: 1 library, max=7 | GetCandidates called with matching params |
| TC-2c.6 | One failing item does not abort the run | Item 1 throws, item 2 succeeds | Item 2 still processed, progress reaches 100% |
| TC-2c.7 | Cancellation propagates | GenerateAsync throws OperationCanceledException | Exception rethrown to the task runner |

### 3. CatchUpEntryPoint Tests

| ID | Test Case | Setup | Expected |
|----|-----------|-------|----------|
| TC-3.1 | Subscribes to ItemAdded when enabled | Config: EnableCatchUpMode=true | ItemAdded event handler registered |
| TC-3.2 | Does not subscribe when disabled | Config: EnableCatchUpMode=false | ItemAdded event handler NOT registered |
| TC-3.3 | Queues STRM items on add | Add event with .strm path | Item added to pending queue |
| TC-3.4 | Ignores non-STRM items | Add event with .mkv path | Queue remains empty |
| TC-3.5 | Debounces processing (30 seconds) | Add 3 items within 1 second | ProcessQueue not called until 30s after last add |
| TC-3.6 | Processes all queued items in batch | Queue 5 items, timer fires | ProbeBatchAsync called with 5 items |
| TC-3.7 | Filters already-probed items before processing | Queue item that already has media streams | Item excluded from ProbeBatchAsync call |
| TC-3.8 | Dispose unsubscribes from events | Call Dispose() | ItemAdded event handler removed |

### 4. PluginConfiguration Tests

| ID | Test Case | Setup | Expected |
|----|-----------|-------|----------|
| TC-4.1 | Default values are correct | New PluginConfiguration() | CatchUp=true, Parallelism=5, Timeout=60, Cooldown=200, Libraries=[] |
| TC-4.2 | Validate clamps parallelism | Parallelism=50 | Clamped to 20 |
| TC-4.3 | Validate clamps timeout | Timeout=0 | Clamped to 10 |
| TC-4.4 | Validate clamps cooldown | Cooldown=-1 | Clamped to 0 |

### 5. Plugin Tests

| ID | Test Case | Setup | Expected |
|----|-----------|-------|----------|
| TC-5.1 | Plugin has correct name | — | Name == "JellySTRMprobe" |
| TC-5.2 | Plugin has valid GUID | — | Id is a valid, non-empty Guid |
| TC-5.3 | Plugin returns config pages | — | GetPages() returns config.html and config.js |

## Integration Test Scenarios (Manual)

These tests require a running Jellyfin instance with STRM files.

### IT-1: Scheduled Task Probing

| Step | Action | Expected Result |
|------|--------|-----------------|
| 1 | Install plugin, restart Jellyfin | Plugin visible in Dashboard > Plugins |
| 2 | Navigate to Dashboard > Scheduled Tasks | "Probe STRM Media Info" task visible under "STRM Probe" category |
| 3 | Click Run on the task | Task starts, progress bar advances |
| 4 | Check Jellyfin logs | "Found N unprobed STRM items" logged |
| 5 | Wait for completion | "Probe complete: X succeeded, Y failed" logged |
| 6 | Check a probed movie in Jellyfin UI | Duration, resolution, codec, audio channels visible |

### IT-2: Catch-up Mode

| Step | Action | Expected Result |
|------|--------|-----------------|
| 1 | Enable catch-up mode in plugin config | Setting saved |
| 2 | Restart Jellyfin | "STRM Probe catch-up mode enabled" in logs |
| 3 | Add new STRM files to library folder | — |
| 4 | Trigger library scan | New items discovered |
| 5 | Wait 30+ seconds | "Catch-up: probing N new STRM items" logged |
| 6 | Check new items in UI | Media info visible |

### IT-3: Configuration

| Step | Action | Expected Result |
|------|--------|-----------------|
| 1 | Navigate to plugin config page | Settings page loads with defaults |
| 2 | Change parallelism to 10 | Saved, reflected in next task run |
| 3 | Select specific library in library picker | Only that library's items are probed |
| 4 | Disable catch-up mode | Catch-up stops (new items not auto-probed) |

### IT-4: Error Resilience

| Step | Action | Expected Result |
|------|--------|-----------------|
| 1 | Add STRM with invalid URL | Probe fails for that item, others continue |
| 2 | Set timeout to 10 seconds, probe slow stream | Timeout logged, item skipped |
| 3 | Cancel task mid-run | Task stops cleanly, partial results preserved |
| 4 | Restart Jellyfin during catch-up | Catch-up re-subscribes on startup |

### IT-6: STRM Trickplay Generation

Covers `TrickplayService.GenerateAsync()`, which has no unit test — it needs a real ffmpeg
and a reachable stream.

| Step | Action | Expected Result |
|------|--------|-----------------|
| 0a | Fresh install, look at Scheduled Tasks | Only "Probe STRM Media Info" under "STRM Probe" — trickplay is off by default |
| 0b | Enable *Enable STRM trickplay*, save, reload Scheduled Tasks | "Generate STRM Trickplay" appears **without a server restart** |
| 0c | Turn it off again, then `POST /ScheduledTasks/Running/StrmTrickplayGeneration` | Returns at once, log shows "STRM trickplay is disabled in the plugin configuration", nothing extracted |
| 1 | Turn it back on. Select a single library and set *Maximum items per run* to a small number | Setting saved |
| 2 | Run "Probe STRM Media Info" first | Items have duration and a video stream |
| 3 | Navigate to Dashboard > Scheduled Tasks | "Generate STRM Trickplay" visible under "STRM Probe", no schedule |
| 4 | Click Run | "Found N STRM videos with usable media info" logged |
| 5 | Watch the log | "Creating trickplay files at {Width} width" then "Finished creation of trickplay files" per item |
| 6 | Confirm ffmpeg was actually invoked | `image2` / `mjpeg` calls appear in the ffmpeg log (they were absent before) |
| 7 | Check the tile directory | JPEG tiles exist under the trickplay directory |
| 8 | Hover the seekbar of a STRM movie in the web client | Scrubbing previews appear |
| 9 | Run the task a second time | Log shows "N already done"; the run picks up the *next* items, not the same ones |
| 10 | Run against items that were never probed | Items skipped with "run the probe task first", no crash |
| 11 | Grep the log for the stream URL | No provider credentials logged |

### IT-5: Dispatcharr Compatibility

| Step | Action | Expected Result |
|------|--------|-----------------|
| 1 | Set cooldown to 500ms | Probes spaced by 500ms minimum |
| 2 | Run task against Dispatcharr VOD proxy URLs | Movies probed successfully |
| 3 | Check Dispatcharr logs | No connection exhaustion or 503 errors |

## Test Coverage Goals

| Component | Target Coverage |
|-----------|----------------|
| ProbeService | 90%+ (core logic) |
| ProbeStrmTask | 80%+ (task orchestration) |
| TrickplayService | `GetCandidates()` only — `GenerateAsync()` needs a real ffmpeg (see IT-6) |
| StrmTrickplayTask | 80%+ (task orchestration) |
| CatchUpEntryPoint | 70%+ (event handling, hard to test timers) |
| PluginConfiguration | 100% (simple model) |
| Plugin | 100% (simple entry point) |
