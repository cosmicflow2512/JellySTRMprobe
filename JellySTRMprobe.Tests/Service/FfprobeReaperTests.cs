using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using FluentAssertions;
using JellySTRMprobe.Service;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace JellySTRMprobe.Tests.Service;

public class FfprobeReaperTests
{
    private readonly FfprobeReaper _reaper;

    public FfprobeReaperTests()
    {
        _reaper = new FfprobeReaper(new Mock<ILogger<FfprobeReaper>>().Object);
    }

    // =====================
    // Safety guards
    // =====================
    //
    // These matter more than the happy path: the reaper kills processes it did not start,
    // so every input that could widen the match must be refused.

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("http://a")]
    [InlineData("ffprobe")]
    [InlineData("/data/x.strm")]
    public void FindProbes_RefusesShortOrEmptyUrls(string? streamUrl)
    {
        // A short needle would match unrelated command lines.
        _reaper.FindProbes(streamUrl).Should().BeEmpty();
    }

    [Fact]
    public void FindProbes_DoesNotMatchThisTestProcess()
    {
        // The current process is not ffprobe, so nothing may come back however long the needle.
        var ownPid = Environment.ProcessId;

        _reaper.FindProbes("this-needle-matches-no-running-command-line-whatsoever")
            .Should().NotContain(ownPid);
    }

    [Fact]
    public void ReapNew_KillsNothingWhenEverythingWasAlreadyRunning()
    {
        var url = "http://192.0.2.1:3000/view/" + Guid.NewGuid().ToString("N");
        var before = _reaper.FindProbes(url);

        // Nothing started in between, so nothing is new, so nothing is killed.
        _reaper.ReapNew(url, before).Should().Be(0);
    }

    [Fact]
    public void ReapNew_WithUnmatchableUrl_KillsNothing()
    {
        _reaper.ReapNew("http://192.0.2.1/" + Guid.NewGuid().ToString("N"), new HashSet<int>())
            .Should().Be(0);
    }

    // =====================
    // Discovery
    // =====================

    [Fact]
    public void FindProbes_FindsAProcessCarryingTheUrl()
    {
        // Process discovery reads /proc, so this exercises nothing off Linux.
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // Stand-in for ffprobe: the match is on the command line, and the path has to
        // contain "ffprobe" for the filter to accept it.
        var url = "http://192.0.2.1:3000/view/" + Guid.NewGuid().ToString("N");
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        var fake = System.IO.Path.Combine(dir, "ffprobe-stub");
        System.IO.File.WriteAllText(fake, "#!/bin/sh\nsleep 30\n");
        System.IO.File.SetUnixFileMode(
            fake,
            System.IO.UnixFileMode.UserRead | System.IO.UnixFileMode.UserWrite | System.IO.UnixFileMode.UserExecute);

        using var process = Process.Start(new ProcessStartInfo(fake, url) { UseShellExecute = false });
        process.Should().NotBeNull();

        try
        {
            var found = WaitForPid(url, process!.Id);
            found.Should().Contain(process.Id);

            // A pid present in the snapshot is left alone.
            _reaper.ReapNew(url, found).Should().Be(0);
            process.HasExited.Should().BeFalse();

            // Treated as new, it gets killed.
            _reaper.ReapNew(url, new HashSet<int>()).Should().Be(1);
            process.WaitForExit(10_000).Should().BeTrue();
        }
        finally
        {
            try
            {
                if (!process!.HasExited)
                {
                    process.Kill();
                }
            }
            catch (InvalidOperationException)
            {
                // Already gone.
            }

            System.IO.Directory.Delete(dir, true);
        }
    }

    private IReadOnlySet<int> WaitForPid(string url, int pid)
    {
        // Process startup is asynchronous; /proc may not show the command line immediately.
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var found = _reaper.FindProbes(url);
            if (found.Contains(pid))
            {
                return found;
            }

            System.Threading.Thread.Sleep(100);
        }

        return _reaper.FindProbes(url);
    }
}
