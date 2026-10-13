using DentaCore.Billing.Domain;

namespace DentaCore.Billing.UnitTests;

public sealed class ShiftAndServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 10, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Shift_close_computes_the_difference_between_counted_and_expected_cash()
    {
        var shift = CashShift.Open(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 50m, Now).Value;
        Assert.True(shift.IsOpen);

        Assert.True(shift.Close(closingCash: 395m, expectedCash: 400m, Now.AddHours(8)).IsSuccess);

        Assert.False(shift.IsOpen);
        Assert.Equal(-5m, shift.Difference);   // kassada 5 çatışmır
    }

    [Fact]
    public void Shift_cannot_be_closed_twice()
    {
        var shift = CashShift.Open(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 0m, Now).Value;
        shift.Close(10m, 10m, Now);

        Assert.Equal("shift.already_closed", shift.Close(10m, 10m, Now).Error!.Code);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1.001)]
    public void Shift_amounts_are_validated(double amount)
    {
        Assert.Equal("shift.invalid_amount", CashShift.Open(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), (decimal)amount, Now).Error!.Code);
        var shift = CashShift.Open(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 0m, Now).Value;
        Assert.Equal("shift.invalid_amount", shift.Close((decimal)amount, 0m, Now).Error!.Code);
    }

    [Fact]
    public void Service_trims_input_and_uppercases_currency()
    {
        var service = Service.Create(Guid.NewGuid(), " D2391 ", "D2391", " Plomb ", " Terapiya ", 45m, "azn", 18m).Value;

        Assert.Equal("D2391", service.Code);
        Assert.Equal("Plomb", service.Name);
        Assert.Equal("Terapiya", service.Category);
        Assert.Equal("AZN", service.Currency);
        Assert.True(service.IsActive);
    }

    [Theory]
    [InlineData("", "n", 1, "AZN", 0, "service.invalid_code")]
    [InlineData("c", " ", 1, "AZN", 0, "service.invalid_name")]
    [InlineData("c", "n", -1, "AZN", 0, "service.invalid_price")]
    [InlineData("c", "n", 1.234, "AZN", 0, "service.invalid_price")]
    [InlineData("c", "n", 1, "AZ", 0, "service.invalid_currency")]
    [InlineData("c", "n", 1, "AZN", 101, "service.invalid_vat")]
    public void Service_validation(string code, string name, double price, string currency, double vat, string expected)
    {
        var result = Service.Create(Guid.NewGuid(), code, null, name, null, (decimal)price, currency, (decimal)vat);

        Assert.Equal(expected, result.Error!.Code);
    }

    [Fact]
    public void Service_update_does_not_change_state_when_invalid()
    {
        var service = Service.Create(Guid.NewGuid(), "c", null, "Ad", null, 10m, "AZN", 0m).Value;

        var result = service.Update("Ad2", null, -5m, 0m, false);

        Assert.True(result.IsFailure);
        Assert.Equal(10m, service.Price);
        Assert.True(service.IsActive);
    }

    [Theory]
    [InlineData("cash", true)]
    [InlineData("card", true)]
    [InlineData("pos", true)]
    [InlineData("transfer", true)]
    [InlineData("gift_card", false)]
    [InlineData("insurance", false)]
    [InlineData("CASH", false)]
    public void Only_implemented_payment_methods_are_supported(string method, bool expected) =>
        Assert.Equal(expected, PaymentMethods.IsSupported(method));
}
