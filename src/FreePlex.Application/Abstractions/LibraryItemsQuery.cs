namespace FreePlex.Application.Abstractions;

public enum MediaSortField
{
    Title,
    Year,
    Added,
    Rating,
    Runtime
}

public sealed record LibraryItemsQuery
{
    public required Guid LibraryId { get; init; }
    public required Guid UserId { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = 50;
    public MediaSortField Sort { get; init; } = MediaSortField.Title;
    public bool Desc { get; init; }
    public string? Genre { get; init; }
    public int? Year { get; init; }
    public bool? Watched { get; init; }
    public string? Query { get; init; }
}
