using FreePlex.Domain.Media;
using FreePlex.Domain.Libraries;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FreePlex.Infrastructure.Persistence.Configurations;

public sealed class MediaItemConfig : IEntityTypeConfiguration<MediaItem>
{
    public void Configure(EntityTypeBuilder<MediaItem> b)
    {
        b.ToTable("media_items");
        b.HasKey(x => x.Id);

        // Kind is a computed property; discrimination uses a shadow "kind" column (TPH).
        b.Ignore(x => x.Kind);
        b.HasDiscriminator<string>("kind")
            .HasValue<Movie>("Movie")
            .HasValue<Series>("Series");

        b.Property(x => x.Title).IsRequired();
        b.Property(x => x.SortTitle).IsRequired();

        b.HasIndex(x => new { x.LibraryId, x.SortTitle });
        b.HasIndex(x => new { x.LibraryId, x.AddedAt });
        b.HasIndex(x => x.TmdbId);
        b.HasIndex(x => x.Title);

        b.HasOne<Library>()
            .WithMany()
            .HasForeignKey(x => x.LibraryId)
            .OnDelete(DeleteBehavior.Cascade);

        b.HasMany(x => x.Genres)
            .WithMany()
            .UsingEntity(j => j.ToTable("media_genres"));
        b.Navigation(x => x.Genres).UsePropertyAccessMode(PropertyAccessMode.Field);

        b.HasMany(x => x.People)
            .WithOne()
            .HasForeignKey(mp => mp.MediaItemId)
            .OnDelete(DeleteBehavior.Cascade);
        b.Navigation(x => x.People).UsePropertyAccessMode(PropertyAccessMode.Field);

        b.HasMany(x => x.Artworks)
            .WithOne()
            .HasForeignKey(a => a.MediaItemId)
            .OnDelete(DeleteBehavior.Cascade);
        b.Navigation(x => x.Artworks).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

public sealed class MovieConfig : IEntityTypeConfiguration<Movie>
{
    public void Configure(EntityTypeBuilder<Movie> b)
    {
        b.HasMany(x => x.Sources)
            .WithOne()
            .HasForeignKey(s => s.MediaItemId)
            .OnDelete(DeleteBehavior.Cascade);
        b.Navigation(x => x.Sources).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

public sealed class SeriesConfig : IEntityTypeConfiguration<Series>
{
    public void Configure(EntityTypeBuilder<Series> b)
    {
        b.Property(x => x.Status).HasConversion<string>();

        b.HasMany(x => x.Seasons)
            .WithOne()
            .HasForeignKey(s => s.SeriesId)
            .OnDelete(DeleteBehavior.Cascade);
        b.Navigation(x => x.Seasons).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

public sealed class SeasonConfig : IEntityTypeConfiguration<Season>
{
    public void Configure(EntityTypeBuilder<Season> b)
    {
        b.ToTable("seasons");
        b.HasKey(x => x.Id);
        b.HasIndex(x => new { x.SeriesId, x.SeasonNumber }).IsUnique();

        b.HasMany(x => x.Episodes)
            .WithOne()
            .HasForeignKey(e => e.SeasonId)
            .OnDelete(DeleteBehavior.Cascade);
        b.Navigation(x => x.Episodes).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

public sealed class EpisodeConfig : IEntityTypeConfiguration<Episode>
{
    public void Configure(EntityTypeBuilder<Episode> b)
    {
        b.ToTable("episodes");
        b.HasKey(x => x.Id);
        // series_id is indexed but not a second cascade relationship (avoids multiple cascade
        // paths to episodes); deletion cascades series → seasons → episodes.
        b.HasIndex(x => x.SeriesId);
        b.HasIndex(x => new { x.SeriesId, x.SeasonNumber, x.EpisodeNumber }).IsUnique();
        b.HasIndex(x => x.SeasonId);

        b.HasMany(x => x.Sources)
            .WithOne()
            .HasForeignKey(s => s.EpisodeId)
            .OnDelete(DeleteBehavior.Cascade);
        b.Navigation(x => x.Sources).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

public sealed class MediaSourceConfig : IEntityTypeConfiguration<MediaSource>
{
    public void Configure(EntityTypeBuilder<MediaSource> b)
    {
        b.ToTable("media_sources", t => t.HasCheckConstraint(
            "ck_media_sources_owner",
            "(media_item_id IS NULL) <> (episode_id IS NULL)"));
        b.HasKey(x => x.Id);
        b.Property(x => x.Path).IsRequired();
        b.Property(x => x.Container).IsRequired();
        b.HasIndex(x => x.Path).IsUnique();
        b.HasIndex(x => x.MediaItemId);
        b.HasIndex(x => x.EpisodeId);
        b.HasIndex(x => x.ContentHash);

        b.Property(x => x.MediaItemId).HasColumnName("media_item_id");
        b.Property(x => x.EpisodeId).HasColumnName("episode_id");

        b.HasMany(x => x.Streams)
            .WithOne()
            .HasForeignKey(s => s.MediaSourceId)
            .OnDelete(DeleteBehavior.Cascade);
        b.Navigation(x => x.Streams).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

public sealed class MediaStreamConfig : IEntityTypeConfiguration<MediaStream>
{
    public void Configure(EntityTypeBuilder<MediaStream> b)
    {
        b.ToTable("media_streams");
        b.HasKey(x => x.Id);
        b.Property(x => x.Kind).HasConversion<string>().IsRequired();
        b.HasIndex(x => x.MediaSourceId);
    }
}

public sealed class ArtworkConfig : IEntityTypeConfiguration<Artwork>
{
    public void Configure(EntityTypeBuilder<Artwork> b)
    {
        b.ToTable("artworks");
        b.HasKey(x => x.Id);
        b.Property(x => x.Kind).HasConversion<string>().IsRequired();
        b.Property(x => x.LocalPath).IsRequired();
        b.Property(x => x.MediaItemId).HasColumnName("media_item_id");
        b.Property(x => x.EpisodeId).HasColumnName("episode_id");
        b.HasIndex(x => new { x.MediaItemId, x.Kind });
        b.HasIndex(x => new { x.EpisodeId, x.Kind });

        b.HasOne<Episode>()
            .WithMany()
            .HasForeignKey(a => a.EpisodeId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class GenreConfig : IEntityTypeConfiguration<Genre>
{
    public void Configure(EntityTypeBuilder<Genre> b)
    {
        b.ToTable("genres");
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).IsRequired();
        b.HasIndex(x => x.Name).IsUnique();
    }
}

public sealed class PersonConfig : IEntityTypeConfiguration<Person>
{
    public void Configure(EntityTypeBuilder<Person> b)
    {
        b.ToTable("people");
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).IsRequired();
        b.HasIndex(x => x.Name);
    }
}

public sealed class MediaPersonConfig : IEntityTypeConfiguration<MediaPerson>
{
    public void Configure(EntityTypeBuilder<MediaPerson> b)
    {
        b.ToTable("media_people");
        b.HasKey(x => new { x.MediaItemId, x.PersonId, x.Type });
        b.Property(x => x.Type).HasConversion<string>().IsRequired();

        b.HasOne(x => x.Person)
            .WithMany()
            .HasForeignKey(x => x.PersonId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
