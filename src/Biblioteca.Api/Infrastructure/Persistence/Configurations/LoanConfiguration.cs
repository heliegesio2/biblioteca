using Biblioteca.Api.Features.Catalog.Domain;
using Biblioteca.Api.Features.Loans.Domain;
using Biblioteca.Api.Features.Users.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Biblioteca.Api.Infrastructure.Persistence.Configurations;

public sealed class LoanConfiguration : IEntityTypeConfiguration<Loan>
{
    public void Configure(EntityTypeBuilder<Loan> builder)
    {
        builder.ToTable("loans", t =>
        {
            t.HasCheckConstraint("ck_loans_status", "status IN ('Active','Returned','Cancelled')");
            t.HasCheckConstraint("ck_loans_due_after_borrow", "due_at > borrowed_at");
            t.HasCheckConstraint("ck_loans_terminal_dates", """
                (status = 'Active'    AND returned_at IS NULL AND cancelled_at IS NULL) OR
                (status = 'Returned'  AND returned_at IS NOT NULL) OR
                (status = 'Cancelled' AND cancelled_at IS NOT NULL)
                """);
        });

        builder.HasKey(l => l.Id);
        builder.Property(l => l.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(l => l.BookId).HasColumnName("book_id").IsRequired();
        builder.Property(l => l.UserId).HasColumnName("user_id").IsRequired();

        builder.Property(l => l.Status)
            .HasColumnName("status")
            .HasConversion<string>()
            .HasMaxLength(16)
            .IsRequired();

        builder.Property(l => l.BorrowedAt).HasColumnName("borrowed_at").IsRequired();
        builder.Property(l => l.DueAt).HasColumnName("due_at").IsRequired();
        builder.Property(l => l.ReturnedAt).HasColumnName("returned_at");
        builder.Property(l => l.CancelledAt).HasColumnName("cancelled_at");
        builder.Property(l => l.CreatedAt).HasColumnName("created_at").IsRequired();
        builder.Property(l => l.UpdatedAt).HasColumnName("updated_at").IsRequired();

        // ON DELETE RESTRICT: o banco recusa apagar livro/usuário com histórico, mesmo
        // por um DELETE manual em produção — ver docs/data-integrity.md.
        builder.HasOne<Book>().WithMany().HasForeignKey(l => l.BookId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>().WithMany().HasForeignKey(l => l.UserId).OnDelete(DeleteBehavior.Restrict);

        // Impede, sob concorrência e sem lock, dois empréstimos ativos do mesmo par
        // livro/usuário. Sai do índice quando o status muda (devolução/cancelamento).
        builder.HasIndex(l => new { l.BookId, l.UserId })
            .HasDatabaseName("ux_loans_active_book_user")
            .IsUnique()
            .HasFilter("status = 'Active'");

        builder.HasIndex(l => new { l.BookId, l.Id })
            .HasDatabaseName("ix_loans_book_id_id")
            .IsDescending(false, true);

        builder.HasIndex(l => new { l.UserId, l.Id })
            .HasDatabaseName("ix_loans_user_id_id")
            .IsDescending(false, true);

        builder.HasIndex(l => l.UserId)
            .HasDatabaseName("ix_loans_active_user")
            .HasFilter("status = 'Active'");
    }
}
