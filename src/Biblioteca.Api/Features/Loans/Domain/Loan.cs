namespace Biblioteca.Api.Features.Loans.Domain;

public enum LoanStatus
{
    Active,
    Returned,
    Cancelled,
}

/// <summary>
/// Empréstimo de um exemplar. Estados finais (<see cref="LoanStatus.Returned"/>,
/// <see cref="LoanStatus.Cancelled"/>) são finais — nenhuma transição os reabre, e
/// nenhuma apaga o registro (docs/domain-model.md#máquina-de-estados).
///
/// Assim como <c>Book.Borrow</c>, <see cref="Return"/> e <see cref="Cancel"/> recusam
/// (retornam <see langword="false"/>) em vez de lançar quando o estado não permite a
/// transição — o mesmo resultado de um <c>UPDATE ... WHERE status = 'Active'</c>
/// afetando 0 linhas. O caminho real de devolução/cancelamento (fase 4) usa esse
/// UPDATE condicional; este método é a mesma regra, testável como tipo puro.
/// </summary>
public sealed class Loan
{
    private Loan()
    {
    }

    public Guid Id { get; private set; }
    public Guid BookId { get; private set; }
    public Guid UserId { get; private set; }
    public LoanStatus Status { get; private set; }
    public DateTimeOffset BorrowedAt { get; private set; }
    public DateTimeOffset DueAt { get; private set; }
    public DateTimeOffset? ReturnedAt { get; private set; }
    public DateTimeOffset? CancelledAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public static Loan Create(Guid bookId, Guid userId, DateTimeOffset borrowedAt, DateTimeOffset dueAt)
    {
        if (dueAt <= borrowedAt)
        {
            throw new ArgumentOutOfRangeException(nameof(dueAt), dueAt,
                "O prazo de devolução deve ser depois da data do empréstimo.");
        }

        return new Loan
        {
            Id = Guid.CreateVersion7(),
            BookId = bookId,
            UserId = userId,
            Status = LoanStatus.Active,
            BorrowedAt = borrowedAt,
            DueAt = dueAt,
            CreatedAt = borrowedAt,
            UpdatedAt = borrowedAt,
        };
    }

    public bool Return(DateTimeOffset now)
    {
        if (Status != LoanStatus.Active)
        {
            return false;
        }

        Status = LoanStatus.Returned;
        ReturnedAt = now;
        UpdatedAt = now;
        return true;
    }

    public bool Cancel(DateTimeOffset now)
    {
        if (Status != LoanStatus.Active)
        {
            return false;
        }

        Status = LoanStatus.Cancelled;
        CancelledAt = now;
        UpdatedAt = now;
        return true;
    }
}
