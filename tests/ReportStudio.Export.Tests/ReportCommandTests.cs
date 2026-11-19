using ReportStudio.Core.Model;
using ReportStudio.Core.Samples;
using ReportStudio.Export;
using Xunit;

namespace ReportStudio.Export.Tests;

/// <summary>The command-line runner (requirement 48), end to end on the sample report and a small CSV file.</summary>
public sealed class ReportCommandTests : IDisposable
{
    private sealed record Outcome(int Code, string Output, string Error);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rs-cli-" + Guid.NewGuid().ToString("N"));

    public ReportCommandTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* a leftover temp folder does no harm */ }
        catch (UnauthorizedAccessException) { }
    }

    private string P(string name) => Path.Combine(_dir, name);

    private string SaveSample()
    {
        var path = P("r.rsr");
        SampleReport.Create().Save(path);
        return path;
    }

    private static Outcome Run(params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var code = ReportCommand.Run(args, output, error);
        return new Outcome(code, output.ToString(), error.ToString());
    }

    [Fact]
    public void Help_and_version_succeed()
    {
        var help = Run("help");
        Assert.Equal(ReportCommand.ExitOk, help.Code);
        Assert.Contains("reportstudio run", help.Output);

        var version = Run("version");
        Assert.Equal(ReportCommand.ExitOk, version.Code);
        Assert.StartsWith("ReportStudio ", version.Output);
    }

    [Fact]
    public void A_wrong_command_line_gives_the_usage_exit_code_and_a_message()
    {
        var r = Run("run", SaveSample());   // no output asked for
        Assert.Equal(ReportCommand.ExitUsage, r.Code);
        Assert.Contains("--pdf", r.Error);
    }

    [Fact]
    public void A_run_writes_every_output_asked_for()
    {
        var report = SaveSample();
        var r = Run("run", report, "--pdf", P("out.pdf"), "--html", P("out.html"), "--csv", P("out.csv"));

        Assert.Equal(ReportCommand.ExitOk, r.Code);
        Assert.Contains("wrote", r.Output);
        var pdf = File.ReadAllBytes(P("out.pdf"));
        Assert.True(pdf.Length > 4);
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString(pdf, 0, 4));
        Assert.Contains("Acme Industries", File.ReadAllText(P("out.html")));   // the parameter's default
        Assert.True(new FileInfo(P("out.csv")).Length > 0);
    }

    [Fact]
    public void A_parameter_value_reaches_the_report()
    {
        var r = Run("run", SaveSample(), "--param", "company=Zed Corp", "--html", P("out.html"), "--quiet");

        Assert.Equal(ReportCommand.ExitOk, r.Code);
        Assert.Equal("", r.Output);   // --quiet
        Assert.Contains("Zed Corp", File.ReadAllText(P("out.html")));
    }

    [Fact]
    public void The_folder_of_an_output_is_created()
    {
        var r = Run("run", SaveSample(), "--pdf", P(Path.Combine("deep", "er", "out.pdf")), "--quiet");

        Assert.Equal(ReportCommand.ExitOk, r.Code);
        Assert.True(File.Exists(P(Path.Combine("deep", "er", "out.pdf"))));
    }

    [Fact]
    public void An_image_output_writes_a_file_per_page()
    {
        var r = Run("run", SaveSample(), "--png", P("page.png"), "--dpi", "50", "--quiet");

        Assert.Equal(ReportCommand.ExitOk, r.Code);
        Assert.NotEmpty(Directory.GetFiles(_dir, "page*.png"));
    }

    [Fact]
    public void An_unknown_parameter_is_a_usage_error_that_lists_the_real_ones()
    {
        var r = Run("run", SaveSample(), "--param", "Nope=1", "--pdf", P("out.pdf"));

        Assert.Equal(ReportCommand.ExitUsage, r.Code);
        Assert.Contains("'Nope'", r.Error);
        Assert.Contains("Company", r.Error);
        Assert.False(File.Exists(P("out.pdf")));
    }

    [Fact]
    public void The_params_command_lists_the_parameters()
    {
        var r = Run("params", SaveSample());

        Assert.Equal(ReportCommand.ExitOk, r.Code);
        Assert.Contains("Company", r.Output);
        Assert.Contains("Acme Industries", r.Output);
    }

    [Fact]
    public void An_unknown_culture_is_a_usage_error()
    {
        var r = Run("run", SaveSample(), "--culture", "xx-nowhere-zz", "--pdf", P("out.pdf"));

        Assert.Equal(ReportCommand.ExitUsage, r.Code);
        Assert.Contains("culture", r.Error);
    }

    [Fact]
    public void A_missing_report_file_fails()
    {
        var r = Run("run", P("missing.rsr"), "--pdf", P("out.pdf"));

        Assert.Equal(ReportCommand.ExitFailed, r.Code);
        Assert.Contains("not found", r.Error);
    }

    [Fact]
    public void A_damaged_report_file_fails_with_a_message()
    {
        File.WriteAllText(P("bad.rsr"), "{ this is not json");
        var r = Run("run", P("bad.rsr"), "--pdf", P("out.pdf"));

        Assert.Equal(ReportCommand.ExitFailed, r.Code);
        Assert.Contains("could not be read", r.Error);
    }

    [Fact]
    public void An_output_that_is_the_report_file_is_refused_and_the_report_is_untouched()
    {
        var report = SaveSample();
        var before = File.ReadAllText(report);
        var r = Run("run", report, "--pdf", report);

        Assert.Equal(ReportCommand.ExitUsage, r.Code);
        Assert.Equal(before, File.ReadAllText(report));
    }

    [Fact]
    public void A_report_with_its_own_query_needs_trust_and_shows_what_it_would_run()
    {
        var def = SampleReport.Create();
        def.DataSource.Provider = DbProvider.SqlServer;
        def.DataSource.ConnectionString = "Server=nowhere;Database=x;Integrated Security=true";
        def.DataSource.Query = "SELECT 1 AS Marker";
        def.Save(P("q.rsr"));

        var r = Run("run", P("q.rsr"), "--pdf", P("out.pdf"));

        Assert.Equal(ReportCommand.ExitRefused, r.Code);
        Assert.Contains("SELECT 1 AS Marker", r.Error);
        Assert.Contains("--trust", r.Error);
        Assert.False(File.Exists(P("out.pdf")));
    }

    [Fact]
    public void A_report_that_reads_a_data_file_needs_the_allow_option()
    {
        File.WriteAllText(P("data.csv"), "Name,Amount\nA,10\nB,20\n");
        var def = SampleReport.Create();
        def.DataSource.Provider = DbProvider.Csv;
        def.DataSource.Source = "data.csv";
        def.Save(P("f.rsr"));

        var refused = Run("run", P("f.rsr"), "--csv", P("out.csv"));
        Assert.Equal(ReportCommand.ExitRefused, refused.Code);
        Assert.Contains("--allow-data-files", refused.Error);
        Assert.False(File.Exists(P("out.csv")));

        var allowed = Run("run", P("f.rsr"), "--csv", P("out.csv"), "--allow-data-files", "--quiet");
        Assert.Equal(ReportCommand.ExitOk, allowed.Code);
        Assert.True(File.Exists(P("out.csv")));
    }

    [Fact]
    public void A_data_file_outside_the_data_root_is_not_read()
    {
        File.WriteAllText(P("data.csv"), "Name,Amount\nA,10\n");
        Directory.CreateDirectory(P("safe"));
        var def = SampleReport.Create();
        def.DataSource.Provider = DbProvider.Csv;
        def.DataSource.Source = "data.csv";
        def.Save(P("f.rsr"));

        var r = Run("run", P("f.rsr"), "--csv", P("out.csv"), "--allow-data-files", "--data-root", P("safe"));

        Assert.Equal(ReportCommand.ExitFailed, r.Code);
        Assert.False(File.Exists(P("out.csv")));
    }

    [Fact]
    public void A_report_that_needs_host_data_cannot_run_here()
    {
        var def = SampleReport.Create();
        def.DataSource.Provider = DbProvider.Host;
        def.Save(P("h.rsr"));

        var r = Run("run", P("h.rsr"), "--pdf", P("out.pdf"));

        Assert.Equal(ReportCommand.ExitFailed, r.Code);
        Assert.Contains("host application", r.Error);
    }
}
