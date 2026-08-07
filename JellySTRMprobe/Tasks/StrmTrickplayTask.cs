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
    public bool IsHidden => false;

    /// <inheritdoc />
    public bool IsEnabled => true;

    /// <inheritdoc />
    public bool IsLogged => true;

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
        config.Validate();

        _logger.LogInformation("Starting STRM trickplay task");

        var candidates = _trickplayService.GetCandidates(config.SelectedLibraryIds, config.TrickplayMaxItemsPerRun);

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
                if (await _trickplayService.GenerateAsync(video, cancellationToken).ConfigureAwait(false))
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
