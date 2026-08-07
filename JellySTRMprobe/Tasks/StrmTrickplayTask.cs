using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using JellySTRMprobe.Service;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace JellySTRMprobe.Tasks;

/// <summary>
/// Scheduled task that generates trickplay scrubbing previews for STRM items,
/// which Jellyfin's own trickplay generation skips by design.
///
/// Requires items to already carry media info, so run <see cref="ProbeStrmTask"/> first.
/// </summary>
public class StrmTrickplayTask : IScheduledTask, IConfigurableScheduledTask
{
    private readonly ITrickplayService _trickplayService;
    private readonly ILogger<StrmTrickplayTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="StrmTrickplayTask"/> class.
    /// </summary>
    /// <param name="trickplayService">Instance of the <see cref="ITrickplayService"/>.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{StrmTrickplayTask}"/>.</param>
    public StrmTrickplayTask(ITrickplayService trickplayService, ILogger<StrmTrickplayTask> logger)
    {
        _trickplayService = trickplayService;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Generate STRM Trickplay";

    /// <inheritdoc />
    public string Key => "StrmTrickplayGeneration";

    /// <inheritdoc />
    public string Description => "Generates trickplay scrubbing previews for STRM items, which Jellyfin skips by design. Run the probe task first.";

    /// <inheritdoc />
    public string Category => "STRM Probe";

    /// <inheritdoc />
    /// <remarks>
    /// Evaluated by the server on every /ScheduledTasks request, so flipping the setting
    /// shows or hides the task without a restart.
    /// </remarks>
    public bool IsHidden => !TrickplayEnabled;

    /// <inheritdoc />
    /// <remarks>
    /// Blocks any trigger an admin may have added while the feature was on. It does not
    /// block a manual run — <see cref="ExecuteAsync"/> guards that case separately.
    /// </remarks>
    public bool IsEnabled => TrickplayEnabled;

    /// <inheritdoc />
    public bool IsLogged => true;

    /// <summary>
    /// Gets a value indicating whether trickplay generation is switched on.
    /// Falls back to <c>false</c> when the plugin instance is not available: this is read
    /// while the server builds the task list, where throwing would break the whole endpoint.
    /// </summary>
    private static bool TrickplayEnabled =>
        Plugin.InstanceOrNull?.Configuration.EnableStrmTrickplay ?? false;

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        // No default trigger: every run streams whole files from the provider, so this
        // stays manual until an admin deliberately schedules it.
        return Array.Empty<TaskTriggerInfo>();
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance.Configuration;

        // IsEnabled only stops trigger-fired runs; a manual start reaches this directly.
        if (!config.EnableStrmTrickplay)
        {
            _logger.LogInformation("STRM trickplay is disabled in the plugin configuration");
            progress.Report(100);
            return;
        }

        config.Validate();

        _logger.LogInformation("Starting STRM trickplay task");

        var candidates = await _trickplayService.GetCandidatesAsync(
            config.SelectedLibraryIds,
            config.TrickplayMaxItemsPerRun,
            cancellationToken).ConfigureAwait(false);

        if (candidates.Count == 0)
        {
            _logger.LogInformation("No STRM items need trickplay generation");
            progress.Report(100);
            return;
        }

        _logger.LogInformation("Generating trickplay for {Count} STRM items", candidates.Count);

        var done = 0;
        var succeeded = 0;
        var skipped = 0;
        var failed = 0;

        foreach (var video in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (await _trickplayService.GenerateAsync(video, config.TrickplayTimeoutMinutes, cancellationToken).ConfigureAwait(false))
                {
                    succeeded++;
                }
                else
                {
                    skipped++;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (cancellationToken.IsCancellationRequested)
            {
                // Killing ffmpeg surfaces as FfmpegException rather than
                // OperationCanceledException, so an admin cancelling the task would
                // otherwise be reported as a failure.
                _logger.LogInformation("Trickplay generation stopped for {Path}: task cancelled", video.Path);
                throw new OperationCanceledException("STRM trickplay task cancelled", ex, cancellationToken);
            }
            catch (Exception ex)
            {
                failed++;
                _logger.LogError(ex, "Trickplay generation failed for {Path}", video.Path);
            }

            done++;
            progress.Report(done * 100.0 / candidates.Count);
        }

        _logger.LogInformation(
            "STRM trickplay task finished: {Succeeded} generated, {Skipped} skipped, {Failed} failed",
            succeeded,
            skipped,
            failed);
    }
}
