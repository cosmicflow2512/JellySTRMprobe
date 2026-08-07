using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Trickplay;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dto;
using Microsoft.Extensions.Logging;

namespace JellySTRMprobe.Service;

/// <summary>
/// Core service for generating trickplay tiles for STRM items.
///
/// Jellyfin skips STRM entirely: BaseVideoResolver sets IsShortcut = true and
/// TrickplayManager.CanGenerateTrickplay() returns false for shortcuts, so the
/// library option "Extract trickplay images" has no effect. On top of that,
/// TrickplayManager also bails on any media source whose path is not an existing
/// file, which rules out the HTTP URL a STRM resolves to.
///
/// This service calls the same pipeline directly
/// (IMediaEncoder -> ITrickplayManager.CreateTiles -> SaveTrickplayInfo),
/// bypassing both gates.
/// </summary>
public class TrickplayService : ITrickplayService
{
    /// <summary>
    /// Smallest interval ffmpeg is asked for. Mirrors TrickplayManager, which raises
    /// anything below this to avoid extracting an absurd number of frames.
    /// </summary>
    private const int MinIntervalMs = 1000;

    private readonly ILibraryManager _libraryManager;
    private readonly ITrickplayManager _trickplayManager;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly IServerConfigurationManager _serverConfig;
    private readonly EncodingHelper _encodingHelper;
    private readonly ILogger<TrickplayService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="TrickplayService"/> class.
    /// </summary>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="trickplayManager">Instance of the <see cref="ITrickplayManager"/> interface.</param>
    /// <param name="mediaEncoder">Instance of the <see cref="IMediaEncoder"/> interface.</param>
    /// <param name="serverConfig">Instance of the <see cref="IServerConfigurationManager"/> interface.</param>
    /// <param name="encodingHelper">Instance of the <see cref="EncodingHelper"/> class.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{TrickplayService}"/> interface.</param>
    public TrickplayService(
        ILibraryManager libraryManager,
        ITrickplayManager trickplayManager,
        IMediaEncoder mediaEncoder,
        IServerConfigurationManager serverConfig,
        EncodingHelper encodingHelper,
        ILogger<TrickplayService> logger)
    {
        _libraryManager = libraryManager;
        _trickplayManager = trickplayManager;
        _mediaEncoder = mediaEncoder;
        _serverConfig = serverConfig;
        _encodingHelper = encodingHelper;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Video>> GetCandidatesAsync(Guid[] selectedLibraryIds, int maxItems, CancellationToken cancellationToken)
    {
        // Jellyfin 10.11.x EF Core: TopParentIds and MediaTypes filters return 0
        // results. AncestorIds uses a join table and works correctly.
        var query = new InternalItemsQuery();

        if (selectedLibraryIds.Length > 0)
        {
            query.AncestorIds = selectedLibraryIds;
        }

        var itemIds = _libraryManager.GetItemIds(query);
        _logger.LogInformation("Query returned {Count} item IDs", itemIds.Count);

        // Items that already have trickplay must be filtered out *before* maxItems is
        // applied. Skipping them later would make a capped run re-select the same first
        // N items forever and never reach the rest of the library.
        var alreadyDone = await GetItemsWithTrickplayAsync(cancellationToken).ConfigureAwait(false);

        var candidates = new List<Video>();
        var skippedNoDuration = 0;
        var skippedHaveTrickplay = 0;

        foreach (var id in itemIds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            BaseItem? item;
            try
            {
                item = _libraryManager.GetItemById(id);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Skipping item {ItemId}: failed to resolve", id);
                continue;
            }

            if (item is not Video video || !video.IsShortcut)
            {
                continue;
            }

            if (video.RunTimeTicks is null or 0)
            {
                skippedNoDuration++;
                _logger.LogDebug("Skipping {Path}: no duration, run the probe task first", video.Path);
                continue;
            }

            if (alreadyDone.Contains(video.Id))
            {
                skippedHaveTrickplay++;
                continue;
            }

            candidates.Add(video);

            if (maxItems > 0 && candidates.Count >= maxItems)
            {
                _logger.LogInformation(
                    "Reached the per-run limit of {MaxItems} items; the next run continues with the remaining items",
                    maxItems);
                break;
            }
        }

        _logger.LogInformation(
            "Selected {Count} STRM videos for trickplay ({HaveTrickplay} already done, {NoDuration} skipped for missing duration)",
            candidates.Count,
            skippedHaveTrickplay,
            skippedNoDuration);

        return candidates;
    }

    /// <summary>
    /// Collects the IDs of every item that already has trickplay data, in one pass.
    ///
    /// An item counts as done once it has any trickplay row. Comparing against the
    /// configured widths instead would permanently stall a capped run: videos narrower
    /// than a configured width get their tiles at the clamped width, would never match,
    /// and would be re-selected on every run without ever producing anything new.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The set of item IDs that already have trickplay.</returns>
    private async Task<HashSet<Guid>> GetItemsWithTrickplayAsync(CancellationToken cancellationToken)
    {
        const int PageSize = 1000;

        var result = new HashSet<Guid>();
        var offset = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var page = await _trickplayManager.GetTrickplayItemsAsync(PageSize, offset).ConfigureAwait(false);

            foreach (var info in page)
            {
                result.Add(info.ItemId);
            }

            if (page.Count < PageSize)
            {
                break;
            }

            offset += PageSize;
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<bool> GenerateAsync(Video video, int timeoutMinutes, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        if (timeoutMinutes > 0)
        {
            timeoutCts.CancelAfter(TimeSpan.FromMinutes(timeoutMinutes));
        }

        try
        {
            return await GenerateCoreAsync(video, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // A killed ffmpeg surfaces as FfmpegException, not OperationCanceledException,
            // so the token has to be checked rather than the exception type.
            _logger.LogWarning(
                ex,
                "Trickplay timed out for {Path} after {Timeout} minutes; moving on to the next item",
                video.Path,
                timeoutMinutes);
            return false;
        }
    }

    /// <summary>
    /// Generates trickplay tiles for a single video at every configured width.
    /// </summary>
    /// <param name="video">The video to process.</param>
    /// <param name="cancellationToken">Token that already carries the per-item time budget.</param>
    /// <returns>True if at least one resolution was generated.</returns>
    private async Task<bool> GenerateCoreAsync(Video video, CancellationToken cancellationToken)
    {
        var options = CloneWithMinInterval(_serverConfig.Configuration.TrickplayOptions);

        // Note: media sources under parent items exist as their own video/item as well.
        // Only use this video's own stream for trickplay, same as TrickplayManager does.
        var mediaSource = video.GetMediaSources(false)
            .FirstOrDefault(source => Guid.TryParse(source.Id, out var sourceId) && sourceId.Equals(video.Id));

        if (mediaSource?.VideoStream is null)
        {
            _logger.LogWarning("No video stream for {Path}, run the probe task first", video.Path);
            return false;
        }

        var inputPath = await ResolveInputPathAsync(video, mediaSource, cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrEmpty(inputPath))
        {
            _logger.LogWarning("Could not resolve a stream URL for {Path}", video.Path);
            return false;
        }

        var existing = await _trickplayManager.GetTrickplayResolutions(video.Id).ConfigureAwait(false);
        var saveWithMedia = _libraryManager.GetLibraryOptions(video)?.SaveTrickplayWithMedia ?? false;
        var generated = false;

        foreach (var width in options.WidthResolutions)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // The width has to be even, otherwise a lot of filters will not be able to sample it.
            // Force using the video width when the trickplay setting has a too large width.
            var actualWidth = 2 * (width / 2);
            var videoWidth = mediaSource.VideoStream.Width;

            if (videoWidth is not null && videoWidth < width)
            {
                actualWidth = 2 * (videoWidth.Value / 2);
            }

            if (existing.ContainsKey(actualWidth))
            {
                _logger.LogDebug("Trickplay at {Width} width already exists for {Path}", actualWidth, video.Path);
                continue;
            }

            if (await GenerateWidthAsync(video, mediaSource, inputPath, actualWidth, options, saveWithMedia, cancellationToken).ConfigureAwait(false))
            {
                generated = true;
            }
        }

        return generated;
    }

    /// <summary>
    /// Copies the server trickplay options, raising an out-of-range interval to the minimum.
    /// A copy is used so the live server configuration object is never mutated.
    /// </summary>
    /// <param name="source">The server trickplay options.</param>
    /// <returns>A copy safe to hand to the encoder and to CreateTiles.</returns>
    private static TrickplayOptions CloneWithMinInterval(TrickplayOptions source)
    {
        return new TrickplayOptions
        {
            EnableHwAcceleration = source.EnableHwAcceleration,
            EnableHwEncoding = source.EnableHwEncoding,
            EnableKeyFrameOnlyExtraction = source.EnableKeyFrameOnlyExtraction,
            ScanBehavior = source.ScanBehavior,
            ProcessPriority = source.ProcessPriority,
            Interval = Math.Max(source.Interval, MinIntervalMs),
            WidthResolutions = source.WidthResolutions,
            TileWidth = source.TileWidth,
            TileHeight = source.TileHeight,
            Qscale = source.Qscale,
            JpegQuality = source.JpegQuality,
            ProcessThreads = source.ProcessThreads,
        };
    }

    /// <summary>
    /// Determines the path handed to ffmpeg.
    ///
    /// For a shortcut, BaseItem.GetVersionInfo already replaces the media source path with
    /// Video.ShortcutPath (the stream URL) and sets the protocol accordingly, so the .strm
    /// file itself never reaches ffmpeg. The file is only read as a fallback for items whose
    /// ShortcutPath was never populated.
    /// </summary>
    /// <param name="video">The video being processed.</param>
    /// <param name="mediaSource">The media source resolved for the video.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The stream URL, or null if it could not be determined.</returns>
    private async Task<string?> ResolveInputPathAsync(Video video, MediaSourceInfo mediaSource, CancellationToken cancellationToken)
    {
        var path = mediaSource.Path;

        if (!string.IsNullOrEmpty(path) && !path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase))
        {
            return path;
        }

        if (string.IsNullOrEmpty(video.Path))
        {
            return null;
        }

        try
        {
            var lines = await File.ReadAllLinesAsync(video.Path, cancellationToken).ConfigureAwait(false);

            var url = lines.FirstOrDefault(line =>
                !string.IsNullOrWhiteSpace(line) && !line.TrimStart().StartsWith('#'));

            if (url is not null)
            {
                _logger.LogDebug("Read stream URL from {Path} directly (ShortcutPath was not set)", video.Path);
                return url.Trim();
            }
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Could not read STRM file {Path}", video.Path);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Could not read STRM file {Path}", video.Path);
        }

        return null;
    }

