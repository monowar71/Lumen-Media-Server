namespace LumenMedia.Infrastructure.Configuration;

public sealed class DatabaseOptions
{
    public const string SectionName = "LumenMedia:Database";
    public string ConnectionString { get; set; } = "Data Source=/config/lumenmedia.db";
}

public sealed class PathsOptions
{
    public const string SectionName = "LumenMedia:Paths";
    public string Config { get; set; } = "/config";
    public string Downloads { get; set; } = "/downloads";
    public string Transcodes { get; set; } = "/config/transcodes";
    /// <summary>Disk cache for converted WebVTT sidecars (<c>{Config}/subtitles</c> by default).</summary>
    public string Subtitles { get; set; } = "/config/subtitles";
}

/// <summary>JWT signing/validation options. Secret must come from env/user-secrets, never appsettings.</summary>
public sealed class JwtOptions
{
    public const string SectionName = "Jwt";
    public string Secret { get; set; } = string.Empty;
    public string Issuer { get; set; } = "lumenmedia";
    public string Audience { get; set; } = "lumenmedia-clients";
    public int AccessTokenMinutes { get; set; } = 15;
    public int RefreshTokenDays { get; set; } = 30;
}

public sealed class ImportOptions
{
    public const string SectionName = "LumenMedia:Import";
    public bool Watch { get; set; } = true;
    public int MinFileSizeMb { get; set; } = 50;
    public string Strategy { get; set; } = "Hardlink";
}

public sealed class JobWorkerOptions
{
    public const string SectionName = "LumenMedia:Jobs";
    public int WorkerCount { get; set; } = 2;
}
