using System.Globalization;
using System.Reflection;
using System.Text.Json;
using ReportStudio.Core.Cli;
using ReportStudio.Core.Data.Sources;
using ReportStudio.Core.Formatting;
using ReportStudio.Core.Layout;
using ReportStudio.Core.Model;

namespace ReportStudio.Export;

/// <summary>
/// Runs a report from the command line (requirement 48): loads the .rsr file, runs it once and writes every output asked for.
/// A report file is somebody else's input, so the same trust rules as the viewer apply: a report that carries its own
/// database query needs <c>--trust</c>, and data files or web addresses need <c>--allow-data-files</c> / <c>--allow-web-data</c>.
/// Nothing is ever asked on the console, so the command is safe in a scheduled job.
/// Exit codes: 0 done, 1 failed, 2 wrong command line, 3 refused, 4 warnings with --fail-on-warning.
/// </summary>
public static class ReportCommand
{
    public const int ExitOk = 0;
    public const int ExitFailed = 1;
    public const int ExitUsage = 2;
    public const int ExitRefused = 3;
    public const int ExitWarnings = 4;

    private const int MaxQueryChars = 600;

    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        var cli = CliArguments.Parse(args);
        if (cli.Error != null)
        {
            error.WriteLine("Error: " + cli.Error);
            if (cli.Command == CliArguments.CommandHelp) error.WriteLine("Use 'reportstudio help' to see how to use it.");
            return ExitUsage;
        }

        switch (cli.Command)
        {
            case CliArguments.CommandHelp:
                output.WriteLine(CliArguments.HelpText);
                return ExitOk;
            case CliArguments.CommandVersion:
                output.WriteLine("ReportStudio " + Version());
                return ExitOk;
        }

