using LumenMedia.Domain.Enums;

namespace LumenMedia.Domain.Libraries;

public class Library
{
    private readonly List<LibraryPath> _paths = [];
    private readonly List<string> _metadataProviders = [];

    private Library() { }

    public Library(
        string name,
        LibraryType type,
        IEnumerable<string> paths,
        DateTimeOffset now,
        string preferredLanguage = "ru-RU",
        IEnumerable<string>? metadataProviders = null,
        bool autoScan = true)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Library name is required", nameof(name));

        Id = Guid.CreateVersion7();
        Name = name;
        Type = type;
        PreferredLanguage = preferredLanguage;
        AutoScan = autoScan;
        CreatedAt = now;
        if (metadataProviders is not null)
            _metadataProviders.AddRange(metadataProviders);
        foreach (var p in paths)
            AddPath(p);
    }

    public Guid Id { get; private set; }
    public string Name { get; private set; } = null!;
    public LibraryType Type { get; private set; }
    public string PreferredLanguage { get; private set; } = "ru-RU";
    public bool AutoScan { get; private set; }
    public DateTimeOffset? LastScanAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public IReadOnlyList<LibraryPath> Paths => _paths;
    public IReadOnlyList<string> MetadataProviders => _metadataProviders;

    public void AddPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Path is required", nameof(path));
        if (_paths.Any(p => string.Equals(p.Path, path, StringComparison.Ordinal)))
            return;
        _paths.Add(new LibraryPath(Id, path));
    }

    public void ReplacePaths(IEnumerable<string> paths)
    {
        _paths.Clear();
        foreach (var p in paths)
            AddPath(p);
    }

    public void Update(string? name, string? preferredLanguage, IEnumerable<string>? metadataProviders, bool? autoScan)
    {
        if (!string.IsNullOrWhiteSpace(name)) Name = name;
        if (!string.IsNullOrWhiteSpace(preferredLanguage)) PreferredLanguage = preferredLanguage;
        if (metadataProviders is not null)
        {
            _metadataProviders.Clear();
            _metadataProviders.AddRange(metadataProviders);
        }
        if (autoScan is not null) AutoScan = autoScan.Value;
    }

    public void MarkScanned(DateTimeOffset now) => LastScanAt = now;
}
