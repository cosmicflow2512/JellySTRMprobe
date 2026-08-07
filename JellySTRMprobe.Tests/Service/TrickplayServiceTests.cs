using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using JellySTRMprobe.Service;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Trickplay;
using Jellyfin.Database.Implementations.Entities;
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

        // Default: nothing in the library has trickplay yet.
        SetupTrickplayDone();

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

    /// <summary>
    /// Stubs the paged trickplay lookup so the given items count as already generated.
    /// The service pages until it gets a short page, so a single short page ends the loop.
    /// </summary>
    private void SetupTrickplayDone(params Guid[] itemIds)
    {
        var infos = itemIds.Select(id => new TrickplayInfo { ItemId = id, Width = 320 }).ToList();

        _mockTrickplayManager
            .Setup(t => t.GetTrickplayItemsAsync(It.IsAny<int>(), 0))
            .ReturnsAsync(infos);
    }

    // =====================
    // GetCandidates Tests
    // =====================

    [Fact]
    public async Task GetCandidates_ReturnsShortcutVideosWithDuration()
    {
        var video = CreateStrmVideo("/media/movie.strm", TimeSpan.FromMinutes(90).Ticks);
        SetupItems(video);

        var result = await _trickplayService.GetCandidatesAsync(Array.Empty<Guid>(), 0, CancellationToken.None);

        result.Should().ContainSingle().Which.Should().BeSameAs(video);
    }

    [Fact]
    public async Task GetCandidates_SkipsNonVideoItems()
    {
        var item = TestHelpers.CreateTestItem("Some Folder", "/media/folder");
        SetupItems(item);

        var result = await _trickplayService.GetCandidatesAsync(Array.Empty<Guid>(), 0, CancellationToken.None);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetCandidates_SkipsNonShortcutVideos()
    {
        var video = CreateStrmVideo("/media/real.mkv", TimeSpan.FromMinutes(90).Ticks, isShortcut: false);
        SetupItems(video);

        var result = await _trickplayService.GetCandidatesAsync(Array.Empty<Guid>(), 0, CancellationToken.None);

        result.Should().BeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0L)]
    public async Task GetCandidates_SkipsVideosWithoutUsableDuration(long? runTimeTicks)
    {
        var video = CreateStrmVideo("/media/unprobed.strm", runTimeTicks);
        SetupItems(video);

        var result = await _trickplayService.GetCandidatesAsync(Array.Empty<Guid>(), 0, CancellationToken.None);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetCandidates_SkipsUnresolvableItems()
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

        var result = await _trickplayService.GetCandidatesAsync(Array.Empty<Guid>(), 0, CancellationToken.None);

        result.Should().ContainSingle().Which.Should().BeSameAs(good);
    }

    [Fact]
    public async Task GetCandidates_RespectsMaxItems()
    {
        var videos = Enumerable.Range(0, 5)
            .Select(i => CreateStrmVideo($"/media/movie{i}.strm", TimeSpan.FromMinutes(90).Ticks))
            .ToArray();

        SetupItems(videos);

        var result = await _trickplayService.GetCandidatesAsync(Array.Empty<Guid>(), 2, CancellationToken.None);

        result.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetCandidates_MaxItemsZeroMeansUnlimited()
    {
        var videos = Enumerable.Range(0, 5)
            .Select(i => CreateStrmVideo($"/media/movie{i}.strm", TimeSpan.FromMinutes(90).Ticks))
            .ToArray();

        SetupItems(videos);

        var result = await _trickplayService.GetCandidatesAsync(Array.Empty<Guid>(), 0, CancellationToken.None);

        result.Should().HaveCount(5);
    }

    [Fact]
    public async Task GetCandidates_ExcludesItemsThatAlreadyHaveTrickplay()
    {
        var done = CreateStrmVideo("/media/done.strm", TimeSpan.FromMinutes(90).Ticks);
        var pending = CreateStrmVideo("/media/pending.strm", TimeSpan.FromMinutes(90).Ticks);

        SetupItems(done, pending);
        SetupTrickplayDone(done.Id);

        var result = await _trickplayService.GetCandidatesAsync(Array.Empty<Guid>(), 0, CancellationToken.None);

        result.Should().ContainSingle().Which.Should().BeSameAs(pending);
    }

    [Fact]
    public async Task GetCandidates_CappedRun_AdvancesPastCompletedItems()
    {
        // Regression: completed items must be filtered out *before* maxItems is applied.
        // Otherwise a capped run keeps re-selecting the same first N items, they all get
        // skipped downstream, and the rest of the library is never reached.
        var videos = Enumerable.Range(0, 5)
            .Select(i => CreateStrmVideo($"/media/movie{i}.strm", TimeSpan.FromMinutes(90).Ticks))
            .ToArray();

        SetupItems(videos);

        // Simulate the state after a first capped run of 2.
        SetupTrickplayDone(videos[0].Id, videos[1].Id);

        var result = await _trickplayService.GetCandidatesAsync(Array.Empty<Guid>(), 2, CancellationToken.None);

        result.Should().HaveCount(2);
        result.Should().BeEquivalentTo(new[] { videos[2], videos[3] }, o => o.WithoutStrictOrdering());
    }

    [Fact]
    public async Task GetCandidates_PagesThroughAllTrickplayRows()
    {
        var video = CreateStrmVideo("/media/movie.strm", TimeSpan.FromMinutes(90).Ticks);
        SetupItems(video);

        // A full first page forces a second request; the item is only on the second page.
        var fullPage = Enumerable.Range(0, 1000)
            .Select(_ => new TrickplayInfo { ItemId = Guid.NewGuid(), Width = 320 })
            .ToList();

        _mockTrickplayManager
            .Setup(t => t.GetTrickplayItemsAsync(1000, 0))
            .ReturnsAsync(fullPage);
        _mockTrickplayManager
            .Setup(t => t.GetTrickplayItemsAsync(1000, 1000))
            .ReturnsAsync(new List<TrickplayInfo> { new() { ItemId = video.Id, Width = 320 } });

        var result = await _trickplayService.GetCandidatesAsync(Array.Empty<Guid>(), 0, CancellationToken.None);

        result.Should().BeEmpty();
        _mockTrickplayManager.Verify(t => t.GetTrickplayItemsAsync(1000, 1000), Times.Once);
    }

    [Fact]
    public async Task GetCandidates_WithNoLibraryIds_DoesNotSetAncestorIds()
    {
        InternalItemsQuery? captured = null;
        _mockLibraryManager
            .Setup(l => l.GetItemIds(It.IsAny<InternalItemsQuery>()))
            .Callback<InternalItemsQuery>(q => captured = q)
            .Returns(new List<Guid>());

        await _trickplayService.GetCandidatesAsync(Array.Empty<Guid>(), 0, CancellationToken.None);

        captured.Should().NotBeNull();
        captured!.AncestorIds.Should().BeEmpty();
    }

    [Fact]
    public async Task GetCandidates_WithLibraryIds_SetsAncestorIds()
    {
        var libraryId = Guid.NewGuid();
        InternalItemsQuery? captured = null;

        _mockLibraryManager
            .Setup(l => l.GetItemIds(It.IsAny<InternalItemsQuery>()))
            .Callback<InternalItemsQuery>(q => captured = q)
            .Returns(new List<Guid>());

        await _trickplayService.GetCandidatesAsync(new[] { libraryId }, 0, CancellationToken.None);

        captured.Should().NotBeNull();
        captured!.AncestorIds.Should().BeEquivalentTo(new[] { libraryId });
    }
}