        try
        {
            return RunReport(cli, output, error);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            error.WriteLine("Failed: " + ex.Message);
            return ExitFailed;
        }
    }

    private static string Version() =>
        typeof(ReportCommand).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(ReportCommand).Assembly.GetName().Version?.ToString()
        ?? "unknown";

    private static int RunReport(CliArguments cli, TextWriter output, TextWriter error)
    {
        var reportPath = Path.GetFullPath(cli.ReportPath!);
        if (!File.Exists(reportPath))
        {
            error.WriteLine($"Failed: the report file '{cli.ReportPath}' was not found.");
            return ExitFailed;
        }

        ReportDefinition def;
        try
        {
            def = ReportDefinition.Load(reportPath);
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            error.WriteLine($"Failed: the report file '{cli.ReportPath}' could not be read: {ex.Message}");
            return ExitFailed;
        }

        // What the report reads from outside is decided here, before anything is run.
        var refusal = Refusal(cli, def);
        if (refusal != null)
        {
            error.WriteLine(refusal);
            return ExitRefused;
        }

        if (cli.Command == CliArguments.CommandParams)
        {
            ListParameters(def, output);
            return ExitOk;
        }

        if (def.DataSource.Provider == DbProvider.Host)
        {
            error.WriteLine("Failed: this report takes its data from a host application, so it cannot be run from the command line. " +
                            "Change its data source in the designer first.");
            return ExitFailed;
        }

        var parameters = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var unknown = new List<string>();
        foreach (var (name, value) in cli.Parameters)
        {
            var known = def.Parameters.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            if (known == null) unknown.Add(name);
            else parameters[known.Name] = value;
        }
        if (unknown.Count > 0)
        {
            error.WriteLine("Error: the report has no parameter named " + string.Join(", ", unknown.Select(u => $"'{u}'")) + ".");
            error.WriteLine(def.Parameters.Count == 0
                ? "This report has no parameters."
                : "Its parameters are: " + string.Join(", ", def.Parameters.Select(p => p.Name)) + ".");
            return ExitUsage;
        }

        CultureInfo culture;
        try
        {
            culture = string.IsNullOrWhiteSpace(cli.Culture) ? IndianFormat.Culture : CultureInfo.GetCultureInfo(cli.Culture);
        }
        catch (CultureNotFoundException)
        {
            error.WriteLine($"Error: '{cli.Culture}' is not a culture name. Examples: en-IN, en-US, de-DE.");
            return ExitUsage;
        }

        // Never overwrite the report with an output (a mistyped name would destroy the design).
        foreach (var o in cli.Outputs)
        {
            if (string.Equals(Path.GetFullPath(o.Path), reportPath, StringComparison.OrdinalIgnoreCase))
            {
                error.WriteLine($"Error: the output '{o.Path}' is the report file itself.");
                return ExitUsage;
            }
        }

        var options = new ReportOptions
        {
            Culture = culture,
            BaseDirectory = Path.GetDirectoryName(reportPath),
            SourceRoot = cli.DataRoot == null ? null : Path.GetFullPath(cli.DataRoot),
            Access = new SourceAccess { AllowFileSources = cli.AllowDataFiles, AllowWebSources = cli.AllowWebData }
        };

        ReportResult result;
        using (var measurer = new GdiTextMeasurer())
        {
            options.Measurer = measurer;
            result = ReportEngine.Run(def.Clone(), parameters, options);
        }

        foreach (var w in result.Warnings) error.WriteLine("warning: " + w);
        if (!cli.Quiet) output.WriteLine($"{result.RowCount} rows, {result.Pages.Count} page(s)");

        foreach (var o in cli.Outputs)
        {
            foreach (var file in Write(result, o, culture, cli.Dpi))
                if (!cli.Quiet) output.WriteLine("wrote " + file);
        }

        return cli.FailOnWarning && result.Warnings.Count > 0 ? ExitWarnings : ExitOk;
    }

    /// <summary>Why this report may not be run with these options, or null when it may.</summary>
    private static string? Refusal(CliArguments cli, ReportDefinition def)
    {
        if (def.UsesFileSources && !cli.AllowDataFiles)
            return "Refused: this report reads data files. Add --allow-data-files if you trust it.";
        if (def.UsesWebSources && !cli.AllowWebData)
            return "Refused: this report reads web addresses. Add --allow-web-data if you trust it.";
        // Data files and web addresses are allowed by their own options above; only a database query needs --trust.
        if (!TrustCheck.NeedsDatabaseTrust(def) || cli.Trust) return null;

        var lines = new List<string>
        {
            "Refused: this report carries its own database query, and running it would do exactly what is listed below.",
            ""
        };
        lines.AddRange(TrustCheck.Describe(def, MaxQueryChars));
        lines.Add("");
        lines.Add("Add --trust to run it. Only do that for reports from a source you trust.");
        return string.Join(Environment.NewLine, lines);
    }

    private static void ListParameters(ReportDefinition def, TextWriter output)
    {
        if (def.Parameters.Count == 0)
        {
            output.WriteLine("This report has no parameters.");
            return;
        }
        foreach (var p in def.Parameters)
        {
            var text = $"{p.Name} ({p.Type})";
            if (!string.IsNullOrWhiteSpace(p.Prompt)) text += " - " + p.Prompt;
            if (!string.IsNullOrWhiteSpace(p.DefaultValue)) text += $" [default: {p.DefaultValue}]";
            if (!string.IsNullOrWhiteSpace(p.Choices)) text += $" [choices: {p.Choices}]";
            output.WriteLine(text);
        }
    }

    /// <summary>Writes one output and returns the files created (images give one per page).</summary>
    private static IEnumerable<string> Write(ReportResult result, CliOutput o, CultureInfo culture, int dpi)
    {
        var full = Path.GetFullPath(o.Path);
        var folder = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);

        switch (o.Format)
        {
            case "pdf": PdfExporter.Export(result, full); break;
            case "xlsx": ExcelExporter.Export(result, full, culture); break;
            case "html": HtmlExporter.Export(result, full); break;
            case "csv": CsvExporter.Export(result, full); break;
            case "docx": WordExporter.Export(result, full); break;
            case "rtf": RtfExporter.Export(result, full); break;
            case "odt": OdfExporter.ExportText(result, full); break;
            case "ods": OdfExporter.ExportSpreadsheet(result, full, culture); break;
            case "pptx": PptxExporter.Export(result, full); break;
            case "xps": XpsExporter.Export(result, full, dpi); break;
            case "png": return ImageExporter.Export(result, full, ImageFormatKind.Png, dpi);
            case "jpeg": return ImageExporter.Export(result, full, ImageFormatKind.Jpeg, dpi);
            default: throw new InvalidOperationException($"The format '{o.Format}' is not supported.");
        }
        return new[] { full };
    }
}
