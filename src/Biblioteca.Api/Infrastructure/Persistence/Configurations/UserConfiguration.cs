using Biblioteca.Api.Features.Users.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Biblioteca.Api.Infrastructure.Persistence.Configurations;

public sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("users");

        builder.HasKey(u => u.Id);
        builder.Property(u => u.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(u => u.Name).HasColumnName("name").HasMaxLength(200).IsRequired();

        // citext: unicidade case-insensitive garantida pelo banco, sem exigir
        // lower(email) em toda query — ver docs/data-integrity.md.
        builder.Property(u => u.Email).HasColumnName("email").HasColumnType("citext").IsRequired();

        builder.Property(u => u.IsActive).HasColumnName("is_active").HasDefaultValue(true).IsRequired();
        builder.Property(u => u.CreatedAt).HasColumnName("created_at").IsRequired();

        builder.HasIndex(u => u.Email).IsUnique().HasDatabaseName("ux_users_email");
    }
}
