namespace FreePlex.Application.Contracts;

public sealed record UpdateProgressRequest
{
    public required long PositionMs { get; init; }
    public long? DurationMs { get; init; }
    public string? SessionId { get; init; }
    public string State { get; init; } = "playing";
}

public sealed record ProgressResponse
{
    public required Guid ItemId { get; init; }
    public long PositionMs { get; init; }
    public bool Watched { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed record HomeSection
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public IReadOnlyList<MediaItemSummary> Items { get; init; } = [];
}

public sealed record HomeResponse
{
    public IReadOnlyList<HomeSection> Sections { get; init; } = [];
}
