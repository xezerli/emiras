using DentaCore.BuildingBlocks.Domain;

namespace DentaCore.Billing.Domain;

/// <summary>Qiymət siyahısı bəndi. Qiymət dəyişəndə köhnə fakturalar toxunulmaz qalır: faktura sətri qiyməti kopyalayır.</summary>
public sealed class Service : AggregateRoot<Guid>
{
    private Service()
        : base(Guid.Empty)
    {
    }

    public string Code { get; private set; } = string.Empty;

    public string? ProcedureCode { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public string? Category { get; private set; }

    public decimal Price { get; private set; }

    public string Currency { get; private set; } = "AZN";

    public decimal VatRate { get; private set; }

    public bool IsActive { get; private set; } = true;

    public static Result<Service> Create(Guid id, string code, string? procedureCode, string name, string? category, decimal price, string currency, decimal vatRate)
    {
        var service = new Service { Id = id, ProcedureCode = procedureCode, IsActive = true };
        var applied = service.Apply(code, name, category, price, currency, vatRate);
        return applied.IsFailure ? applied.Error! : service;
    }

    public Result Update(string name, string? category, decimal price, decimal vatRate, bool isActive)
    {
        var applied = Apply(Code, name, category, price, Currency, vatRate);
        if (applied.IsSuccess)
        {
            IsActive = isActive;
        }

        return applied;
    }

    private Result Apply(string code, string name, string? category, decimal price, string currency, decimal vatRate)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Trim().Length > 40)
        {
            return Error.Validation("service.invalid_code", "Code is required (max 40 characters).");
        }

        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 200)
        {
            return Error.Validation("service.invalid_name", "Name is required (max 200 characters).");
        }

        if (price < 0 || !Money.IsValidAmount(price))
        {
            return Error.Validation("service.invalid_price", "Price must be non-negative with at most 2 decimal places.");
        }

        if (string.IsNullOrWhiteSpace(currency) || currency.Length != 3)
        {
            return Error.Validation("service.invalid_currency", "Currency must be a 3-letter code.");
        }

        if (vatRate is < 0 or > 100)
        {
            return Error.Validation("service.invalid_vat", "VAT rate must be between 0 and 100.");
        }

        Code = code.Trim();
        Name = name.Trim();
        Category = string.IsNullOrWhiteSpace(category) ? null : category.Trim();
        Price = price;
        Currency = currency.ToUpperInvariant();
        VatRate = vatRate;
        return Result.Success();
    }
}
