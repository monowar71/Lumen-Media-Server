namespace LumenMedia.Application.Contracts;

public sealed record HealthResponse
{
    public required string Status { get; init; }
    public required string Version { get; init; }
    public long UptimeSec { get; init; }
    public IReadOnlyDictionary<string, string> Checks { get; init; } = new Dictionary<string, string>();
}

public sealed record ServerInfoResponse
{
    public string Name { get; init; } = "LumenMedia";
    public required string Version { get; init; }
    public bool SetupCompleted { get; init; }
    public ServerFeatures Features { get; init; } = new();
}

public sealed record ServerFeatures
{
    public string HardwareAccel { get; init; } = "none";
    public bool Abr { get; init; }
}

public sealed record SearchResponse
{
    public IReadOnlyList<MediaItemSummary> Movies { get; init; } = [];
    public IReadOnlyList<MediaItemSummary> Series { get; init; } = [];
    public IReadOnlyList<EpisodeSummary> Episodes { get; init; } = [];
}
