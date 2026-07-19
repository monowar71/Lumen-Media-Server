namespace FreePlex.Infrastructure.Configuration;

public sealed class DatabaseOptions
{
    public const string SectionName = "FreePlex:Database";
    public string ConnectionString { get; set; } = "Data Source=/config/freeplex.db";
}

public sealed class PathsOptions
{
    public const string SectionName = "FreePlex:Paths";
    public string Config { get; set; } = "/config";
    public string Downloads { get; set; } = "/downloads";
    public string Transcodes { get; set; } = "/config/transcodes";
}

/// <summary>JWT signing/validation options. Secret must come from env/user-secrets, never appsettings.</summary>
public sealed class JwtOptions
{
    public const string SectionName = "Jwt";
    public string Secret { get; set; } = string.Empty;
    public string Issuer { get; set; } = "freeplex";
    public string Audience { get; set; } = "freeplex-clients";
    public int AccessTokenMinutes { get; set; } = 15;
    public int RefreshTokenDays { get; set; } = 30;
}

public sealed class ImportOptions
{
    public const string SectionName = "FreePlex:Import";
    public bool Watch { get; set; } = true;
    public int MinFileSizeMb { get; set; } = 50;
    public string Strategy { get; set; } = "Hardlink";
}

public sealed class JobWorkerOptions
{
    public const string SectionName = "FreePlex:Jobs";
    public int WorkerCount { get; set; } = 2;
}
