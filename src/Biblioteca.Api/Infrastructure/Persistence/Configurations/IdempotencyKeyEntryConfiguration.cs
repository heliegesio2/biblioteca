using Biblioteca.Api.Infrastructure.Idempotency;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Biblioteca.Api.Infrastructure.Persistence.Configurations;

public sealed class IdempotencyKeyEntryConfiguration : IEntityTypeConfiguration<IdempotencyKeyEntry>
{
    public void Configure(EntityTypeBuilder<IdempotencyKeyEntry> builder)
    {
        builder.ToTable("idempotency_keys", t =>
        {
            t.HasCheckConstraint("ck_idem_status", "status IN ('InProgress','Completed')");
            t.HasCheckConstraint("ck_idem_completed",
                "status <> 'Completed' OR (response_status IS NOT NULL AND response_body IS NOT NULL)");
        });

        builder.HasKey(e => e.Key);
        builder.Property(e => e.Key).HasColumnName("key").HasMaxLength(128).ValueGeneratedNever();

        builder.Property(e => e.Endpoint).HasColumnName("endpoint").HasMaxLength(64).IsRequired();
        builder.Property(e => e.RequestHash).HasColumnName("request_hash").HasColumnType("char(64)").IsRequired();

        builder.Property(e => e.Status)
            .HasColumnName("status")
            .HasConversion<string>()
            .HasMaxLength(16)
            .IsRequired();

        builder.Property(e => e.ResponseStatus).HasColumnName("response_status");
        builder.Property(e => e.ResponseBody).HasColumnName("response_body").HasColumnType("jsonb");
        builder.Property(e => e.ResourceId).HasColumnName("resource_id");
        builder.Property(e => e.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(e => e.ExpiresAt).HasColumnName("expires_at").IsRequired();

        builder.HasIndex(e => e.ExpiresAt).HasDatabaseName("ix_idem_expires_at");
    }
}
