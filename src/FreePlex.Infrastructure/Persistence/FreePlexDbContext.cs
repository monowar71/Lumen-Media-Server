using FreePlex.Domain.Jobs;
using FreePlex.Domain.Libraries;
using FreePlex.Domain.Media;
using FreePlex.Domain.Playback;
using FreePlex.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace FreePlex.Infrastructure.Persistence;

/// <summary>
/// Stores <see cref="DateTimeOffset"/> as UTC ticks (INTEGER). SQLite cannot ORDER BY or
/// compare DateTimeOffset stored as TEXT, so every timestamp column uses this converter.
/// UtcTicks sorts by UTC instant with full precision; the offset is normalized to UTC.
/// </summary>
internal sealed class UtcTicksConverter : ValueConverter<DateTimeOffset, long>
{
    public UtcTicksConverter()
        : base(v => v.UtcTicks, v => new DateTimeOffset(v, TimeSpan.Zero))
    {
    }
}

/// <summary>
/// SQLite TEXT comparison is case-sensitive; Guid.ToString() is lowercase while older rows
/// may be uppercase. Always persist uppercase so UPDATE/DELETE by key keep matching.
/// </summary>
internal sealed class UpperGuidConverter : ValueConverter<Guid, string>
{
    public UpperGuidConverter()
        : base(
            v => v.ToString("D").ToUpperInvariant(),
            v => Guid.Parse(v))
    {
    }
}

internal sealed class UpperNullableGuidConverter : ValueConverter<Guid?, string?>
{
    public UpperNullableGuidConverter()
        : base(
            v => v.HasValue ? v.Value.ToString("D").ToUpperInvariant() : null,
            v => string.IsNullOrEmpty(v) ? null : Guid.Parse(v))
    {
    }
}

public sealed class FreePlexDbContext(DbContextOptions<FreePlexDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<Library> Libraries => Set<Library>();
    public DbSet<LibraryPath> LibraryPaths => Set<LibraryPath>();
    public DbSet<MediaItem> MediaItems => Set<MediaItem>();
    public DbSet<Movie> Movies => Set<Movie>();
    public DbSet<Series> Series => Set<Series>();
    public DbSet<Season> Seasons => Set<Season>();
    public DbSet<Episode> Episodes => Set<Episode>();
    public DbSet<MediaSource> MediaSources => Set<MediaSource>();
    public DbSet<MediaStream> MediaStreams => Set<MediaStream>();
    public DbSet<Artwork> Artworks => Set<Artwork>();
    public DbSet<Genre> Genres => Set<Genre>();
    public DbSet<Person> People => Set<Person>();
    public DbSet<MediaPerson> MediaPeople => Set<MediaPerson>();
    public DbSet<PlaybackProgress> Progress => Set<PlaybackProgress>();
    public DbSet<ImportJob> ImportJobs => Set<ImportJob>();
    public DbSet<BackgroundJob> BackgroundJobs => Set<BackgroundJob>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<UtcTicksConverter>();
        configurationBuilder.Properties<Guid>().HaveConversion<UpperGuidConverter>();
        configurationBuilder.Properties<Guid?>().HaveConversion<UpperNullableGuidConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(FreePlexDbContext).Assembly);
    }
}
