namespace DentaCore.Billing.Domain;

public static class Money
{
    /// <summary>Pul məbləği ən çox 2 onluq mərtəbə: DB numeric(14,2) səssizcə yuvarlaqlaşdırmasın deyə domen rədd edir.</summary>
    public static bool IsValidAmount(decimal value) => decimal.Round(value, 2) == value && Math.Abs(value) < 1_000_000_000_000m;

    public static decimal Round(decimal value) => decimal.Round(value, 2, MidpointRounding.AwayFromZero);
}
