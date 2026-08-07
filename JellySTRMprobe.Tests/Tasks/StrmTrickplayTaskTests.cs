using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using JellySTRMprobe.Service;
using JellySTRMprobe.Tasks;
using MediaBrowser.Controller.Entities;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace JellySTRMprobe.Tests.Tasks;

public class StrmTrickplayTaskTests
{
    private readonly Mock<ITrickplayService> _mockTrickplayService;
    private readonly StrmTrickplayTask _task;

    public StrmTrickplayTaskTests()
    {
        _mockTrickplayService = new Mock<ITrickplayService>();

        TestHelpers.EnsurePluginInstance();

        _task = new StrmTrickplayTask(
            _mockTrickplayService.Object,
            new Mock<ILogger<StrmTrickplayTask>>().Object);
    }

    private static Video CreateStrmVideo(string path)
    {
        return new Video
        {
            Id = Guid.NewGuid(),
            Name = path,
            Path = path,
            IsShortcut = true,
            RunTimeTicks = TimeSpan.FromMinutes(90).Ticks,
        };
    }

    private void SetupCandidates(params Video[] videos)
    {
        _mockTrickplayService
            .Setup(s => s.GetCandidates(It.IsAny<Guid[]>(), It.IsAny<int>()))
            .Returns(videos);
    }

    [Fact]
    public void Task_HasCorrectMetadata()
    {
        _task.Name.Should().Be("Generate STRM Trickplay");
        _task.Key.Should().Be("StrmTrickplayGeneration");
        _task.Category.Should().Be("STRM Probe");
        _task.IsHidden.Should().BeFalse();
        _task.IsEnabled.Should().BeTrue();
        _task.IsLogged.Should().BeTrue();
    }

    [Fact]
    public void GetDefaultTriggers_ReturnsNoTriggers()
    {
        _task.GetDefaultTriggers().Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_WithNoCandidates_ReportsFullProgress()
    {
        SetupCandidates();

        double? lastProgress = null;
        var progress = new Progress<double>(v => lastProgress = v);

        await _task.ExecuteAsync(progress, CancellationToken.None);

        // Wait for async progress callback
        await Task.Delay(100);

        lastProgress.Should().Be(100);
        _mockTrickplayService.Verify(
            s => s.GenerateAsync(It.IsAny<Video>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_GeneratesForEveryCandidate()
    {
        var videos = new[]
        {
            CreateStrmVideo("/media/one.strm"),
            CreateStrmVideo("/media/two.strm"),
            CreateStrmVideo("/media/three.strm"),
        };

        SetupCandidates(videos);
        _mockTrickplayService
            .Setup(s => s.GenerateAsync(It.IsAny<Video>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        foreach (var video in videos)
        {
            _mockTrickplayService.Verify(
                s => s.GenerateAsync(video, It.IsAny<CancellationToken>()),
                Times.Once);
        }
    }

    [Fact]
    public async Task ExecuteAsync_PassesConfiguredLimitAndLibraries()
    {
        var libraryId = Guid.NewGuid();
        Plugin.Instance.Configuration.SelectedLibraryIds = new[] { libraryId };
        Plugin.Instance.Configuration.TrickplayMaxItemsPerRun = 7;

        SetupCandidates();

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        _mockTrickplayService.Verify(
            s => s.GetCandidates(It.Is<Guid[]>(ids => ids.Single() == libraryId), 7),
            Times.Once);

        Plugin.Instance.Configuration.SelectedLibraryIds = Array.Empty<Guid>();
        Plugin.Instance.Configuration.TrickplayMaxItemsPerRun = 50;
    }

    [Fact]
    public async Task ExecuteAsync_ContinuesAfterItemFailure()
    {
        var failing = CreateStrmVideo("/media/bad.strm");
        var working = CreateStrmVideo("/media/good.strm");

        SetupCandidates(failing, working);

        _mockTrickplayService
            .Setup(s => s.GenerateAsync(failing, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("ffmpeg exploded"));
        _mockTrickplayService
            .Setup(s => s.GenerateAsync(working, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        double? lastProgress = null;
        var progress = new Progress<double>(v => lastProgress = v);

        await _task.ExecuteAsync(progress, CancellationToken.None);
        await Task.Delay(100);

        _mockTrickplayService.Verify(
            s => s.GenerateAsync(working, It.IsAny<CancellationToken>()),
            Times.Once);
        lastProgress.Should().Be(100);
    }

    [Fact]
    public async Task ExecuteAsync_PropagatesCancellation()
    {
        SetupCandidates(CreateStrmVideo("/media/one.strm"));

        _mockTrickplayService
            .Setup(s => s.GenerateAsync(It.IsAny<Video>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var act = async () => await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
