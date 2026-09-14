using System.Net;
using System.Net.Http.Json;
using Biblioteca.Api.Features.Loans.Domain;
using Biblioteca.Api.Infrastructure.Persistence;
using Biblioteca.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Biblioteca.IntegrationTests;

/// <summary>
/// O teste que dá sentido a toda a arquitetura (docs/testing.md#o-teste-de-concorrência,
/// docs/concurrency.md). Critério de pronto da fase 4: precisa passar antes de qualquer
/// outra coisa ser adicionada (docs/implementation-plan.md#fase-4--empréstimos-o-núcleo).
///
/// 20 usuários distintos para que o índice único parcial (um empréstimo ativo por
/// usuário/livro) não seja o que rejeita — o que está sob teste é o UPDATE condicional.
/// </summary>
public sealed class LastCopyConcurrencyTests(PostgresApiFactory factory) : IntegrationTestBase(factory)
{
    [Fact(Timeout = 30000)]
    public async Task VinteRequisicoesSimultaneas_CriaExatamenteUmEmprestimo()
    {
        var bookId = await CreateBookAsync(totalCopies: 1);
        var userIds = await CreateUsersAsync(20);

        using var barrier = new Barrier(20);

        // Task.Run é essencial aqui: Task.WhenAll(seq.Select(async x => ...)) invoca as
        // lambdas uma de cada vez, na própria thread que enumera. Como
        // Barrier.SignalAndWait() é uma chamada síncrona bloqueante antes do primeiro
        // await, sem Task.Run a primeira chamada trava esperando um 2º participante que
        // nunca chega a começar — a enumeração está presa dentro da primeira invocação.
        // Com Task.Run, cada chamada agenda no thread pool e a enumeração segue em
        // frente, permitindo que as 20 realmente disparem juntas.
        var tasks = userIds.Select(userId => Task.Run(async () =>
        {
            barrier.SignalAndWait();
            return await PostLoanAsync(bookId, userId);
        })).ToArray();

        var responses = await Task.WhenAll(tasks);

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal(19, responses.Count(r => r.StatusCode == HttpStatusCode.Conflict));

        foreach (var conflict in responses.Where(r => r.StatusCode == HttpStatusCode.Conflict))
        {
            var problem = await conflict.Content.ReadFromJsonAsync<ProblemDetails>();
            Assert.Equal("book-unavailable", problem!.Extensions["code"]!.ToString());
        }

        using var scope = Factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<BibliotecaDbContext>();

        var book = await dbContext.Books.AsNoTracking().SingleAsync(b => b.Id == bookId);
        Assert.Equal(0, book.AvailableCopies); // nunca negativo, exatamente zero

        var activeLoans = await dbContext.Loans.AsNoTracking()
            .CountAsync(l => l.BookId == bookId && l.Status == LoanStatus.Active);
        Assert.Equal(1, activeLoans); // nenhum empréstimo duplicado

        var loanCreatedEvents = await dbContext.AuditEvents.AsNoTracking()
            .CountAsync(e => e.EntityType == "Loan" && e.Action == "LoanCreated");
        Assert.Equal(1, loanCreatedEvents); // trilha coerente com o resultado
    }
}
