using System.Globalization;

namespace ReportStudio.Core.Cli;

/// <summary>One output file asked for on the command line: the format name (pdf, xlsx, ...) and where to write it.</summary>
public sealed record CliOutput(string Format, string Path);

/// <summary>
/// The command line of the runner (requirement 48): <c>reportstudio run report.rsr --param Year=2026 --pdf out.pdf</c>.
/// Parsing is pure (no files are touched) so it can be tested alone. A bad command line never throws; it sets <see cref="Error"/>.
/// </summary>
public sealed class CliArguments
{
    public const string CommandRun = "run";
    public const string CommandParams = "params";
    public const string CommandVersion = "version";
    public const string CommandHelp = "help";

    public const int DefaultDpi = 150;
    public const int MinDpi = 30;
    public const int MaxDpi = 1200;

    /// <summary>The output formats, each given as <c>--format file</c>.</summary>
    public static readonly IReadOnlyList<string> Formats =
        new[] { "pdf", "xlsx", "html", "csv", "docx", "rtf", "odt", "ods", "pptx", "xps", "png", "jpeg" };

    private static readonly string[] Flags = { "allow-data-files", "allow-web-data", "trust", "quiet", "fail-on-warning" };

    public string Command { get; private set; } = CommandHelp;
    public string? ReportPath { get; private set; }
    public Dictionary<string, string> Parameters { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<CliOutput> Outputs { get; } = new();
    public string? Culture { get; private set; }
    public int Dpi { get; private set; } = DefaultDpi;
    public string? DataRoot { get; private set; }
    public bool AllowDataFiles { get; private set; }
    public bool AllowWebData { get; private set; }
    public bool Trust { get; private set; }
    public bool Quiet { get; private set; }
    public bool FailOnWarning { get; private set; }

    /// <summary>A sentence saying what is wrong with the command line, or null when it is fine.</summary>
    public string? Error { get; private set; }

    public static string HelpText =>
        string.Join(Environment.NewLine, new[]
        {
            "ReportStudio command-line runner",
            "",
            "Usage:",
            "  reportstudio run <report.rsr> [options]     run a report and write the outputs",
            "  reportstudio params <report.rsr>            list the report's parameters",
            "  reportstudio version                        show the version",
            "  reportstudio help                           show this text",
            "",
            "Outputs (at least one, each takes a file name):",
            "  --pdf --xlsx --html --csv --docx --rtf --odt --ods --pptx --xps --png --jpeg",
            "  (png and jpeg write one file per page: out_page1.png, out_page2.png ...; a one-page report keeps the name)",
            "",
            "Options:",
            "  --param Name=Value     set a report parameter (repeat for more)",
            "  --culture <code>       culture for numbers and dates, e.g. en-IN (default en-IN)",
            "  --dpi <n>              resolution for images and XPS, 30-1200 (default 150)",
            "  --data-root <folder>   the only folder data files may be read from",
            "  --allow-data-files     let the report read files (Json, Xml, Csv, Excel)",
            "  --allow-web-data       let the report read web sources (Rest, OData)",
            "  --trust                run a report that has its own database query",
            "  --quiet                print nothing except errors",
            "  --fail-on-warning      exit with code 4 when the run gave warnings",
            "",
            "Exit codes: 0 done, 1 failed, 2 wrong command line, 3 refused (needs --trust or an allow option), 4 warnings"
        });

    public static CliArguments Parse(IReadOnlyList<string> args)
    {
        var result = new CliArguments();
        if (args.Count == 0) return result;

        var first = args[0];
        switch (first.ToLowerInvariant())
        {
            case "help":
            case "--help":
            case "-h":
            case "-?":
            case "/?":
                result.Command = CommandHelp;
                return result;
            case "version":
            case "--version":
                result.Command = CommandVersion;
                return result;
            case CommandRun:
            case CommandParams:
                result.Command = first.ToLowerInvariant();
                break;
            default:
                result.Command = CommandHelp;
                result.Error = $"'{first}' is not a command. Use run, params, version or help.";
                return result;
        }

        var formatsSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 1; i < args.Count; i++)
        {
            var token = args[i];
            if (!token.StartsWith('-'))
            {
                if (result.ReportPath != null)
                {
                    result.Error = $"Only one report file can be given, but there is also '{token}'.";
                    return result;
                }
                result.ReportPath = token;
                continue;
            }

            if (!token.StartsWith("--", StringComparison.Ordinal) || token.Length == 2)
            {
                result.Error = $"'{token}' is not an option. Options start with two dashes, like --pdf.";
                return result;
            }

            var body = token[2..];
            string? inline = null;
            var eq = body.IndexOf('=');
            if (eq >= 0)
            {
                inline = body[(eq + 1)..];
                body = body[..eq];
            }
            var name = body.ToLowerInvariant();

            if (Array.IndexOf(Flags, name) >= 0)
            {
                if (inline != null)
                {
                    result.Error = $"--{name} does not take a value.";
                    return result;
                }
                switch (name)
                {
                    case "allow-data-files": result.AllowDataFiles = true; break;
                    case "allow-web-data": result.AllowWebData = true; break;
                    case "trust": result.Trust = true; break;
                    case "quiet": result.Quiet = true; break;
                    case "fail-on-warning": result.FailOnWarning = true; break;
                }
                continue;
            }

            var known = name is "param" or "culture" or "dpi" or "data-root" || Formats.Contains(name);
            if (!known)
            {
                result.Error = $"Unknown option --{name}. Use 'reportstudio help' to see the options.";
                return result;
            }

            string value;
            if (inline != null) value = inline;
            else if (i + 1 < args.Count && !(args[i + 1].StartsWith("--", StringComparison.Ordinal) && args[i + 1].Length > 2)) value = args[++i];
            else
            {
                result.Error = $"--{name} needs a value.";
                return result;
            }

            if (value.Length == 0)
            {
                result.Error = $"--{name} needs a value that is not empty.";
                return result;
            }

            if (name == "param")
            {
                var split = value.IndexOf('=');
                if (split <= 0)
                {
                    result.Error = $"--param needs Name=Value, but got '{value}'.";
                    return result;
                }
                var pName = value[..split].Trim();
                if (pName.Length == 0)
                {
                    result.Error = $"--param needs a name before the '=', but got '{value}'.";
                    return result;
                }
                if (!result.Parameters.TryAdd(pName, value[(split + 1)..]))
                {
                    result.Error = $"The parameter '{pName}' is given twice.";
                    return result;
                }
            }
            else if (name == "culture")
            {
                result.Culture = value.Trim();
            }
            else if (name == "dpi")
            {
                if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var dpi) || dpi < MinDpi || dpi > MaxDpi)
                {
                    result.Error = $"--dpi must be a whole number from {MinDpi} to {MaxDpi}, but got '{value}'.";
                    return result;
                }
                result.Dpi = dpi;
            }
            else if (name == "data-root")
            {
                result.DataRoot = value;
            }
            else
            {
                if (!formatsSeen.Add(name))
                {
                    result.Error = $"--{name} is given twice.";
                    return result;
                }
                if (!paths.Add(value))
                {
                    result.Error = $"Two outputs would be written to the same file '{value}'.";
                    return result;
                }
                result.Outputs.Add(new CliOutput(name, value));
            }
        }

        if (result.ReportPath == null)
        {
            result.Error = "Give the report file, for example: reportstudio " + result.Command + " report.rsr";
            return result;
        }

        if (result.Command == CommandParams)
        {
            if (result.Outputs.Count > 0 || result.Parameters.Count > 0 || result.Culture != null || result.DataRoot != null)
                result.Error = "'params' only takes the report file (and --allow-data-files, --allow-web-data, --trust, --quiet).";
        }
        else if (result.Outputs.Count == 0)
        {
            result.Error = "Say what to write, for example --pdf out.pdf. Use 'reportstudio help' to see the formats.";
        }

        return result;
    }
}
