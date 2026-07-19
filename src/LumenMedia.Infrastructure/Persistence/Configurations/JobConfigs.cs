using LumenMedia.Domain.Jobs;
using LumenMedia.Domain.Playback;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace LumenMedia.Infrastructure.Persistence.Configurations;

public sealed class PlaybackProgressConfig : IEntityTypeConfiguration<PlaybackProgress>
{
    public void Configure(EntityTypeBuilder<PlaybackProgress> b)
    {
        b.ToTable("playback_progress");
        b.HasKey(x => new { x.UserId, x.MediaId });
        b.Property(x => x.MediaKind).HasConversion<string>().IsRequired();
        b.HasIndex(x => new { x.UserId, x.UpdatedAt });
    }
}

public sealed class ImportJobConfig : IEntityTypeConfiguration<ImportJob>
{
    public void Configure(EntityTypeBuilder<ImportJob> b)
    {
        b.ToTable("import_jobs");
        b.HasKey(x => x.Id);
        b.Property(x => x.SourcePath).IsRequired();
        b.Property(x => x.Status).HasConversion<string>().IsRequired();
        b.HasIndex(x => x.SourcePath).IsUnique();
        b.HasIndex(x => x.Status);
    }
}

public sealed class BackgroundJobConfig : IEntityTypeConfiguration<BackgroundJob>
{
    public void Configure(EntityTypeBuilder<BackgroundJob> b)
    {
        b.ToTable("background_jobs");
        b.HasKey(x => x.Id);
        b.Property(x => x.Type).HasConversion<string>().IsRequired();
        b.Property(x => x.State).HasConversion<string>().IsRequired();
        b.HasIndex(x => x.State);
        b.HasIndex(x => new { x.Type, x.CreatedAt });
    }
}
