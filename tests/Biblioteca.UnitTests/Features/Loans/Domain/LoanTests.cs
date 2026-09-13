using Biblioteca.Api.Features.Loans.Domain;

namespace Biblioteca.UnitTests.Features.Loans.Domain;

public sealed class LoanTests
{
    private static readonly DateTimeOffset BorrowedAt = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset DueAt = BorrowedAt.AddDays(14);

    private static Loan CreateLoan() => Loan.Create(Guid.NewGuid(), Guid.NewGuid(), BorrowedAt, DueAt);

    [Fact]
    public void Create_DefineStatusActivoComDatasCorretas()
    {
        var loan = CreateLoan();

        Assert.Equal(LoanStatus.Active, loan.Status);
        Assert.Equal(BorrowedAt, loan.BorrowedAt);
        Assert.Equal(DueAt, loan.DueAt);
        Assert.Null(loan.ReturnedAt);
        Assert.Null(loan.CancelledAt);
    }

    [Fact]
    public void Create_DueAtNaoDepoisDeBorrowedAt_Lanca()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            Loan.Create(Guid.NewGuid(), Guid.NewGuid(), BorrowedAt, BorrowedAt));
    }

    [Fact]
    public void Return_QuandoAtivo_TransicionaParaReturnedERetornaTrue()
    {
        var loan = CreateLoan();
        var returnedAt = BorrowedAt.AddDays(5);

        var result = loan.Return(returnedAt);

        Assert.True(result);
        Assert.Equal(LoanStatus.Returned, loan.Status);
        Assert.Equal(returnedAt, loan.ReturnedAt);
    }

    [Fact]
    public void Return_QuandoJaDevolvido_RecusaSemAlterarEstado()
    {
        var loan = CreateLoan();
        var firstReturn = BorrowedAt.AddDays(5);
        loan.Return(firstReturn);

        var result = loan.Return(BorrowedAt.AddDays(10));

        Assert.False(result);
        Assert.Equal(LoanStatus.Returned, loan.Status);
        Assert.Equal(firstReturn, loan.ReturnedAt);
    }

    [Fact]
    public void Cancel_QuandoAtivo_TransicionaParaCancelledERetornaTrue()
    {
        var loan = CreateLoan();
        var cancelledAt = BorrowedAt.AddHours(1);

        var result = loan.Cancel(cancelledAt);

        Assert.True(result);
        Assert.Equal(LoanStatus.Cancelled, loan.Status);
        Assert.Equal(cancelledAt, loan.CancelledAt);
    }

    [Fact]
    public void Cancel_QuandoJaCancelado_Recusa()
    {
        var loan = CreateLoan();
        loan.Cancel(BorrowedAt.AddHours(1));

        var result = loan.Cancel(BorrowedAt.AddHours(2));

        Assert.False(result);
    }

    [Fact]
    public void Cancel_QuandoJaDevolvido_Recusa()
    {
        var loan = CreateLoan();
        loan.Return(BorrowedAt.AddDays(5));

        var result = loan.Cancel(BorrowedAt.AddDays(6));

        Assert.False(result);
        Assert.Equal(LoanStatus.Returned, loan.Status);
    }
}
