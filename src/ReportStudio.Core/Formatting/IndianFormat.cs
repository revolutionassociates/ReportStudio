using System.Globalization;
using System.Text;

namespace ReportStudio.Core.Formatting;

/// <summary>
/// Indian number, date and amount formatting. Built from the invariant culture, so it behaves the same
/// on every machine (it does not depend on installed regional data).
/// </summary>
public static class IndianFormat
{
    /// <summary>Digit grouping 12,34,567.00; dates dd-MM-yyyy; currency symbol ₹.</summary>
    public static CultureInfo Culture { get; } = Build();

    private static CultureInfo Build()
    {
        var c = (CultureInfo)CultureInfo.InvariantCulture.Clone();

        var n = c.NumberFormat;
        n.NumberGroupSizes = new[] { 3, 2 };      // last size repeats: 1,23,45,678
        n.CurrencyGroupSizes = new[] { 3, 2 };
        n.CurrencySymbol = "₹";
        n.CurrencyDecimalDigits = 2;
        n.CurrencyPositivePattern = 0;            // ₹1,234.00
        n.CurrencyNegativePattern = 1;            // -₹1,234.00

        var d = c.DateTimeFormat;
        d.DateSeparator = "-";
        d.ShortDatePattern = "dd-MM-yyyy";

        return CultureInfo.ReadOnly(c);
    }

    /// <summary>Rounds money to 2 decimals, halves away from zero (the payroll convention).</summary>
    public static decimal RoundMoney(decimal amount) => Math.Round(amount, 2, MidpointRounding.AwayFromZero);

    private static readonly string[] Ones =
    {
        "Zero", "One", "Two", "Three", "Four", "Five", "Six", "Seven", "Eight", "Nine", "Ten", "Eleven", "Twelve",
        "Thirteen", "Fourteen", "Fifteen", "Sixteen", "Seventeen", "Eighteen", "Nineteen"
    };

    private static readonly string[] Tens =
        { "", "", "Twenty", "Thirty", "Forty", "Fifty", "Sixty", "Seventy", "Eighty", "Ninety" };

    /// <summary>
    /// Amount in rupees and paise in words, Indian system, e.g. 123456.50 becomes
    /// "Rupees One Lakh Twenty Three Thousand Four Hundred Fifty Six and Fifty Paise Only". Null gives "".
    /// </summary>
    public static string AmountInWords(decimal? value)
    {
        if (value == null) return "";

        var rounded = RoundMoney(value.Value);
        bool negative = rounded < 0;
        rounded = Math.Abs(rounded);

        var rupees = decimal.Truncate(rounded);
        int paise = (int)((rounded - rupees) * 100m);
        if (rupees > 999_999_999_999_999m)
            throw new ArgumentOutOfRangeException(nameof(value), "Amount is too large to write in words.");

        var sb = new StringBuilder();
        if (negative) sb.Append("Minus ");

        if (rupees > 0 || paise == 0)
        {
            sb.Append("Rupees ").Append(Words((long)rupees));
            if (paise > 0) sb.Append(" and ");
        }
        if (paise > 0) sb.Append(Words(paise)).Append(" Paise");

        return sb.Append(" Only").ToString();
    }

    /// <summary>A whole number in words using crore, lakh and thousand.</summary>
    public static string Words(long n)
    {
        if (n == 0) return "Zero";
        var parts = new List<string>();

        long crore = n / 10_000_000;
        if (crore > 0) parts.Add(Words(crore) + " Crore");   // crores can themselves be large
        n %= 10_000_000;

        AddPart(parts, n / 100_000, "Lakh");
        n %= 100_000;
        AddPart(parts, n / 1_000, "Thousand");
        n %= 1_000;
        AddPart(parts, n / 100, "Hundred");
        n %= 100;

        if (n > 0) parts.Add(Below100((int)n));
        return string.Join(" ", parts);
    }

    private static void AddPart(List<string> parts, long count, string unit)
    {
        if (count > 0) parts.Add(Below100((int)count) + " " + unit);
    }

    private static string Below100(int n) =>
        n < 20 ? Ones[n] : n % 10 == 0 ? Tens[n / 10] : Tens[n / 10] + " " + Ones[n % 10];
}
