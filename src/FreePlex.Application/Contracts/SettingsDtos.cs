namespace FreePlex.Application.Contracts;

public sealed record LadderRungDto
{
    public required string Id { get; init; }
    public int Height { get; init; }
    public int VideoBitrateKbps { get; init; }
}

public sealed record TranscodingSettingsDto
{
    public string HardwareAccel { get; init; } = "auto";
    public int MaxConcurrentSessions { get; init; } = 3;
    public bool AbrEnabled { get; init; } = true;
    public int SegmentDurationSec { get; init; } = 4;
    public IReadOnlyList<LadderRungDto> Ladder { get; init; } = [];
    public int DefaultRemoteCapKbps { get; init; } = 8000;
}

public sealed record MetadataSettingsDto
{
    /// <summary>Providers that are currently usable (configured + free always-on like TvMaze).</summary>
    public IReadOnlyList<string> Providers { get; init; } = [];

    public string Language { get; init; } = "ru-RU";
    public string FallbackLanguage { get; init; } = "en-US";

    /// <summary>True when a TMDB API key is present (env or settings). Key itself is never returned.</summary>
    public bool TmdbConfigured { get; init; }

    /// <summary>True when a TVDB API key is present.</summary>
    public bool TvdbConfigured { get; init; }

    /// <summary>TVMaze needs no key and is always available for series.</summary>
    public bool TvMazeConfigured { get; init; } = true;

    /// <summary>Write-only: set a new TMDB API key. Omitted/null = unchanged. Empty string = clear.</summary>
    public string? TmdbApiKey { get; init; }

    /// <summary>Write-only: set a new TVDB API key. Omitted/null = unchanged. Empty string = clear.</summary>
    public string? TvdbApiKey { get; init; }

    /// <summary>Write-only: TVDB subscriber PIN (required for some free/project keys).</summary>
    public string? TvdbPin { get; init; }
}

public sealed record ImportSettingsDto
{
    public bool Watch { get; init; } = true;
    public int MinFileSizeMb { get; init; } = 50;
    public string Strategy { get; init; } = "Hardlink";
}

public sealed record ServerSettingsDto
{
    public TranscodingSettingsDto Transcoding { get; init; } = new();
    public MetadataSettingsDto Metadata { get; init; } = new();
    public ImportSettingsDto Import { get; init; } = new();
}
