using System.Globalization;

namespace Tetragon.SharedKernel;

/// <summary>통화를 포함한 금액 VO. 금액 계산은 반드시 이 타입만 사용한다 (설계서 5.8).</summary>
public readonly record struct Money(decimal Amount, string Currency)
{
    public static Money Krw(decimal amount) => new(amount, "KRW");
    public static Money Cny(decimal amount) => new(amount, "CNY");
    public static Money Usd(decimal amount) => new(amount, "USD");

    public Money Add(Money other)
    {
        EnsureSameCurrency(other);
        return this with { Amount = Amount + other.Amount };
    }

    public Money Multiply(decimal factor) => this with { Amount = Amount * factor };

    public Money ConvertTo(string currency, decimal rate) => new(Amount * rate, currency);

    /// <summary>절사: 원 단위 내림 (예: 12,345.6 → 12,340 KRW는 10원 단위 내림 등 단위 지정).</summary>
    public Money FloorTo(int unit) => unit <= 1
        ? this with { Amount = Math.Floor(Amount) }
        : this with { Amount = Math.Floor(Amount / unit) * unit };

    private void EnsureSameCurrency(Money other)
    {
        if (Currency != other.Currency)
            throw new InvalidOperationException($"통화 불일치: {Currency} vs {other.Currency}");
    }

    public override string ToString() => $"{Amount.ToString("N0", CultureInfo.InvariantCulture)} {Currency}";
}

/// <summary>다국어 텍스트. { "zh-CN": "...", "ko-KR": "..." }</summary>
public sealed record LocalizedText
{
    public Dictionary<string, string> Values { get; init; } = [];

    public static LocalizedText Of(string locale, string value) =>
        new() { Values = new Dictionary<string, string> { [locale] = value } };

    public string? Get(string locale) => Values.GetValueOrDefault(locale);
    public string GetOrFirst(string locale) =>
        Values.GetValueOrDefault(locale) ?? Values.Values.FirstOrDefault() ?? string.Empty;

    public LocalizedText With(string locale, string value)
    {
        var next = new Dictionary<string, string>(Values) { [locale] = value };
        return new LocalizedText { Values = next };
    }
}

/// <summary>공급처 원본 참조: 공급처 코드 + 원본 상품 ID + URL.</summary>
public sealed record SourceRef(string SupplierCode, string SourceProductId, string Url);

/// <summary>기간 VO.</summary>
public readonly record struct DateRange(DateTimeOffset From, DateTimeOffset To);
