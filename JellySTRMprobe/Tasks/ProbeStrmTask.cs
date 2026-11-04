using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using JellySTRMprobe.Service;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace JellySTRMprobe.Tasks;

/// <summary>
/// Scheduled task that probes unprobed STRM items to extract media information.
/// </summary>
public class ProbeStrmTask : IScheduledTask, IConfigurableScheduledTask
{
    private readonly IProbeService _probeService;
    private readonly ILogger<ProbeStrmTask> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProbeStrmTask"/> class.
    /// </summary>
    /// <param name="probeService">Instance of the <see cref="IProbeService"/>.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{ProbeStrmTask}"/>.</param>
    public ProbeStrmTask(IProbeService probeService, ILogger<ProbeStrmTask> logger)
    {
        _probeService = probeService;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Probe STRM Media Info";

    /// <inheritdoc />
    public string Key => "StrmProbeMediaInfo";

    /// <inheritdoc />
    public string Description => "Probes STRM file targets to extract media information (codec, resolution, duration, audio).";

    /// <inheritdoc />
    public string Category => "STRM Probe";

    /// <inheritdoc />
    public bool IsHidden => false;

    /// <inheritdoc />
    public bool IsEnabled => true;

    /// <inheritdoc />
    public bool IsLogged => true;

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return new[]
        {
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.DailyTrigger,
                TimeOfDayTicks = TimeSpan.FromHours(4).Ticks,
            },
        };
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance.Configuration;
        config.Validate();

        _logger.LogInformation("Starting STRM probe task");

        var items = _probeService.GetUnprobedItems(config.SelectedLibraryIds);

        if (items.Count == 0)
        {
            _logger.LogInformation("No unprobed STRM items found");
            progress.Report(100);
            return;
        }

        _logger.LogInformation(
            "Probing {Count} items with parallelism={Parallelism}, timeout={Timeout}s, cooldown={Cooldown}ms",
            items.Count,
            config.ProbeParallelism,
            config.ProbeTimeoutSeconds,
            config.ProbeCooldownMs);

        var result = await _probeService.ProbeBatchAsync(
            items,
            config.ProbeParallelism,
            config.ProbeTimeoutSeconds,
            config.ProbeCooldownMs,
            config.ProbeMaxConsecutiveFailures,
            progress,
            cancellationToken).ConfigureAwait(false);

        // An aborted run says nothing about the files themselves — the provider was down,
        // not the STRMs. Deleting on that evidence would wipe a working library.
        if (config.DeleteFailedStrms && result.FailedItems.Count > 0 && !result.Aborted)
        {
            var totalProbed = result.Probed + result.Failed;
            var failurePercent = (double)result.Failed / totalProbed * 100;

            if (failurePercent > config.DeleteFailureThreshold)
            {
                _logger.LogWarning(
                    "Failure rate {Rate:F1}% exceeds threshold {Threshold}% — skipping deletion of {Count} STRM files (provider may be down)",
                    failurePercent,
                    config.DeleteFailureThreshold,
                    result.FailedItems.Count);
            }
            else
            {
                var deleted = _probeService.DeleteStrmFiles(result.FailedItems);
                _logger.LogInformation("Deleted {Deleted} failed STRM files ({Rate:F1}% failure rate)", deleted, failurePercent);
            }
        }

        if (result.Aborted)
        {
            _logger.LogWarning(
                "STRM probe task aborted early: {Probed} probed, {Failed} failed, {Skipped} not attempted. Check that the provider is reachable, then run the task again",
                result.Probed,
                result.Failed,
                result.Skipped);
        }
        else
        {
            _logger.LogInformation(
                "STRM probe task finished: {Probed} probed, {Failed} failed",
                result.Probed,
                result.Failed);
        }
    }
}
