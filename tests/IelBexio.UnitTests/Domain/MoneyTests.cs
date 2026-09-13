using IelBexio.Domain.Common;

namespace IelBexio.UnitTests.Domain;

/// <summary>
/// Money is the foundation of every financial assertion in the system, so its invariants are tested
/// before anything that depends on them (§21).
/// </summary>
public sealed class MoneyTests
{
    [Fact]
    public void Constructor_normalises_the_currency_code_to_upper_case()
    {
        new Money(10m, "chf").Currency.Should().Be("CHF");
        new Money(10m, " eur ").Currency.Should().Be("EUR");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("CH")]
    [InlineData("CHFX")]
    [InlineData("12F")]
    public void Constructor_rejects_anything_that_is_not_an_iso_4217_alphabetic_code(string currency)
    {
        var act = () => new Money(10m, currency);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Adding_two_different_currencies_is_impossible_rather_than_merely_discouraged()
    {
        var chf = new Money(100m, "CHF");
        var eur = new Money(100m, "EUR");

        var act = () => _ = chf + eur;

        act.Should().Throw<CurrencyMismatchException>()
            .WithMessage("*CHF*EUR*");
    }

    [Fact]
    public void Arithmetic_within_one_currency_behaves_exactly()
    {
        var subtotal = new Money(1000m, "CHF");
        var tax = new Money(81m, "CHF");

        (subtotal + tax).Should().Be(new Money(1081m, "CHF"));
        (subtotal - tax).Should().Be(new Money(919m, "CHF"));
        (subtotal * 2m).Should().Be(new Money(2000m, "CHF"));
    }

    [Fact]
    public void Decimal_arithmetic_does_not_accumulate_binary_floating_point_error()
    {
        // The canonical demonstration: 0.1 + 0.2 != 0.3 in binary floating point. With decimal it does.
        var total = new Money(0.1m, "CHF") + new Money(0.2m, "CHF");
        total.Amount.Should().Be(0.3m);

        // And summing a hundred rappen amounts lands exactly on a franc.
        var hundred = Money.Sum(Enumerable.Repeat(new Money(0.01m, "CHF"), 100), "CHF");
        hundred.Amount.Should().Be(1.00m);
    }

    [Fact]
    public void Amounts_are_rounded_to_the_storage_scale_banker_style()
    {
        // Round-half-to-even at the 4-decimal storage scale.
        new Money(1.00005m, "CHF").Amount.Should().Be(1.0000m);
        new Money(1.00015m, "CHF").Amount.Should().Be(1.0002m);
    }

    [Theory]
    [InlineData("CHF", 2)]
    [InlineData("EUR", 2)]
    [InlineData("USD", 2)]
    [InlineData("JPY", 0)]
    [InlineData("KWD", 3)]
    public void Currency_presentation_scale_is_known_for_the_currencies_the_poc_handles(string currency, int expectedScale)
    {
        Money.ScaleFor(currency).Should().Be(expectedScale);
    }

    [Fact]
    public void Sum_of_an_empty_sequence_still_carries_an_explicit_currency()
    {
        var sum = Money.Sum([], "CHF");
        sum.Should().Be(Money.Zero("CHF"));
        sum.Currency.Should().Be("CHF");
    }

    [Fact]
    public void Comparison_requires_a_shared_currency()
    {
        (new Money(2m, "CHF") > new Money(1m, "CHF")).Should().BeTrue();

        var act = () => _ = new Money(2m, "CHF") > new Money(1m, "EUR");
        act.Should().Throw<CurrencyMismatchException>();
    }
}
