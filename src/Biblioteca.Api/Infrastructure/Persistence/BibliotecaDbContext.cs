using Biblioteca.Api.Features.Audit.Domain;
using Biblioteca.Api.Features.Catalog.Domain;
using Biblioteca.Api.Features.Loans.Domain;
using Biblioteca.Api.Features.Users.Domain;
using Biblioteca.Api.Infrastructure.Idempotency;
using Microsoft.EntityFrameworkCore;

namespace Biblioteca.Api.Infrastructure.Persistence;

public sealed class BibliotecaDbContext(DbContextOptions<BibliotecaDbContext> options) : DbContext(options)
{
    public DbSet<Book> Books => Set<Book>();
    public DbSet<User> Users => Set<User>();
    public DbSet<Loan> Loans => Set<Loan>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<IdempotencyKeyEntry> IdempotencyKeys => Set<IdempotencyKeyEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // citext (e-mail case-insensitive) e pg_trgm (busca por título/autor com índice)
        // — ver docs/data-integrity.md.
        modelBuilder.HasPostgresExtension("citext");
        modelBuilder.HasPostgresExtension("pg_trgm");

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(BibliotecaDbContext).Assembly);
    }
}
