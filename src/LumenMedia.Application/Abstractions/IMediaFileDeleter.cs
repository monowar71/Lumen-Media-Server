namespace LumenMedia.Application.Abstractions;

/// <summary>
/// Deletes library media files from disk after path-canonicalization and root checks
/// (same safety model as download/stream).
/// </summary>
public interface IMediaFileDeleter
{
    /// <summary>
    /// Deletes <paramref name="path"/> when it exists and resolves under one of
    /// <paramref name="libraryRoots"/>. Returns false when the file is missing or outside roots
    /// (DB cleanup may still proceed).
    /// </summary>
    bool TryDelete(string path, IReadOnlyList<string> libraryRoots);
}
