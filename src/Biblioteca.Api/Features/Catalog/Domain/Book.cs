namespace Biblioteca.Api.Features.Catalog.Domain;

/// <summary>
/// Resultado de <see cref="Book.ChangeTotalCopies"/>: a mudança de quantidade pode ser
/// recusada sem que isso seja uma exceção — é uma rejeição de negócio (422).
/// </summary>
public enum ChangeTotalCopiesResult
{
    Success,
    CopiesBelowActiveLoans,
}

/// <summary>
/// Entidade de catálogo. Não referencia EF Core, HTTP nem cache — ver CLAUDE.md.
///
/// <see cref="Borrow"/> e <see cref="Return"/> espelham, como tipo puro e testável, a
/// mesma invariante do <c>UPDATE</c> condicional descrito em docs/concurrency.md. O
/// caminho de empréstimo em produção (<c>CreateLoanCommand</c>, fase 4) **não** chama
/// estes métodos seguidos de <c>SaveChanges</c> — ele executa o UPDATE atômico
/// diretamente (ADR-0003). Estes métodos existem para que a regra em si seja testável
/// isoladamente e sirvam de referência para o SQL que a implementa de fato.
/// </summary>
public sealed class Book
{
    private Book()
    {
    }

    public Guid Id { get; private set; }
    public Isbn Isbn { get; private set; } = null!;
    public string Title { get; private set; } = string.Empty;
    public string Author { get; private set; } = string.Empty;
    public int TotalCopies { get; private set; }
    public int AvailableCopies { get; private set; }
    public bool IsActive { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public static Book Create(Isbn isbn, string title, string author, int totalCopies, DateTimeOffset now)
    {
        ValidateTitle(title);
        ValidateAuthor(author);

        if (totalCopies < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(totalCopies), totalCopies,
                "A quantidade de exemplares não pode ser negativa.");
        }

        return new Book
        {
            Id = Guid.CreateVersion7(),
            Isbn = isbn,
            Title = title,
            Author = author,
            TotalCopies = totalCopies,
            AvailableCopies = totalCopies,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary>
    /// Decrementa um exemplar disponível. Recusa (retorna <see langword="false"/>, não
    /// lança) quando o livro está inativo ou não há exemplar — o mesmo resultado que o
    /// <c>UPDATE ... WHERE is_active AND available_copies &gt; 0</c> afetando 0 linhas.
    /// </summary>
    public bool Borrow(DateTimeOffset now)
    {
        if (!IsActive || AvailableCopies <= 0)
        {
            return false;
        }

        AvailableCopies--;
        UpdatedAt = now;
        return true;
    }

    /// <summary>
    /// Devolve um exemplar ao estoque (devolução ou cancelamento de empréstimo).
    /// Recusa se o estoque já estiver cheio — não deveria acontecer em uso correto,
    /// mas a invariante <c>AvailableCopies &lt;= TotalCopies</c> nunca é violada aqui.
    /// </summary>
    public bool Return(DateTimeOffset now)
    {
        if (AvailableCopies >= TotalCopies)
        {
            return false;
        }

        AvailableCopies++;
        UpdatedAt = now;
        return true;
    }

    /// <summary>
    /// Aplica a nova quantidade total pelo <b>delta</b> sobre os exemplares disponíveis
    /// (docs/domain-model.md#alteração-de-quantidade) — nunca recalcula
    /// <see cref="AvailableCopies"/> a partir de uma contagem de empréstimos.
    /// </summary>
    public ChangeTotalCopiesResult ChangeTotalCopies(int newTotalCopies, DateTimeOffset now)
    {
        if (newTotalCopies < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(newTotalCopies), newTotalCopies,
                "A quantidade de exemplares não pode ser negativa.");
        }

        var delta = newTotalCopies - TotalCopies;
        var newAvailableCopies = AvailableCopies + delta;

        if (newAvailableCopies < 0)
        {
            return ChangeTotalCopiesResult.CopiesBelowActiveLoans;
        }

        TotalCopies = newTotalCopies;
        AvailableCopies = newAvailableCopies;
        UpdatedAt = now;
        return ChangeTotalCopiesResult.Success;
    }

    public void Rename(string title, string author, DateTimeOffset now)
    {
        ValidateTitle(title);
        ValidateAuthor(author);

        Title = title;
        Author = author;
        UpdatedAt = now;
    }

    public void Deactivate(DateTimeOffset now)
    {
        IsActive = false;
        UpdatedAt = now;
    }

    private static void ValidateTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title) || title.Length > 300)
        {
            throw new ArgumentException("O título deve ter entre 1 e 300 caracteres.", nameof(title));
        }
    }

    private static void ValidateAuthor(string author)
    {
        if (string.IsNullOrWhiteSpace(author) || author.Length > 200)
        {
            throw new ArgumentException("O autor deve ter entre 1 e 200 caracteres.", nameof(author));
        }
    }
}
