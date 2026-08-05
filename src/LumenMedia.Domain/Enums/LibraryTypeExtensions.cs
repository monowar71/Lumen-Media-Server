namespace LumenMedia.Domain.Enums;

/// <summary>
/// Maps library type to the media kinds it accepts during scan.
/// Enables Movies and Series libraries to share one filesystem root without moving files:
/// each library imports only filenames that parse as its kind.
/// </summary>
public static class LibraryTypeExtensions
{
    /// <summary>
    /// Whether a parsed release <paramref name="kind"/> belongs in a library of
    /// <paramref name="libraryType"/>. Parser uses <see cref="MediaKind.Series"/> for episode
    /// filenames (not <see cref="MediaKind.Episode"/>).
    /// </summary>
    public static bool Accepts(this LibraryType libraryType, MediaKind kind) =>
        libraryType switch
        {
            LibraryType.Movies => kind == MediaKind.Movie,
            LibraryType.Series => kind == MediaKind.Series,
            // Mixed catalog: classify each video inside a .torrent via NameParser.
            LibraryType.Torrent => kind is MediaKind.Movie or MediaKind.Series,
            _ => false,
        };
}
