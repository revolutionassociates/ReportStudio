using ReportStudio.Core.Data;
using ReportStudio.Core.Expressions;
using ReportStudio.Core.Model;

namespace ReportStudio.Core.Layout;

using Row = Dictionary<string, object?>;

/// <summary>
/// Real data for the designer canvas (requirement 26): the text of an element as it would print for one data row, so the
/// designer shows "Ravi Kumar" instead of "{Name}". Uses the same template and expression engine as the report run.
/// Aggregates ({Sum(Amount)}) see all loaded rows; page numbers show page 1 of 1.
/// </summary>
public sealed class DesignPreview
{
    /// <summary>Rows read for the preview; a designer needs a sample, not a whole table.</summary>
    public const int DefaultMaxRows = 200;

    private readonly List<Row> _rows;
    private readonly Dictionary<string, object?> _parameters;
    private int _current;

    private DesignPreview(List<Row> rows, Dictionary<string, object?> parameters)
    {
        _rows = rows;
        _parameters = parameters;
    }

    public int RowCount => _rows.Count;

    /// <summary>Index (from 0) of the row shown on the canvas.</summary>
    public int CurrentIndex
    {
        get => _current;
        set => _current = _rows.Count == 0 ? 0 : Math.Clamp(value, 0, _rows.Count - 1);
    }

    /// <summary>Shows the next row, wrapping to the first after the last.</summary>
    public void Next() => CurrentIndex = _rows.Count == 0 ? 0 : (_current + 1) % _rows.Count;

    /// <summary>Loads the report's rows (at most <paramref name="maxRows"/>). Throws when the data source cannot be read.</summary>
    public static DesignPreview Load(ReportDefinition def, IDictionary<string, string?>? rawParameters, int maxRows = DefaultMaxRows,
        string? baseDirectory = null)
    {
        var parameters = ReportEngine.ResolveParameters(def, rawParameters);
        var rows = DataLoader.Load(def.DataSource, parameters, new ReportStudio.Core.Data.Sources.SourceSettings { BaseDirectory = baseDirectory });
        return new DesignPreview(rows.Take(Math.Max(1, maxRows)).ToList(), parameters);
    }

    /// <summary>A preview over rows the caller already has (the Host data source, tests).</summary>
    public static DesignPreview From(ReportDefinition def, IEnumerable<Row> rows, IDictionary<string, string?>? rawParameters = null) =>
        new(rows.Take(DefaultMaxRows).ToList(), ReportEngine.ResolveParameters(def, rawParameters));

    /// <summary>
    /// The text the element would print for the current row. Elements that show no text expression, and expressions
    /// that cannot be evaluated, keep their design text so the canvas never shows an error.
    /// </summary>
    public string TextFor(ReportItem item)
    {
        if (item.Kind is not (ItemKind.Text or ItemKind.Barcode or ItemKind.QrCode or ItemKind.Barcode2D)) return item.Text;
        if (_rows.Count == 0) return item.Text;

        var template = TextTemplate.Parse(item.Text);
        if (!template.HasExpressions) return template.EvaluateString(Context(), null);
        try { return template.EvaluateString(Context(), item.Format); }
        catch (Exception) { return item.Text; }
    }

    private EvalContext Context() => new()
    {
        Row = _rows[_current],
        Scope = _rows,
        Parameters = _parameters,
        RowNumber = _current + 1
    };
}
