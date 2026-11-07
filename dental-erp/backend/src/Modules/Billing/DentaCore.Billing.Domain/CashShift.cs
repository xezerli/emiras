using DentaCore.BuildingBlocks.Domain;

namespace DentaCore.Billing.Domain;

/// <summary>Kassa smeni. Bir kassirin eyni anda ən çox bir açıq smeni ola bilər (DB: ux_shift_open).</summary>
public sealed class CashShift : Entity<Guid>
{
    private CashShift()
        : base(Guid.Empty)
    {
    }

    public Guid BranchId { get; private set; }

    public Guid CashierId { get; private set; }

    public DateTimeOffset OpenedAt { get; private set; }

    public DateTimeOffset? ClosedAt { get; private set; }

    public decimal OpeningCash { get; private set; }

    public decimal? ClosingCash { get; private set; }

    public decimal? ExpectedCash { get; private set; }

    public bool IsOpen => ClosedAt is null;

    /// <summary>Faktiki − gözlənilən. Mənfi = kassada çatışmazlıq.</summary>
    public decimal? Difference => ClosingCash - ExpectedCash;

    public static Result<CashShift> Open(Guid id, Guid branchId, Guid cashierId, decimal openingCash, DateTimeOffset now)
    {
        if (openingCash < 0 || !Money.IsValidAmount(openingCash))
        {
            return Error.Validation("shift.invalid_amount", "Opening cash must be non-negative with at most 2 decimal places.");
        }

        return new CashShift { Id = id, BranchId = branchId, CashierId = cashierId, OpeningCash = openingCash, OpenedAt = now };
    }

    public Result Close(decimal closingCash, decimal expectedCash, DateTimeOffset now)
    {
        if (!IsOpen)
        {
            return Error.Conflict("shift.already_closed", "The shift is already closed.");
        }

        if (closingCash < 0 || !Money.IsValidAmount(closingCash))
        {
            return Error.Validation("shift.invalid_amount", "Closing cash must be non-negative with at most 2 decimal places.");
        }

        ClosingCash = closingCash;
        ExpectedCash = expectedCash;
        ClosedAt = now;
        return Result.Success();
    }
}
