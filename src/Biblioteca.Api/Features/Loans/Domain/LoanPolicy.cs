namespace Biblioteca.Api.Features.Loans.Domain;

/// <summary>
/// Motivo pelo qual um empréstimo não pode ser criado — ou <see cref="Allowed"/>.
/// A distinção 409 (conflito, retry pode funcionar) vs. 422 (semanticamente inválido)
/// é feita pelo handler ao traduzir cada valor para Problem Details
/// (docs/api-contract.md#códigos-de-erro-de-negócio).
/// </summary>
public enum LoanEligibility
{
    Allowed,
    UserInactive,
    BookInactive,
    UserLoanLimitReached,
    DuplicateActiveLoan,
    BookUnavailable,
}

/// <summary>
/// Regras de empréstimo centralizadas em um tipo puro, sem I/O (docs/domain-model.md).
/// Os parâmetros vêm de configuração (<c>Loans:*</c>), nunca hard-coded.
///
/// <see cref="EnsureCanBorrow"/> dá a mensagem certa de negócio, mas <b>não</b> é a
/// garantia de corretude sob concorrência — essa vem do UPDATE condicional e das
/// constraints do banco (docs/concurrency.md). Se uma validação daqui passar por uma
/// condição de corrida, o banco recusa de qualquer forma.
/// </summary>
public sealed class LoanPolicy
{
    public int LoanPeriodDays { get; }
    public int MaxActiveLoansPerUser { get; }

    public LoanPolicy(int loanPeriodDays, int maxActiveLoansPerUser)
    {
        if (loanPeriodDays <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(loanPeriodDays), loanPeriodDays,
                "O prazo de devolução deve ser positivo.");
        }

        if (maxActiveLoansPerUser <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxActiveLoansPerUser), maxActiveLoansPerUser,
                "O limite de empréstimos ativos por usuário deve ser positivo.");
        }

        LoanPeriodDays = loanPeriodDays;
        MaxActiveLoansPerUser = maxActiveLoansPerUser;
    }

    public DateTimeOffset ComputeDueAt(DateTimeOffset borrowedAt) => borrowedAt.AddDays(LoanPeriodDays);

    /// <summary>
    /// Avalia, na ordem em que os erros de negócio são listados no contrato: usuário
    /// ativo, livro ativo, limite de empréstimos, duplicidade e, por fim,
    /// disponibilidade de exemplar (a única checagem cuja resposta definitiva vem do
    /// UPDATE condicional, não desta avaliação).
    /// </summary>
    public LoanEligibility EnsureCanBorrow(
        bool userIsActive,
        bool bookIsActive,
        bool bookHasAvailableCopy,
        int userActiveLoanCount,
        bool userHasActiveLoanForBook)
    {
        if (!userIsActive)
        {
            return LoanEligibility.UserInactive;
        }

        if (!bookIsActive)
        {
            return LoanEligibility.BookInactive;
        }

        if (userActiveLoanCount >= MaxActiveLoansPerUser)
        {
            return LoanEligibility.UserLoanLimitReached;
        }

        if (userHasActiveLoanForBook)
        {
            return LoanEligibility.DuplicateActiveLoan;
        }

        if (!bookHasAvailableCopy)
        {
            return LoanEligibility.BookUnavailable;
        }

        return LoanEligibility.Allowed;
    }
}
