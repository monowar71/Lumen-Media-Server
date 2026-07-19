using FreePlex.Domain.Enums;

namespace FreePlex.Application.Contracts;

public sealed record LibrarySettingsDto
{
    public string PreferredLanguage { get; init; } = "ru-RU";
    public IReadOnlyList<string> MetadataProviders { get; init; } = [];
    public bool AutoScan { get; init; } = true;
}

public sealed record LibraryDto
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public required LibraryType Type { get; init; }
    public IReadOnlyList<string> Paths { get; init; } = [];
    public int ItemCount { get; init; }
    public LibrarySettingsDto Settings { get; init; } = new();
    public DateTimeOffset? LastScanAt { get; init; }
}

public sealed record CreateLibraryRequest
{
    public required string Name { get; init; }
    public required LibraryType Type { get; init; }
    public IReadOnlyList<string> Paths { get; init; } = [];
    public LibrarySettingsDto? Settings { get; init; }
}

public sealed record UpdateLibraryRequest
{
    public string? Name { get; init; }
    public IReadOnlyList<string>? Paths { get; init; }
    public LibrarySettingsDto? Settings { get; init; }
}

public sealed record RefreshLibraryMetadataRequest
{
    /// <summary>Which items to enqueue. Default: Missing.</summary>
    public MetadataRefreshMode Mode { get; init; } = MetadataRefreshMode.Missing;

    /// <summary>
    /// Optional. When set and different from the library language, updates
    /// <c>preferredLanguage</c> before enqueueing (without a separate refresh pass).
    /// </summary>
    public string? PreferredLanguage { get; init; }
}

public sealed record LibraryMetadataRefreshAccepted
{
    public required Guid LibraryId { get; init; }
    public required MetadataRefreshMode Mode { get; init; }
    public required int EnqueuedCount { get; init; }
    public string? PreferredLanguage { get; init; }
}
