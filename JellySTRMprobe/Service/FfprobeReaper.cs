using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace JellySTRMprobe.Service;

/// <summary>
/// Kills ffprobe processes that Jellyfin leaves behind when a probe is cancelled.
///
/// <para>
/// Jellyfin's <c>MediaEncoder.GetMediaInfoInternal</c> reads ffprobe's output with
/// <c>CopyToAsync(stream, cancellationToken)</c>. On cancellation that throws, the surrounding
/// <c>using (ProcessWrapper)</c> disposes, and <c>ProcessWrapper.Dispose()</c> only detaches:
/// it unhooks the Exited handler, calls <c>Untrack()</c> to remove itself from the encoder's
/// own <c>_runningProcesses</c> list, and then <c>Process.Dispose()</c> — which releases the
/// .NET handle without signalling the OS process. The ffprobe keeps running, untracked, and
/// keeps filling its <c>-probesize</c> buffer. The failure path ten lines above does call
/// <c>StopProcess</c>, so the omission is clearly an oversight; it is unchanged on master.
/// </para>
///
/// <para>
/// Verified against Jellyfin 12.0 through 12.4 and master. Once upstream kills the process on
/// cancellation this class becomes a no-op and can be removed.
/// </para>
/// </summary>
public class FfprobeReaper
{
    private readonly ILogger<FfprobeReaper> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="FfprobeReaper"/> class.
    /// </summary>
    /// <param name="logger">Instance of the <see cref="ILogger{FfprobeReaper}"/> interface.</param>
    public FfprobeReaper(ILogger<FfprobeReaper> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Gets a value indicating whether reaping is possible on this platform.
    /// Process discovery reads /proc, so this is Linux only.
    /// </summary>
    [SupportedOSPlatformGuard("linux")]
    public static bool IsSupported => OperatingSystem.IsLinux();

    /// <summary>
    /// Lists the PIDs of running ffprobe processes whose command line mentions the given URL.
    /// </summary>
    /// <param name="streamUrl">The stream URL to match on. A short or empty value matches nothing.</param>
    /// <returns>The matching process IDs; empty when unsupported or nothing matched.</returns>
    public IReadOnlySet<int> FindProbes(string? streamUrl)
    {
        // Matching on a short string risks killing unrelated work, so refuse to guess.
        var matches = new HashSet<int>();

        if (!IsSupported || string.IsNullOrEmpty(streamUrl) || streamUrl.Length < 16)
        {
            return matches;
        }

        foreach (var dir in EnumerateProcDirectories())
        {
            if (!int.TryParse(Path.GetFileName(dir), out var pid))
            {
                continue;
            }

            var cmdline = ReadCmdline(dir);

            if (cmdline is null)
            {
                continue;
            }

            if (cmdline.Contains("ffprobe", StringComparison.Ordinal)
                && cmdline.Contains(streamUrl, StringComparison.Ordinal))
            {
                matches.Add(pid);
            }
        }

        return matches;
    }

    /// <summary>
    /// Kills ffprobe processes matching the URL that were not already running beforehand.
    ///
    /// <para>
    /// The <paramref name="existingBefore"/> snapshot is what keeps this safe: only processes
    /// that appeared while this plugin was probing are killed, so an ffprobe started earlier by
    /// playback of the same item is left alone.
    /// </para>
    /// </summary>
    /// <param name="streamUrl">The stream URL to match on.</param>
    /// <param name="existingBefore">PIDs that already matched before the probe started.</param>
    /// <returns>The number of processes killed.</returns>
    public int ReapNew(string? streamUrl, IReadOnlySet<int> existingBefore)
    {
        var killed = 0;

        foreach (var pid in FindProbes(streamUrl))
        {
            if (existingBefore.Contains(pid))
            {
                continue;
            }

            if (TryKill(pid))
            {
                killed++;
            }
        }

        return killed;
    }

    private static IEnumerable<string> EnumerateProcDirectories()
    {
        try
        {
            return Directory.EnumerateDirectories("/proc");
        }
        catch (IOException)
        {
            return Array.Empty<string>();
        }
        catch (UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    private static string? ReadCmdline(string procDir)
    {
        try
        {
            // Arguments are NUL-separated; spaces keep substring matching intact.
            return File.ReadAllText(Path.Combine(procDir, "cmdline")).Replace('\0', ' ');
        }
        catch (Exception)
        {
            // The process exited between enumeration and read, or is not ours to inspect.
            return null;
        }
    }

    private bool TryKill(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            process.Kill();

            // Deliberately logs the PID only. STRM targets routinely embed provider
            // credentials in the URL, and the URL is the thing we matched on.
            _logger.LogWarning("Killed orphaned ffprobe process {Pid} left behind by a cancelled probe", pid);
            return true;
        }
        catch (ArgumentException)
        {
            // Already gone.
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not kill orphaned ffprobe process {Pid}", pid);
            return false;
        }
    }
}
