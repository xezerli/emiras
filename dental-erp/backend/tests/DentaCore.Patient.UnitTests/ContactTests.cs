using DentaCore.Patient.Application;

namespace DentaCore.Patient.UnitTests;

public class ContactNormalizerTests
{
    [Theory]
    [InlineData("+994 50 123-45-67", "+994501234567")]
    [InlineData("0501234567", "+994501234567")]
    [InlineData("00994501234567", "+994501234567")]
    [InlineData("(050) 123 45 67", "+994501234567")]
    [InlineData("501234567", "+994501234567")]
    [InlineData("+90 532 123 45 67", "+905321234567")]
    public void Different_spellings_normalize_to_the_same_e164(string raw, string expected) =>
        Assert.Equal(expected, ContactNormalizer.Phone(raw, "994").Value);

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("+99450")]
    [InlineData("+9945012345678901234")]
    public void Invalid_phone_is_rejected(string raw) => Assert.Equal("patient.invalid_phone", ContactNormalizer.Phone(raw, "994").Error!.Code);

    [Fact]
    public void Email_and_national_id_are_case_normalized()
    {
        Assert.Equal("a@b.az", ContactNormalizer.Email("  A@B.az "));
        Assert.Equal("AZE1234567", ContactNormalizer.NationalId(" aze1234567 "));
    }

    [Theory]
    [InlineData("050 123 45 67", true)]
    [InlineData("Aysel", false)]
    [InlineData("12345", false)]      // 7 rəqəmdən az: kart nömrəsidir, telefon deyil
    [InlineData("AZE1234567", false)]  // hərf var: FİN
    public void Search_text_is_recognized_as_phone_only_when_it_looks_like_one(string query, bool isPhone) =>
        Assert.Equal(isPhone, ContactNormalizer.TryPhone(query, "994") is not null);
}

public class MaskingTests
{
    [Fact]
    public void Masks_never_reveal_more_than_the_tail()
    {
        Assert.Equal("+994*******67", Masking.Phone("+994501234567"));
        Assert.Equal("a***@clinic.az", Masking.Email("aysel@clinic.az"));
        Assert.Equal("***@x.az", Masking.Email("a@x.az"));
        Assert.Equal("***67", Masking.NationalId("AZE1234567"));
        Assert.Equal("***", Masking.NationalId("A1"));
    }
}
