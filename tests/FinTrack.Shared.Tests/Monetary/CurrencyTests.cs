using FinTrack.Shared.Kernel.Monetary;
using FinTrack.Shared.Kernel.Results;
using FinTrack.Tests.Common;

namespace FinTrack.Shared.Tests.Monetary;

public class CurrencyTests
{
    [Theory]
    [InlineData("PLN", 2)]
    [InlineData("EUR", 2)]
    [InlineData("USD", 2)]
    public void FromCode_RegisteredCurrency_ReturnsIt(string code, int decimalPlaces)
    {
        var result = Currency.FromCode(code);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Code.ShouldBe(code);
        result.Value.DecimalPlaces.ShouldBe(decimalPlaces);
    }

    [Fact]
    public void FromCode_ReturnsTheSameInstanceAsTheStaticField() =>
        Currency.FromCode("PLN").Value.ShouldBeSameAs(Currency.Pln);

    [Theory]
    [InlineData("XYZ")]
    [InlineData("pln")]
    [InlineData(" PLN")]
    [InlineData("")]
    public void FromCode_UnknownCode_Fails(string code)
    {
        var result = Currency.FromCode(code);

        result.ShouldFailWith(ErrorCodes.CurrencyUnknown, ErrorKind.Validation)
            .Parameters.ShouldBe(new Dictionary<string, object> { ["currency"] = code });
    }

    [Fact]
    public void FromCode_Null_Fails() =>
        Currency.FromCode(null).ShouldFailWith(ErrorCodes.CurrencyUnknown, ErrorKind.Validation)
            .Parameters.ShouldBeEmpty();

    [Fact]
    public void Equality_IsByCodeAndPrecision()
    {
        Currency.Pln.ShouldBe(Currency.FromCode("PLN").Value);
        Currency.Pln.ShouldNotBe(Currency.Eur);
    }

    [Fact]
    public void ToString_IsTheCode() => Currency.Usd.ToString().ShouldBe("USD");

    [Theory]
    [InlineData("A1", 0)]
    [InlineData("USDT", 6)]
    [InlineData("ETH", 18)]
    public void Define_ValidDefinition_Works(string code, int decimalPlaces) =>
        Currency.Define(code, decimalPlaces).DecimalPlaces.ShouldBe(decimalPlaces);

    [Theory]
    [InlineData("P", 2)]
    [InlineData("pln", 2)]
    [InlineData("PL N", 2)]
    [InlineData("ABCDEFGHIJK", 2)]
    [InlineData("PLN", -1)]
    [InlineData("PLN", 19)]
    public void Define_InvalidDefinition_Throws(string code, int decimalPlaces) =>
        Should.Throw<ArgumentException>(() => Currency.Define(code, decimalPlaces));
}
