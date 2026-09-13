using Biblioteca.Api.Features.Catalog.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Biblioteca.Api.Infrastructure.Persistence.Configurations;

public sealed class BookConfiguration : IEntityTypeConfiguration<Book>
{
    public void Configure(EntityTypeBuilder<Book> builder)
    {
        builder.ToTable("books", t =>
        {
            t.HasCheckConstraint("ck_books_total_copies", "total_copies >= 0");
            t.HasCheckConstraint("ck_books_available_copies",
                "available_copies >= 0 AND available_copies <= total_copies");
        });

        builder.HasKey(b => b.Id);
        builder.Property(b => b.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(b => b.Isbn)
            .HasColumnName("isbn")
            .HasColumnType("varchar(13)")
            .HasConversion(isbn => isbn.Value, value => Isbn.Parse(value))
            .IsRequired();

        builder.Property(b => b.Title).HasColumnName("title").HasMaxLength(300).IsRequired();
        builder.Property(b => b.Author).HasColumnName("author").HasMaxLength(200).IsRequired();
        builder.Property(b => b.TotalCopies).HasColumnName("total_copies").IsRequired();
        builder.Property(b => b.AvailableCopies).HasColumnName("available_copies").IsRequired();
        builder.Property(b => b.IsActive).HasColumnName("is_active").HasDefaultValue(true).IsRequired();
        builder.Property(b => b.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(b => b.UpdatedAt).HasColumnName("updated_at").IsRequired();

        // Token de concorrência para PATCH /books/{id} (edição de catálogo). Não é
        // usado no caminho de empréstimo — ver docs/concurrency.md. "xmin" é uma
        // coluna de sistema do PostgreSQL: o provider Npgsql a reconhece pelo nome e
        // não gera DDL para ela nas migrations.
        builder.Property<uint>("xmin").IsRowVersion();

        builder.HasIndex(b => b.Isbn).IsUnique().HasDatabaseName("ux_books_isbn");

        // Paginação keyset de GET /books (search ordena por título).
        builder.HasIndex(b => new { b.Title, b.Id }).HasDatabaseName("ix_books_title_id");

        builder.HasIndex(b => b.Title)
            .HasDatabaseName("ix_books_title_trgm")
            .HasMethod("gin")
            .HasOperators("gin_trgm_ops");

        builder.HasIndex(b => b.Author)
            .HasDatabaseName("ix_books_author_trgm")
            .HasMethod("gin")
            .HasOperators("gin_trgm_ops");
    }
}
