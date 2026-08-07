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

        // The feature ships off; every test below that exercises a run needs it on.
        // Tests share the configuration singleton, so the ones that change it reset it.
        Plugin.Instance.Configuration.EnableStrmTrickplay = true;

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
            .Setup(s => s.GetCandidatesAsync(It.IsAny<Guid[]>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(videos);
    }

    [Fact]
    public void Task_HasCorrectMetadata()
    {
        _task.Name.Should().Be("Generate STRM Trickplay");
        _task.Key.Should().Be("StrmTrickplayGeneration");
        _task.Category.Should().Be("STRM Probe");
        _task.IsLogged.Should().BeTrue();
    }

    [Fact]
    public void Task_IsVisibleAndEnabled_WhenTrickplayIsOn()
    {
        Plugin.Instance.Configuration.EnableStrmTrickplay = true;

        _task.IsHidden.Should().BeFalse();
        _task.IsEnabled.Should().BeTrue();
    }

    [Fact]
    public void Task_IsHiddenAndDisabled_WhenTrickplayIsOff()
    {
        Plugin.Instance.Configuration.EnableStrmTrickplay = false;

        try
        {
            _task.IsHidden.Should().BeTrue();
            _task.IsEnabled.Should().BeFalse();
        }
        finally
        {
            Plugin.Instance.Configuration.EnableStrmTrickplay = true;
        }
    }

    [Fact]
    public async Task ExecuteAsync_WhenTrickplayIsOff_DoesNothing()
    {
        Plugin.Instance.Configuration.EnableStrmTrickplay = false;

        SetupCandidates(CreateStrmVideo("/media/one.strm"));

        double? lastProgress = null;
        var progress = new Progress<double>(v => lastProgress = v);

        try
        {
            await _task.ExecuteAsync(progress, CancellationToken.None);
            await Task.Delay(100);
        }
        finally
        {
            Plugin.Instance.Configuration.EnableStrmTrickplay = true;
        }

        lastProgress.Should().Be(100);
        _mockTrickplayService.Verify(
            s => s.GetCandidatesAsync(It.IsAny<Guid[]>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _mockTrickplayService.Verify(
            s => s.GenerateAsync(It.IsAny<Video>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
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
            s => s.GenerateAsync(It.IsAny<Video>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
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
            .Setup(s => s.GenerateAsync(It.IsAny<Video>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        foreach (var video in videos)
        {
            _mockTrickplayService.Verify(
                s => s.GenerateAsync(video, It.IsAny<int>(), It.IsAny<CancellationToken>()),
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
            s => s.GetCandidatesAsync(It.Is<Guid[]>(ids => ids.Single() == libraryId), 7, It.IsAny<CancellationToken>()),
            Times.Once);

        Plugin.Instance.Configuration.SelectedLibraryIds = Array.Empty<Guid>();
        Plugin.Instance.Configuration.TrickplayMaxItemsPerRun = 50;
    }

    [Fact]
    public async Task ExecuteAsync_PassesConfiguredTimeout()
    {
        Plugin.Instance.Configuration.TrickplayTimeoutMinutes = 25;

        SetupCandidates(CreateStrmVideo("/media/one.strm"));
        _mockTrickplayService
            .Setup(s => s.GenerateAsync(It.IsAny<Video>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        try
        {
            await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);
        }
        finally
        {
            Plugin.Instance.Configuration.TrickplayTimeoutMinutes = 60;
        }

        _mockTrickplayService.Verify(
            s => s.GenerateAsync(It.IsAny<Video>(), 25, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_TranslatesFfmpegKillOnCancellationIntoCancellation()
    {
        // Cancelling the task kills ffmpeg, which surfaces as a plain exception rather
        // than OperationCanceledException. That must not be reported as a failure.
        using var cts = new CancellationTokenSource();

        SetupCandidates(CreateStrmVideo("/media/one.strm"));
        _mockTrickplayService
            .Setup(s => s.GenerateAsync(It.IsAny<Video>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                await cts.CancelAsync();
                throw new InvalidOperationException("ffmpeg image extraction failed");
            });

        var act = async () => await _task.ExecuteAsync(new Progress<double>(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ExecuteAsync_ContinuesAfterItemFailure()
    {
        var failing = CreateStrmVideo("/media/bad.strm");
        var working = CreateStrmVideo("/media/good.strm");

        SetupCandidates(failing, working);

        _mockTrickplayService
            .Setup(s => s.GenerateAsync(failing, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("ffmpeg exploded"));
        _mockTrickplayService
            .Setup(s => s.GenerateAsync(working, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        double? lastProgress = null;
        var progress = new Progress<double>(v => lastProgress = v);

        await _task.ExecuteAsync(progress, CancellationToken.None);
        await Task.Delay(100);

        _mockTrickplayService.Verify(
            s => s.GenerateAsync(working, It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Once);
        lastProgress.Should().Be(100);
    }

    [Fact]
    public async Task ExecuteAsync_PropagatesCancellation()
    {
        SetupCandidates(CreateStrmVideo("/media/one.strm"));

        _mockTrickplayService
            .Setup(s => s.GenerateAsync(It.IsAny<Video>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var act = async () => await _task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
