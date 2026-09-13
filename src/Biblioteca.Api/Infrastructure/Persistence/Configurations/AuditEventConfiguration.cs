using Biblioteca.Api.Features.Audit.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Biblioteca.Api.Infrastructure.Persistence.Configurations;

public sealed class AuditEventConfiguration : IEntityTypeConfiguration<AuditEvent>
{
    public void Configure(EntityTypeBuilder<AuditEvent> builder)
    {
        builder.ToTable("audit_events");

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnName("id").UseIdentityAlwaysColumn();

        builder.Property(e => e.EntityType).HasColumnName("entity_type").HasMaxLength(32).IsRequired();
        builder.Property(e => e.EntityId).HasColumnName("entity_id").IsRequired();
        builder.Property(e => e.Action).HasColumnName("action").HasMaxLength(64).IsRequired();
        builder.Property(e => e.Actor).HasColumnName("actor").HasMaxLength(200).IsRequired();
        builder.Property(e => e.OccurredAt).HasColumnName("occurred_at").IsRequired();
        builder.Property(e => e.CorrelationId).HasColumnName("correlation_id").HasMaxLength(64).IsRequired();
        builder.Property(e => e.Payload).HasColumnName("payload").HasColumnType("jsonb").IsRequired();

        // Paginação keyset por id decrescente, sob cada filtro de GET /audit-events.
        builder.HasIndex(e => new { e.EntityType, e.EntityId, e.Id })
            .HasDatabaseName("ix_audit_entity")
            .IsDescending(false, false, true);

        builder.HasIndex(e => e.OccurredAt)
            .HasDatabaseName("ix_audit_occurred_at")
            .IsDescending(true);

        builder.HasIndex(e => new { e.Action, e.Id })
            .HasDatabaseName("ix_audit_action")
            .IsDescending(false, true);
    }
}
