using FreePlex.Domain.Libraries;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FreePlex.Infrastructure.Persistence.Configurations;

public sealed class LibraryConfig : IEntityTypeConfiguration<Library>
{
    public void Configure(EntityTypeBuilder<Library> b)
    {
        b.ToTable("libraries");
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).IsRequired();
        b.Property(x => x.Type).HasConversion<string>().IsRequired();
        b.Property(x => x.PreferredLanguage).IsRequired();

        b.PrimitiveCollection(x => x.MetadataProviders)
            .HasField("_metadataProviders")
            .UsePropertyAccessMode(PropertyAccessMode.Field);

        b.HasMany(x => x.Paths)
            .WithOne()
            .HasForeignKey(p => p.LibraryId)
            .OnDelete(DeleteBehavior.Cascade);

        b.Navigation(x => x.Paths).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

public sealed class LibraryPathConfig : IEntityTypeConfiguration<LibraryPath>
{
    public void Configure(EntityTypeBuilder<LibraryPath> b)
    {
        b.ToTable("library_paths");
        b.HasKey(x => x.Id);
        b.Property(x => x.Path).IsRequired();
        b.HasIndex(x => x.LibraryId);
        b.HasIndex(x => new { x.LibraryId, x.Path }).IsUnique();
    }
}
