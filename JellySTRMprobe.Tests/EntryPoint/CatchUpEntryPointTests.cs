using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using JellySTRMprobe.EntryPoint;
using JellySTRMprobe.Service;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace JellySTRMprobe.Tests.EntryPoint;

public class CatchUpEntryPointTests
{
    private readonly Mock<ILibraryManager> _mockLibraryManager;
    private readonly Mock<IProbeService> _mockProbeService;
    private readonly Mock<ILogger<CatchUpEntryPoint>> _mockLogger;

    public CatchUpEntryPointTests()
    {
        _mockLibraryManager = new Mock<ILibraryManager>();
        _mockProbeService = new Mock<IProbeService>();
        _mockLogger = new Mock<ILogger<CatchUpEntryPoint>>();

        TestHelpers.EnsurePluginInstance();
    }

    private CatchUpEntryPoint CreateEntryPoint()
    {
        return new CatchUpEntryPoint(
            _mockLibraryManager.Object,
            _mockProbeService.Object,
            _mockLogger.Object);
    }

    // Creates an entry point with a near-zero debounce window and captures the item
    // list handed to ProbeBatchAsync, so tests can assert the probe path fires
    // without waiting the production 30-second debounce.
    private (CatchUpEntryPoint EntryPoint, TaskCompletionSource<IReadOnlyList<BaseItem>> Probed) CreateEntryPointWithProbeCapture()
    {
        var probed = new TaskCompletionSource<IReadOnlyList<BaseItem>>(TaskCreationOptions.RunContinuationsAsynchronously);

        _mockProbeService
            .Setup(s => s.ProbeBatchAsync(
                It.IsAny<IReadOnlyList<BaseItem>>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<IProgress<double>>(),
                It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<BaseItem>, int, int, int, IProgress<double>, CancellationToken>(
                (items, _, _, _, _, _) => probed.TrySetResult(items))
            .ReturnsAsync(new ProbeResult());

        var entryPoint = CreateEntryPoint();
        entryPoint.DebounceDelay = TimeSpan.FromMilliseconds(30);
        return (entryPoint, probed);
    }

    [Fact]
    public async Task StartAsync_AlwaysSubscribesToItemAdded()
    {
        var entryPoint = CreateEntryPoint();

        await entryPoint.StartAsync(CancellationToken.None);

        _mockLibraryManager.VerifyAdd(l => l.ItemAdded += It.IsAny<EventHandler<ItemChangeEventArgs>>(), Times.Once);

        entryPoint.Dispose();
    }

    [Fact]
    public async Task OnItemAdded_WithStrmPath_WhenEnabled_EnqueuesItem()
    {
        Plugin.Instance.Configuration.EnableCatchUpMode = true;
        var entryPoint = CreateEntryPoint();
        await entryPoint.StartAsync(CancellationToken.None);

        var item = TestHelpers.CreateTestItem("Test Movie", "/media/test.strm");
        var eventArgs = new ItemChangeEventArgs { Item = item };

        // Raise the event — should not throw
        _mockLibraryManager.Raise(l => l.ItemAdded += null!, this, eventArgs);

        entryPoint.Dispose();
    }

    [Fact]
    public async Task OnItemAdded_WithStrmPath_WhenDisabled_DoesNotEnqueue()
    {
        Plugin.Instance.Configuration.EnableCatchUpMode = false;
        var entryPoint = CreateEntryPoint();
        await entryPoint.StartAsync(CancellationToken.None);

        var item = TestHelpers.CreateTestItem("Test Movie", "/media/test.strm");
        var eventArgs = new ItemChangeEventArgs { Item = item };

        _mockLibraryManager.Raise(l => l.ItemAdded += null!, this, eventArgs);

        // Wait to ensure nothing happens
        await Task.Delay(100);

        _mockProbeService.Verify(s => s.ProbeBatchAsync(
            It.IsAny<IReadOnlyList<BaseItem>>(),
            It.IsAny<int>(),
            It.IsAny<int>(),
            It.IsAny<int>(),
            It.IsAny<IProgress<double>>(),
            It.IsAny<CancellationToken>()), Times.Never);

        entryPoint.Dispose();
    }

    [Fact]
    public async Task OnItemAdded_WithNonStrmPath_DoesNotEnqueueItem()
    {
        Plugin.Instance.Configuration.EnableCatchUpMode = true;
        var entryPoint = CreateEntryPoint();
        await entryPoint.StartAsync(CancellationToken.None);

        var item = TestHelpers.CreateTestItem("Test Movie", "/media/test.mkv");
        var eventArgs = new ItemChangeEventArgs { Item = item };

        _mockLibraryManager.Raise(l => l.ItemAdded += null!, this, eventArgs);

        // Wait to ensure nothing happens
        await Task.Delay(100);

        _mockProbeService.Verify(s => s.ProbeBatchAsync(
            It.IsAny<IReadOnlyList<BaseItem>>(),
            It.IsAny<int>(),
            It.IsAny<int>(),
            It.IsAny<int>(),
            It.IsAny<IProgress<double>>(),
            It.IsAny<CancellationToken>()), Times.Never);

        entryPoint.Dispose();
    }

    [Fact]
    public async Task OnItemAdded_WithNewStrmItem_TriggersProbeForThatItem()
    {
        Plugin.Instance.Configuration.EnableCatchUpMode = true;
        var (entryPoint, probed) = CreateEntryPointWithProbeCapture();
        await entryPoint.StartAsync(CancellationToken.None);

        var item = TestHelpers.CreateTestItem("New Episode", "/media/new.strm");
        _mockLibraryManager.Raise(l => l.ItemAdded += null!, this, new ItemChangeEventArgs { Item = item });

        var completed = await Task.WhenAny(probed.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        completed.Should().Be(probed.Task, "the debounce timer should fire and probe the queued STRM item");
        (await probed.Task).Should().ContainSingle().Which.Should().BeSameAs(item);

        entryPoint.Dispose();
    }

    [Fact]
    public async Task OnItemAdded_WithStrmItemHavingStreamsButNoRunTimeTicks_TriggersProbe()
    {
        Plugin.Instance.Configuration.EnableCatchUpMode = true;
        var (entryPoint, probed) = CreateEntryPointWithProbeCapture();
        await entryPoint.StartAsync(CancellationToken.None);

        // Half-item: streams are present but the duration is missing — must still probe.
        var streams = new List<MediaStream> { new MediaStream { Type = MediaStreamType.Video } };
        var item = TestHelpers.CreateTestItem("Half Item", "/media/half.strm", streams, runTimeTicks: null);
        _mockLibraryManager.Raise(l => l.ItemAdded += null!, this, new ItemChangeEventArgs { Item = item });

        var completed = await Task.WhenAny(probed.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        completed.Should().Be(probed.Task, "an added STRM item with streams but no duration must still be probed");
        (await probed.Task).Should().ContainSingle().Which.Should().BeSameAs(item);

        entryPoint.Dispose();
    }

    [Fact]
    public async Task OnItemAdded_WithFullyProbedStrmItem_DoesNotProbe()
    {
        Plugin.Instance.Configuration.EnableCatchUpMode = true;
        var (entryPoint, probed) = CreateEntryPointWithProbeCapture();
        await entryPoint.StartAsync(CancellationToken.None);

        // Fully probed: streams AND a valid duration → nothing to do.
        var streams = new List<MediaStream>
        {
            new MediaStream { Type = MediaStreamType.Video },
            new MediaStream { Type = MediaStreamType.Audio },
        };
        var item = TestHelpers.CreateTestItem(
            "Fully Probed",
            "/media/probed.strm",
            streams,
            runTimeTicks: TimeSpan.FromMinutes(20).Ticks);
        _mockLibraryManager.Raise(l => l.ItemAdded += null!, this, new ItemChangeEventArgs { Item = item });

        // Wait comfortably past the debounce; the queue drains but nothing is probed.
        await Task.Delay(300);

        probed.Task.IsCompleted.Should().BeFalse();
        _mockProbeService.Verify(s => s.ProbeBatchAsync(
            It.IsAny<IReadOnlyList<BaseItem>>(),
            It.IsAny<int>(),
            It.IsAny<int>(),
            It.IsAny<int>(),
            It.IsAny<IProgress<double>>(),
            It.IsAny<CancellationToken>()), Times.Never);

        entryPoint.Dispose();
    }

    [Fact]
    public async Task StopAsync_UnsubscribesFromEvents()
    {
        var entryPoint = CreateEntryPoint();
        await entryPoint.StartAsync(CancellationToken.None);

        await entryPoint.StopAsync(CancellationToken.None);

        _mockLibraryManager.VerifyRemove(l => l.ItemAdded -= It.IsAny<EventHandler<ItemChangeEventArgs>>(), Times.Once);

        entryPoint.Dispose();
    }

    [Fact]
    public async Task Dispose_UnsubscribesFromEvents()
    {
        var entryPoint = CreateEntryPoint();
        await entryPoint.StartAsync(CancellationToken.None);

        entryPoint.Dispose();

        _mockLibraryManager.VerifyRemove(l => l.ItemAdded -= It.IsAny<EventHandler<ItemChangeEventArgs>>(), Times.AtLeastOnce);
    }

    [Fact]
    public void Dispose_CalledMultipleTimes_DoesNotThrow()
    {
        var entryPoint = CreateEntryPoint();

        var act = () =>
        {
            entryPoint.Dispose();
            entryPoint.Dispose();
        };

        act.Should().NotThrow();
    }
}
