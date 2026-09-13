using Biblioteca.Api.Features.Loans.Domain;

namespace Biblioteca.UnitTests.Features.Loans.Domain;

public sealed class LoanPolicyTests
{
    private static readonly LoanPolicy Policy = new(loanPeriodDays: 14, maxActiveLoansPerUser: 5);

    [Fact]
    public void ComputeDueAt_SomaOPrazoConfigurado()
    {
        var borrowedAt = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

        var dueAt = Policy.ComputeDueAt(borrowedAt);

        Assert.Equal(borrowedAt.AddDays(14), dueAt);
    }

    [Fact]
    public void Constructor_ParametrosInvalidos_Lanca()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LoanPolicy(0, 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LoanPolicy(14, 0));
    }

    [Fact]
    public void EnsureCanBorrow_TudoOk_PermiteEmprestimo()
    {
        var result = Policy.EnsureCanBorrow(
            userIsActive: true,
            bookIsActive: true,
            bookHasAvailableCopy: true,
            userActiveLoanCount: 0,
            userHasActiveLoanForBook: false);

        Assert.Equal(LoanEligibility.Allowed, result);
    }

    [Fact]
    public void EnsureCanBorrow_UsuarioInativo_RecusaComUserInactive()
    {
        var result = Policy.EnsureCanBorrow(
            userIsActive: false,
            bookIsActive: true,
            bookHasAvailableCopy: true,
            userActiveLoanCount: 0,
            userHasActiveLoanForBook: false);

        Assert.Equal(LoanEligibility.UserInactive, result);
    }

    [Fact]
    public void EnsureCanBorrow_LivroInativo_RecusaComBookInactive()
    {
        var result = Policy.EnsureCanBorrow(
            userIsActive: true,
            bookIsActive: false,
            bookHasAvailableCopy: true,
            userActiveLoanCount: 0,
            userHasActiveLoanForBook: false);

        Assert.Equal(LoanEligibility.BookInactive, result);
    }

    [Fact]
    public void EnsureCanBorrow_LimiteAtingido_RecusaComUserLoanLimitReached()
    {
        var result = Policy.EnsureCanBorrow(
            userIsActive: true,
            bookIsActive: true,
            bookHasAvailableCopy: true,
            userActiveLoanCount: 5,
            userHasActiveLoanForBook: false);

        Assert.Equal(LoanEligibility.UserLoanLimitReached, result);
    }

    [Fact]
    public void EnsureCanBorrow_EmprestimoAtivoDuplicado_RecusaComDuplicateActiveLoan()
    {
        var result = Policy.EnsureCanBorrow(
            userIsActive: true,
            bookIsActive: true,
            bookHasAvailableCopy: true,
            userActiveLoanCount: 1,
            userHasActiveLoanForBook: true);

        Assert.Equal(LoanEligibility.DuplicateActiveLoan, result);
    }

    [Fact]
    public void EnsureCanBorrow_SemExemplarDisponivel_RecusaComBookUnavailable()
    {
        var result = Policy.EnsureCanBorrow(
            userIsActive: true,
            bookIsActive: true,
            bookHasAvailableCopy: false,
            userActiveLoanCount: 0,
            userHasActiveLoanForBook: false);

        Assert.Equal(LoanEligibility.BookUnavailable, result);
    }
}