    /// <summary>
    /// Extracts thumbnails and builds the tiles for one width.
    /// </summary>
    /// <param name="video">The video being processed.</param>
    /// <param name="mediaSource">The media source resolved for the video.</param>
    /// <param name="inputPath">The stream URL handed to ffmpeg.</param>
    /// <param name="width">The already normalized target width.</param>
    /// <param name="options">The trickplay options to use.</param>
    /// <param name="saveWithMedia">Whether tiles are stored next to the media.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if tiles were created and recorded.</returns>
    private async Task<bool> GenerateWidthAsync(
        Video video,
        MediaSourceInfo mediaSource,
        string inputPath,
        int width,
        TrickplayOptions options,
        bool saveWithMedia,
        CancellationToken cancellationToken)
    {
        string? imgTempDir = null;

        try
        {
            // The stream URL is deliberately not logged: STRM targets routinely embed
            // provider credentials in the query string.
            _logger.LogInformation(
                "Creating trickplay files at {Width} width, for {Path} [ID: {ItemId}]",
                width,
                video.Path,
                video.Id);

            imgTempDir = await _mediaEncoder.ExtractVideoImagesOnIntervalAccelerated(
                inputPath,
                mediaSource.Container ?? string.Empty,
                mediaSource,
                mediaSource.VideoStream,
                width,
                TimeSpan.FromMilliseconds(options.Interval),
                options.EnableHwAcceleration,
                options.EnableHwEncoding,
                options.ProcessThreads,
                options.Qscale,
                options.ProcessPriority,
                options.EnableKeyFrameOnlyExtraction,
                _encodingHelper,
                cancellationToken).ConfigureAwait(false);

            if (string.IsNullOrEmpty(imgTempDir) || !Directory.Exists(imgTempDir))
            {
                _logger.LogWarning("Media encoder returned no image directory for {Path}", video.Path);
                return false;
            }

            var images = Directory.GetFiles(imgTempDir, "*.jpg")
                .OrderBy(i => i, StringComparer.Ordinal)
                .ToList();

            if (images.Count == 0)
            {
                _logger.LogWarning("No thumbnails extracted for {Path}", video.Path);
                return false;
            }

            var outputDir = _trickplayManager.GetTrickplayDirectory(
                video,
                options.TileWidth,
                options.TileHeight,
                width,
                saveWithMedia);

            var info = _trickplayManager.CreateTiles(images, width, options, outputDir);

            try
            {
                // CreateTiles does not set ItemId. Without this the tiles sit on disk
                // and no client will ever find them.
                info.ItemId = video.Id;
                await _trickplayManager.SaveTrickplayInfo(info).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error while saving trickplay tiles info for {Path}", video.Path);

                // Make sure no orphaned tiles stay behind if the info row was not saved.
                TryDeleteDirectory(outputDir);
                return false;
            }

            _logger.LogInformation(
                "Finished creation of trickplay files for {Path}: {Count} thumbnails at {Width}x{Height}",
                video.Path,
                info.ThumbnailCount,
                info.Width,
                info.Height);

            return true;
        }
        finally
        {
            if (!string.IsNullOrEmpty(imgTempDir))
            {
                TryDeleteDirectory(imgTempDir);
            }
        }
    }

    /// <summary>
    /// Deletes a directory tree, logging instead of throwing on failure.
    /// </summary>
    /// <param name="path">The directory to remove.</param>
    private void TryDeleteDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        try
        {
            Directory.Delete(path, true);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Could not delete directory {Dir}", path);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Could not delete directory {Dir}", path);
        }
    }
}
