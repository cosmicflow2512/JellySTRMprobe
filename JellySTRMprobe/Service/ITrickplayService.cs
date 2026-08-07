using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;

namespace JellySTRMprobe.Service;

/// <summary>
/// Service for generating trickplay scrubbing previews for STRM items.
/// </summary>
public interface ITrickplayService
{
    /// <summary>
    /// Gets STRM videos that are eligible for trickplay generation.
    ///
    /// Items that already have trickplay data are excluded before <paramref name="maxItems"/>
    /// is applied, so a capped run advances through the library instead of re-selecting the
    /// same items every time.
    /// </summary>
    /// <param name="selectedLibraryIds">Library IDs to filter by. Empty array means all libraries.</param>
    /// <param name="maxItems">Maximum number of candidates to return. Zero means unlimited.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A list of STRM videos that carry a usable duration and have no trickplay yet.</returns>
    Task<IReadOnlyList<Video>> GetCandidatesAsync(Guid[] selectedLibraryIds, int maxItems, CancellationToken cancellationToken);

    /// <summary>
    /// Generates trickplay tiles for a single video at every configured width.
    /// </summary>
    /// <param name="video">The video to process.</param>
    /// <param name="timeoutMinutes">Time budget for this item. Zero means no limit.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if at least one resolution was generated, false if the item was skipped or timed out.</returns>
    Task<bool> GenerateAsync(Video video, int timeoutMinutes, CancellationToken cancellationToken);
}
