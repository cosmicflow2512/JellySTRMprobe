using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using JellySTRMprobe.Service;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Trickplay;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace JellySTRMprobe.Tests.Service;

public class TrickplayServiceTests
{
    private readonly Mock<ILibraryManager> _mockLibraryManager;
    private readonly Mock<ITrickplayManager> _mockTrickplayManager;
    private readonly Mock<IMediaEncoder> _mockMediaEncoder;
    private readonly Mock<IServerConfigurationManager> _mockServerConfig;
    private readonly TrickplayService _trickplayService;

    public TrickplayServiceTests()
    {
        _mockLibraryManager = new Mock<ILibraryManager>();
        _mockTrickplayManager = new Mock<ITrickplayManager>();
        _mockMediaEncoder = new Mock<IMediaEncoder>();
        _mockServerConfig = new Mock<IServerConfigurationManager>();

        // EncodingHelper is a concrete class with no mockable surface and is only
        // touched by GenerateAsync, which these tests do not exercise.
        _trickplayService = new TrickplayService(
            _mockLibraryManager.Object,
            _mockTrickplayManager.Object,
            _mockMediaEncoder.Object,
            _mockServerConfig.Object,
            null!,
            new Mock<ILogger<TrickplayService>>().Object);
    }

    private static Video CreateStrmVideo(string path, long? runTimeTicks, bool isShortcut = true)
    {
        return new Video
        {
            Id = Guid.NewGuid(),
            Name = path,
            Path = path,
            IsShortcut = isShortcut,
            RunTimeTicks = runTimeTicks,
        };
    }

    private void SetupItems(params BaseItem[] items)
    {
        var ids = new List<Guid>();
        foreach (var item in items)
        {
            ids.Add(item.Id);
            _mockLibraryManager
                .Setup(l => l.GetItemById(item.Id))
                .Returns(item);
        }

        _mockLibraryManager
            .Setup(l => l.GetItemIds(It.IsAny<InternalItemsQuery>()))
            .Returns(ids);
    }

    // =====================
    // GetCandidates Tests
    // =====================

    [Fact]
    public void GetCandidates_ReturnsShortcutVideosWithDuration()
    {
        var video = CreateStrmVideo("/media/movie.strm", TimeSpan.FromMinutes(90).Ticks);
        SetupItems(video);

        var result = _trickplayService.GetCandidates(Array.Empty<Guid>(), 0);

        result.Should().ContainSingle().Which.Should().BeSameAs(video);
    }

    [Fact]
    public void GetCandidates_SkipsNonVideoItems()
    {
        var item = TestHelpers.CreateTestItem("Some Folder", "/media/folder");
        SetupItems(item);

        var result = _trickplayService.GetCandidates(Array.Empty<Guid>(), 0);

        result.Should().BeEmpty();
    }

    [Fact]
    public void GetCandidates_SkipsNonShortcutVideos()
    {
        var video = CreateStrmVideo("/media/real.mkv", TimeSpan.FromMinutes(90).Ticks, isShortcut: false);
        SetupItems(video);

        var result = _trickplayService.GetCandidates(Array.Empty<Guid>(), 0);

        result.Should().BeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0L)]
    public void GetCandidates_SkipsVideosWithoutUsableDuration(long? runTimeTicks)
    {
        var video = CreateStrmVideo("/media/unprobed.strm", runTimeTicks);
        SetupItems(video);

        var result = _trickplayService.GetCandidates(Array.Empty<Guid>(), 0);

        result.Should().BeEmpty();
    }

    [Fact]
    public void GetCandidates_SkipsUnresolvableItems()
    {
        var good = CreateStrmVideo("/media/good.strm", TimeSpan.FromMinutes(90).Ticks);
        var badId = Guid.NewGuid();

        _mockLibraryManager
            .Setup(l => l.GetItemIds(It.IsAny<InternalItemsQuery>()))
            .Returns(new List<Guid> { good.Id, badId });
        _mockLibraryManager
            .Setup(l => l.GetItemById(good.Id))
            .Returns(good);
        _mockLibraryManager
            .Setup(l => l.GetItemById(badId))
            .Throws(new InvalidOperationException("boom"));

        var result = _trickplayService.GetCandidates(Array.Empty<Guid>(), 0);

        result.Should().ContainSingle().Which.Should().BeSameAs(good);
    }

    [Fact]
    public void GetCandidates_RespectsMaxItems()
    {
        var videos = Enumerable.Range(0, 5)
            .Select(i => CreateStrmVideo($"/media/movie{i}.strm", TimeSpan.FromMinutes(90).Ticks))
            .ToArray();

        SetupItems(videos);

        var result = _trickplayService.GetCandidates(Array.Empty<Guid>(), 2);

        result.Should().HaveCount(2);
    }

    [Fact]
    public void GetCandidates_MaxItemsZeroMeansUnlimited()
    {
        var videos = Enumerable.Range(0, 5)
            .Select(i => CreateStrmVideo($"/media/movie{i}.strm", TimeSpan.FromMinutes(90).Ticks))
            .ToArray();

        SetupItems(videos);

        var result = _trickplayService.GetCandidates(Array.Empty<Guid>(), 0);

        result.Should().HaveCount(5);
    }

    [Fact]
    public void GetCandidates_WithNoLibraryIds_DoesNotSetAncestorIds()
    {
        InternalItemsQuery? captured = null;
        _mockLibraryManager
            .Setup(l => l.GetItemIds(It.IsAny<InternalItemsQuery>()))
            .Callback<InternalItemsQuery>(q => captured = q)
            .Returns(new List<Guid>());

        _trickplayService.GetCandidates(Array.Empty<Guid>(), 0);

        captured.Should().NotBeNull();
        captured!.AncestorIds.Should().BeEmpty();
    }

    [Fact]
    public void GetCandidates_WithLibraryIds_SetsAncestorIds()
    {
        var libraryId = Guid.NewGuid();
        InternalItemsQuery? captured = null;

        _mockLibraryManager
            .Setup(l => l.GetItemIds(It.IsAny<InternalItemsQuery>()))
            .Callback<InternalItemsQuery>(q => captured = q)
            .Returns(new List<Guid>());

        _trickplayService.GetCandidates(new[] { libraryId }, 0);

        captured.Should().NotBeNull();
        captured!.AncestorIds.Should().BeEquivalentTo(new[] { libraryId });
    }
}
