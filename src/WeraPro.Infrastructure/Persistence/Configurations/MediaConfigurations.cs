namespace WeraPro.Infrastructure.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WeraPro.Infrastructure.Persistence.Entities;

/// <summary>تهيئة كيانات الوسائط (G5) — أسماء snake_case مطابقة لـV002/V016 حرفياً.</summary>

public sealed class MediaAssetRowConfiguration : IEntityTypeConfiguration<MediaAssetRow>
{
    public void Configure(EntityTypeBuilder<MediaAssetRow> builder)
    {
        builder.ToTable("media_assets", "catalog");
        builder.HasKey(x => x.MediaId);
        builder.Property(x => x.MediaId).HasColumnName("media_id").ValueGeneratedOnAdd();
        builder.Property(x => x.StorageKey).HasColumnName("storage_key").HasMaxLength(256);
        builder.Property(x => x.Sha256).HasColumnName("sha256").HasMaxLength(64);
        builder.Property(x => x.MimeType).HasColumnName("mime_type").HasMaxLength(32);
        builder.Property(x => x.WidthPx).HasColumnName("width_px");
        builder.Property(x => x.HeightPx).HasColumnName("height_px");
        builder.Property(x => x.SizeBytes).HasColumnName("size_bytes");
        builder.Property(x => x.ThumbKey).HasColumnName("thumb_key").HasMaxLength(256);
        builder.Property(x => x.UploadedBy).HasColumnName("uploaded_by");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at");
        builder.Property(x => x.ThumbCatalogKey).HasColumnName("thumb_catalog_key").HasMaxLength(256);
        builder.Property(x => x.ThumbPosKey).HasColumnName("thumb_pos_key").HasMaxLength(256);
        builder.Property(x => x.PreviewKey).HasColumnName("preview_key").HasMaxLength(256);
        builder.Property(x => x.Ahash).HasColumnName("ahash");
        builder.Property(x => x.ProcessedAt).HasColumnName("processed_at");

        builder.HasIndex(x => x.Sha256).HasDatabaseName("idx_media_sha256");
        builder.HasIndex(x => x.StorageKey).IsUnique().HasDatabaseName("media_assets_storage_key_key");
    }
}

public sealed class ProductImageRowConfiguration : IEntityTypeConfiguration<ProductImageRow>
{
    public void Configure(EntityTypeBuilder<ProductImageRow> builder)
    {
        builder.ToTable("product_images", "catalog");
        builder.HasKey(x => x.LinkId);
        builder.Property(x => x.LinkId).HasColumnName("link_id").ValueGeneratedOnAdd();
        builder.Property(x => x.ProductId).HasColumnName("product_id");
        builder.Property(x => x.SkuId).HasColumnName("sku_id");
        builder.Property(x => x.MediaId).HasColumnName("media_id");
        builder.Property(x => x.SortOrder).HasColumnName("sort_order");
        builder.Property(x => x.IsPrimary).HasColumnName("is_primary");

        builder.HasOne<MediaAssetRow>().WithMany().HasForeignKey(x => x.MediaId);
        // الفريدان الجزئيان من V016 (لا ربط مزدوج لنفس الصورة بنفس الهدف):
        builder.HasIndex(x => new { x.MediaId, x.ProductId })
            .HasDatabaseName("uq_product_images_media_product")
            .HasFilter("product_id IS NOT NULL");
        builder.HasIndex(x => new { x.MediaId, x.SkuId })
            .HasDatabaseName("uq_product_images_media_sku")
            .HasFilter("sku_id IS NOT NULL");
    }
}

public sealed class MediaImportJobRowConfiguration : IEntityTypeConfiguration<MediaImportJobRow>
{
    public void Configure(EntityTypeBuilder<MediaImportJobRow> builder)
    {
        builder.ToTable("media_import_jobs", "catalog");
        builder.HasKey(x => x.JobId);
        builder.Property(x => x.JobId).HasColumnName("job_id").ValueGeneratedOnAdd();
        builder.Property(x => x.Label).HasColumnName("label").HasMaxLength(120);
        builder.Property(x => x.State).HasColumnName("state").HasMaxLength(16);
        builder.Property(x => x.CreatedBy).HasColumnName("created_by");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at");
        builder.Property(x => x.StartedAt).HasColumnName("started_at");
        builder.Property(x => x.FinishedAt).HasColumnName("finished_at");

        builder.HasIndex(x => x.JobId).HasDatabaseName("idx_media_import_jobs_running")
            .HasFilter("state = 'RUNNING'");
    }
}

public sealed class MediaImportFileRowConfiguration : IEntityTypeConfiguration<MediaImportFileRow>
{
    public void Configure(EntityTypeBuilder<MediaImportFileRow> builder)
    {
        builder.ToTable("media_import_files", "catalog");
        builder.HasKey(x => x.FileId);
        builder.Property(x => x.FileId).HasColumnName("file_id").ValueGeneratedOnAdd();
        builder.Property(x => x.JobId).HasColumnName("job_id");
        builder.Property(x => x.Ordinal).HasColumnName("ordinal");
        builder.Property(x => x.ClientName).HasColumnName("client_name").HasMaxLength(200);
        builder.Property(x => x.StagingKey).HasColumnName("staging_key").HasMaxLength(256);
        builder.Property(x => x.SizeBytes).HasColumnName("size_bytes");
        builder.Property(x => x.State).HasColumnName("state").HasMaxLength(16);
        builder.Property(x => x.Sha256).HasColumnName("sha256").HasMaxLength(64);
        builder.Property(x => x.ErrorCode).HasColumnName("error_code").HasMaxLength(64);
        builder.Property(x => x.SkipReason).HasColumnName("skip_reason").HasMaxLength(128);
        builder.Property(x => x.MediaId).HasColumnName("media_id");
        builder.Property(x => x.ProcessedAt).HasColumnName("processed_at");

        builder.HasIndex(x => new { x.JobId, x.State }).HasDatabaseName("idx_media_import_files_job_state");
        builder.HasIndex(x => new { x.JobId, x.Ordinal }).IsUnique().HasDatabaseName("mif_job_ordinal_uq");
        builder.HasOne<MediaAssetRow>().WithMany().HasForeignKey(x => x.MediaId);
        builder.HasOne<MediaImportJobRow>().WithMany().HasForeignKey(x => x.JobId);
    }
}
