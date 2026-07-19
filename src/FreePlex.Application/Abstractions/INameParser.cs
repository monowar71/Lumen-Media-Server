using FreePlex.Domain.Enums;

namespace FreePlex.Application.Abstractions;

/// <summary>Result of parsing a release file/dir name.</summary>
public sealed record ParsedName
{
    public required MediaKind Kind { get; init; }
    public required string Title { get; init; }
    public int? Year { get; init; }
    public int? Season { get; init; }
    public int? Episode { get; init; }
    public string? Quality { get; init; }
    public string? Codec { get; init; }
    public string? ReleaseGroup { get; init; }
}

public interface INameParser
{
    ParsedName Parse(string fileName);
}
