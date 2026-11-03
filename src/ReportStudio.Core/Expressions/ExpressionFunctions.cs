namespace ReportStudio.Core.Expressions;

/// <summary>One function the expression editor offers: how it is written, what it does, an example.</summary>
public sealed record FunctionInfo(string Category, string Name, string Signature, string Description, string Example);

/// <summary>
/// The list of functions shown in the designer's expression editor. A test makes sure every name here is a function the
/// engine really knows, so the list cannot drift from the engine.
/// </summary>
public static class ExpressionFunctions
{
    public const string Totals = "Totals";
    public const string Logic = "Logic";
    public const string Text = "Text";
    public const string Numbers = "Numbers";
    public const string Dates = "Dates";
    public const string Ranges = "Ranges (for grouping)";

    public static IReadOnlyList<FunctionInfo> All { get; } = new List<FunctionInfo>
    {
        new(Totals, "Sum", "Sum(value)", "Adds the value over the rows of the current group (the whole report in the report footer).", "Sum(Amount)"),
        new(Totals, "Count", "Count()  or  Count(value)", "Counts the rows, or only the rows where the value is not empty.", "Count()"),
        new(Totals, "CountDistinct", "CountDistinct(value)", "Counts the different values that are not empty.", "CountDistinct(Department)"),
        new(Totals, "Avg", "Avg(value)", "Average of the values that are not empty.", "Avg(Net)"),
        new(Totals, "Min", "Min(value)", "Smallest value (numbers, dates or text).", "Min(JoinDate)"),
        new(Totals, "Max", "Max(value)", "Largest value (numbers, dates or text).", "Max(Net)"),

        new(Logic, "IIf", "IIf(condition, whenTrue, whenFalse)", "Gives one value when the condition is true and another when it is not. 'If' works too.", "IIf(Net > 50000, 'High', 'Normal')"),
        new(Logic, "IsNull", "IsNull(value, other)", "The value, or the other value when it is empty.", "IsNull(Remarks, '-')"),

        new(Text, "Format", "Format(value, format)", "Shows a number or date in a format such as N2, #,##0.00 or dd-MMM-yyyy.", "Format(JoinDate, 'dd-MMM-yyyy')"),
        new(Text, "Upper", "Upper(text)", "Capital letters.", "Upper(Name)"),
        new(Text, "Lower", "Lower(text)", "Small letters.", "Lower(Email)"),
        new(Text, "Trim", "Trim(text)", "Removes spaces at the start and end.", "Trim(Name)"),
        new(Text, "Len", "Len(text)", "Number of characters.", "Len(PanNo)"),
        new(Text, "Left", "Left(text, count)", "The first characters.", "Left(Name, 3)"),
        new(Text, "Right", "Right(text, count)", "The last characters.", "Right(Account, 4)"),
        new(Text, "Mid", "Mid(text, start, count)", "Characters from a position (the first character is 1). Without count: up to the end.", "Mid(Code, 3, 2)"),
        new(Text, "Replace", "Replace(text, find, with)", "Replaces every occurrence of one text by another.", "Replace(Phone, '-', '')"),
        new(Text, "ToText", "ToText(value)", "The value as text. 'Str' works too.", "ToText(EmpCode)"),

        new(Numbers, "Round", "Round(number, decimals)", "Rounds half away from zero; decimals can be left out (0).", "Round(Net / 12, 2)"),
        new(Numbers, "Abs", "Abs(number)", "The number without its minus sign.", "Abs(Difference)"),
        new(Numbers, "AmountInWords", "AmountInWords(number)", "Rupees and paise in words, Indian style. 'RupeesWords' works too.", "AmountInWords(Net)"),

        new(Dates, "Today", "Today()", "Today's date.", "Today()"),
        new(Dates, "Now", "Now()", "Today's date and the time.", "Now()"),
        new(Dates, "Year", "Year(date)", "The year of a date.", "Year(JoinDate)"),
        new(Dates, "Month", "Month(date)", "The month of a date, 1 to 12.", "Month(JoinDate)"),
        new(Dates, "Day", "Day(date)", "The day of the month.", "Day(JoinDate)"),

        new(Ranges, "MonthStart", "MonthStart(date)", "First day of the month. Group by it to get one group per month.", "MonthStart(InvoiceDate)"),
        new(Ranges, "QuarterStart", "QuarterStart(date)", "First day of the calendar quarter.", "QuarterStart(InvoiceDate)"),
        new(Ranges, "YearStart", "YearStart(date)", "First day of the calendar year.", "YearStart(InvoiceDate)"),
        new(Ranges, "WeekStart", "WeekStart(date)", "The Monday of the week.", "WeekStart(InvoiceDate)"),
        new(Ranges, "FinancialYear", "FinancialYear(date)", "Indian financial year (April to March) as text, such as 2025-26.", "FinancialYear(InvoiceDate)"),
        new(Ranges, "Bucket", "Bucket(number, size)", "Lower end of the equal-sized range the number falls in: Bucket(1750, 1000) is 1000.", "Bucket(Amount, 10000)"),
        new(Ranges, "Range", "Range(number, limit1, limit2, ...)", "Lower limit of the range, among limits you choose. Groups sort in order.", "Range(Amount, 1000, 5000, 20000)"),
        new(Ranges, "RangeLabel", "RangeLabel(number, limit1, limit2, ...)", "Like Range, but gives a readable label such as '1,000 to below 5,000'.", "RangeLabel(Amount, 1000, 5000, 20000)"),
    };

    /// <summary>Values that are written without brackets.</summary>
    public static IReadOnlyList<(string Name, string Description)> SpecialValues { get; } = new List<(string, string)>
    {
        ("Page", "The page number."),
        ("PageCount", "The number of pages in the report."),
        ("RowNumber", "The number of the row being printed, starting at 1."),
        ("@Parameter", "The value of a report parameter, e.g. @FromDate."),
    };

    public static IEnumerable<string> Categories => All.Select(f => f.Category).Distinct();
}
