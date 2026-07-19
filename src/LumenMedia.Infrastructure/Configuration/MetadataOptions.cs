namespace LumenMedia.Infrastructure.Configuration;

public sealed class MetadataOptions
{
    public const string SectionName = "LumenMedia:Metadata";

    public string Language { get; set; } = "ru-RU";
    public string FallbackLanguage { get; set; } = "en-US";
    public TmdbOptions Tmdb { get; set; } = new();
    public TvdbOptions Tvdb { get; set; } = new();
}

public sealed class TmdbOptions
{
    public string ApiKey { get; set; } = string.Empty;
}

public sealed class TvdbOptions
{
    public string ApiKey { get; set; } = string.Empty;
    /// <summary>Subscriber PIN from thetvdb.com account (often required with free project keys).</summary>
    public string Pin { get; set; } = string.Empty;
}
