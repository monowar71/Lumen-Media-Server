using FreePlex.Domain.Media;

namespace FreePlex.Application.Abstractions;

public interface ISubtitleConverter
{
    /// <summary>
    /// Produces WebVTT for a text subtitle stream (external file or embedded track).
    /// Returns null when the stream is missing / bitmap / conversion failed.
    /// </summary>
    Task<string?> ToWebVttAsync(MediaSource source, MediaStream stream, CancellationToken ct);
}
