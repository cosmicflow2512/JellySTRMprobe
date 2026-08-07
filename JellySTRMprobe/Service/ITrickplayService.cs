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
    /// </summary>
    /// <param name="selectedLibraryIds">Library IDs to filter by. Empty array means all libraries.</param>
    /// <param name="maxItems">Maximum number of candidates to return. Zero means unlimited.</param>
    /// <returns>A list of STRM videos that carry a usable duration.</returns>
    IReadOnlyList<Video> GetCandidates(Guid[] selectedLibraryIds, int maxItems);

    /// <summary>
    /// Generates trickplay tiles for a single video at every configured width.
    /// </summary>
    /// <param name="video">The video to process.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if at least one resolution was generated, false if the item was skipped.</returns>
    Task<bool> GenerateAsync(Video video, CancellationToken cancellationToken);
}
