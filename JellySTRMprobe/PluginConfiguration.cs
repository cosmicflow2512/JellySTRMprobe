using System;
using MediaBrowser.Model.Plugins;

namespace JellySTRMprobe;

/// <summary>
/// Plugin configuration for JellySTRMprobe.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets a value indicating whether catch-up mode is enabled.
    /// When enabled, new STRM items are automatically probed when added during library scans.
    /// </summary>
    public bool EnableCatchUpMode { get; set; } = true;

    /// <summary>
    /// Gets or sets the number of parallel probe operations.
    /// Higher values speed up probing but increase load on upstream servers.
    /// </summary>
    public int ProbeParallelism { get; set; } = 5;

    /// <summary>
    /// Gets or sets the per-item probe timeout in seconds.
    /// </summary>
    public int ProbeTimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// Gets or sets the cooldown delay in milliseconds between probe operations.
    /// Helps prevent connection exhaustion on upstream servers.
    /// </summary>
    public int ProbeCooldownMs { get; set; } = 200;

    /// <summary>
    /// Gets or sets a value indicating whether failed STRM files should be deleted after probing.
    /// When enabled, STRM files that fail to probe (dead/unavailable streams) are removed from disk.
    /// Files are recreated on the next library sync, giving them a fresh chance nightly.
    /// </summary>
    public bool DeleteFailedStrms { get; set; } = false;

    /// <summary>
    /// Gets or sets the maximum failure percentage before deletion is skipped.
    /// If the failure rate exceeds this threshold, no files are deleted (provider may be down).
    /// </summary>
    public int DeleteFailureThreshold { get; set; } = 10;

    /// <summary>
    /// Gets or sets the array of selected library IDs to probe.
    /// Empty array means probe all libraries.
    /// </summary>
    public Guid[] SelectedLibraryIds { get; set; } = Array.Empty<Guid>();

    /// <summary>
    /// Gets or sets a value indicating whether the STRM trickplay task is available.
    /// Off by default: every run streams each file in full, which is slow and expensive.
    /// While disabled the task is hidden from the dashboard and refuses to run.
    /// </summary>
    public bool EnableStrmTrickplay { get; set; } = false;

    /// <summary>
    /// Gets or sets the per-item time budget for trickplay generation, in minutes.
    /// Zero means no limit. Jellyfin already kills an ffmpeg that stops producing images,
    /// but an item that trickles along slowly can otherwise occupy a run for hours.
    /// </summary>
    public int TrickplayTimeoutMinutes { get; set; } = 60;

    /// <summary>
    /// Gets or sets the maximum number of items the trickplay task processes per run.
    /// Zero means unlimited. Trickplay extraction streams each file in full, so this
    /// limit keeps an accidental run from pulling the entire library through the provider.
    /// </summary>
    public int TrickplayMaxItemsPerRun { get; set; } = 50;

    /// <summary>
    /// Validates and clamps all configuration values to safe ranges.
    /// </summary>
    public void Validate()
    {
        ProbeParallelism = Math.Clamp(ProbeParallelism, 1, 20);
        ProbeTimeoutSeconds = Math.Clamp(ProbeTimeoutSeconds, 10, 300);
        ProbeCooldownMs = Math.Clamp(ProbeCooldownMs, 0, 5000);
        DeleteFailureThreshold = Math.Clamp(DeleteFailureThreshold, 1, 100);
        TrickplayMaxItemsPerRun = Math.Clamp(TrickplayMaxItemsPerRun, 0, 100000);
        TrickplayTimeoutMinutes = Math.Clamp(TrickplayTimeoutMinutes, 0, 1440);
    }
}
