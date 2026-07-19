namespace FreePlex.Infrastructure.Configuration;

public sealed class MetadataOptions
{
    public const string SectionName = "FreePlex:Metadata";

    public string Language { get; set; } = "ru-RU";
    public string FallbackLanguage { get; set; } = "en-US";
    public TmdbOptions Tmdb { get; set; } = new();
}

public sealed class TmdbOptions
{
    public string ApiKey { get; set; } = string.Empty;
}
