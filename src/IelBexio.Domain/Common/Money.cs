namespace IelBexio.Domain.Common;

/// <summary>
/// A monetary amount with an explicit currency. Never a bare <c>decimal</c>: the specification
/// (§21) requires every monetary value to carry its currency, and arithmetic across currencies
/// must be impossible by construction rather than by convention.
/// </summary>
public readonly record struct Money : IComparable<Money>
{
    /// <summary>Scale used for all persisted monetary amounts (PostgreSQL numeric(19,4)).</summary>
    public const int StorageScale = 4;

    public decimal Amount { get; }

    /// <summary>ISO-4217 alphabetic code, upper case.</summary>
    public string Currency { get; }

    public Money(decimal amount, string currency)
    {
        if (string.IsNullOrWhiteSpace(currency))
        {
            throw new ArgumentException("Currency is required for a monetary amount.", nameof(currency));
        }

        var normalized = currency.Trim().ToUpperInvariant();
        if (normalized.Length != 3 || !normalized.All(char.IsAsciiLetterUpper))
        {
            throw new ArgumentException(
                $"Currency '{currency}' is not a 3-letter ISO-4217 alphabetic code.", nameof(currency));
        }

        Amount = decimal.Round(amount, StorageScale, MidpointRounding.ToEven);
        Currency = normalized;
    }

    public static Money Zero(string currency) => new(0m, currency);

    public bool IsZero => Amount == 0m;
    public bool IsNegative => Amount < 0m;

    private static void AssertSameCurrency(in Money a, in Money b)
    {
        if (!string.Equals(a.Currency, b.Currency, StringComparison.Ordinal))
        {
            throw new CurrencyMismatchException(a.Currency, b.Currency);
        }
    }

    public static Money operator +(Money a, Money b)
    {
        AssertSameCurrency(a, b);
        return new Money(a.Amount + b.Amount, a.Currency);
    }

    public static Money operator -(Money a, Money b)
    {
        AssertSameCurrency(a, b);
        return new Money(a.Amount - b.Amount, a.Currency);
    }

    public static Money operator *(Money a, decimal factor) => new(a.Amount * factor, a.Currency);

    public static Money Add(Money left, Money right) => left + right;
    public static Money Subtract(Money left, Money right) => left - right;
    public static Money Multiply(Money left, decimal factor) => left * factor;

    /// <summary>Sums amounts that must all share one currency. Empty sums require an explicit currency.</summary>
    public static Money Sum(IEnumerable<Money> amounts, string currency)
    {
        ArgumentNullException.ThrowIfNull(amounts);
        var total = Zero(currency);
        foreach (var m in amounts)
        {
            total += m;
        }

        return total;
    }

    /// <summary>Absolute difference, for tolerance comparisons. Both operands must share a currency.</summary>
    public static decimal AbsoluteDifference(Money a, Money b)
    {
        AssertSameCurrency(a, b);
        return Math.Abs(a.Amount - b.Amount);
    }

    /// <summary>
    /// Rounds to the currency's normal presentation scale. CHF/EUR/USD use 2 decimals; the
    /// zero-decimal and three-decimal currencies relevant to the POC are listed explicitly.
    /// </summary>
    public Money RoundToCurrencyScale() => new(decimal.Round(Amount, ScaleFor(Currency), MidpointRounding.ToEven), Currency);

    public static int ScaleFor(string currency) => currency.ToUpperInvariant() switch
    {
        "JPY" or "KRW" or "CLP" or "ISK" or "VND" => 0,
        "BHD" or "JOD" or "KWD" or "OMR" or "TND" => 3,
        _ => 2,
    };

    public static bool operator <(Money left, Money right) => left.CompareTo(right) < 0;
    public static bool operator <=(Money left, Money right) => left.CompareTo(right) <= 0;
    public static bool operator >(Money left, Money right) => left.CompareTo(right) > 0;
    public static bool operator >=(Money left, Money right) => left.CompareTo(right) >= 0;

    public int CompareTo(Money other)
    {
        AssertSameCurrency(this, other);
        return Amount.CompareTo(other.Amount);
    }

    public override string ToString() => $"{Amount.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)} {Currency}";
}

public sealed class CurrencyMismatchException : InvalidOperationException
{
    public CurrencyMismatchException(string left, string right)
        : base($"Currency mismatch: cannot combine '{left}' with '{right}'.")
    {
        Left = left;
        Right = right;
    }

    public CurrencyMismatchException() : base("Currency mismatch.") { }

    public CurrencyMismatchException(string message) : base(message) { }

    public CurrencyMismatchException(string message, Exception innerException) : base(message, innerException) { }

    public string? Left { get; }
    public string? Right { get; }
}
