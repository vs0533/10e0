using Microsoft.EntityFrameworkCore;

namespace TenE0.Core.Files.Storage;

public static class FileModelBuilderExtensions
{
    public static ModelBuilder ConfigureTenE0FileAttachmentTables(this ModelBuilder mb)
    {
        mb.Entity<TenE0FileAttachment>(b =>
        {
            b.ToTable("FileAttachments");

            b.HasKey(x => x.Id);

            b.Property(x => x.FileName).HasMaxLength(255).IsRequired();
            b.Property(x => x.StoragePath).HasMaxLength(500).IsRequired();
            b.Property(x => x.ContentType).HasMaxLength(200).IsRequired();
            b.Property(x => x.StorageBackend).HasMaxLength(50).IsRequired();
            b.Property(x => x.Category).HasMaxLength(50);
            b.Property(x => x.FileHash).HasMaxLength(100);
            b.Property(x => x.ThumbnailPath).HasMaxLength(200);
            b.Property(x => x.RelatedEntityId).HasMaxLength(50);
            b.Property(x => x.RelatedEntityType).HasMaxLength(100);

            b.HasIndex(x => x.Category);
            b.HasIndex(x => x.RelatedEntityId);
            b.HasIndex(x => x.RelatedEntityType);
            b.HasIndex(x => x.IsDeleted);
        });

        return mb;
    }
}
