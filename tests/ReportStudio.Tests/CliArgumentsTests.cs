using ReportStudio.Core.Cli;
using Xunit;

namespace ReportStudio.Tests;

/// <summary>The command line of the runner (requirement 48). Parsing touches no files, so these run anywhere.</summary>
public class CliArgumentsTests
{
    private static CliArguments Parse(params string[] args) => CliArguments.Parse(args);

    [Fact]
    public void No_arguments_show_the_help_without_an_error()
    {
        var cli = Parse();
        Assert.Equal(CliArguments.CommandHelp, cli.Command);
        Assert.Null(cli.Error);
    }

    [Theory]
    [InlineData("help")]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("-?")]
    [InlineData("/?")]
    [InlineData("HELP")]
    public void Help_in_all_its_spellings(string word)
    {
        var cli = Parse(word);
        Assert.Equal(CliArguments.CommandHelp, cli.Command);
        Assert.Null(cli.Error);
    }

    [Theory]
    [InlineData("version")]
    [InlineData("--version")]
    public void Version_is_recognised(string word)
    {
        var cli = Parse(word);
        Assert.Equal(CliArguments.CommandVersion, cli.Command);
        Assert.Null(cli.Error);
    }

    [Fact]
    public void An_unknown_command_is_an_error_that_names_it()
    {
        var cli = Parse("fly", "r.rsr");
        Assert.NotNull(cli.Error);
        Assert.Contains("'fly'", cli.Error);
    }

    [Fact]
    public void A_full_run_command_is_read()
    {
        var cli = Parse("run", "r.rsr", "--param", "Year=2026", "--pdf", "out.pdf", "--xlsx", "out.xlsx");
        Assert.Null(cli.Error);
        Assert.Equal(CliArguments.CommandRun, cli.Command);
        Assert.Equal("r.rsr", cli.ReportPath);
        Assert.Equal("2026", cli.Parameters["Year"]);
        Assert.Equal("2026", cli.Parameters["year"]);   // parameter names ignore case
        Assert.Equal(2, cli.Outputs.Count);
        Assert.Equal("pdf", cli.Outputs[0].Format);
        Assert.Equal("out.pdf", cli.Outputs[0].Path);
        Assert.Equal("xlsx", cli.Outputs[1].Format);
        Assert.Equal(CliArguments.DefaultDpi, cli.Dpi);
    }

    [Fact]
    public void The_report_may_come_after_the_options()
    {
        var cli = Parse("run", "--pdf", "out.pdf", "r.rsr");
        Assert.Null(cli.Error);
        Assert.Equal("r.rsr", cli.ReportPath);
    }

    [Fact]
    public void Options_may_use_equals_instead_of_a_separate_value()
    {
        var cli = Parse("run", "r.rsr", "--pdf=out.pdf", "--param=Year=2026", "--dpi=300");
        Assert.Null(cli.Error);
        Assert.Equal("out.pdf", cli.Outputs.Single().Path);
        Assert.Equal("2026", cli.Parameters["Year"]);
        Assert.Equal(300, cli.Dpi);
    }

    [Fact]
    public void A_parameter_value_keeps_every_equals_sign_after_the_first()
    {
        var cli = Parse("run", "r.rsr", "--pdf", "o.pdf", "--param", "Filter=a=b");
        Assert.Equal("a=b", cli.Parameters["Filter"]);
    }

    [Fact]
    public void A_parameter_value_may_be_empty()
    {
        var cli = Parse("run", "r.rsr", "--pdf", "o.pdf", "--param", "Note=");
        Assert.Null(cli.Error);
        Assert.Equal("", cli.Parameters["Note"]);
    }

    [Fact]
    public void Flags_are_read()
    {
        var cli = Parse("run", "r.rsr", "--pdf", "o.pdf", "--trust", "--quiet", "--fail-on-warning",
            "--allow-data-files", "--allow-web-data", "--data-root", "C:\\data", "--culture", "en-US");
        Assert.Null(cli.Error);
        Assert.True(cli.Trust);
        Assert.True(cli.Quiet);
        Assert.True(cli.FailOnWarning);
        Assert.True(cli.AllowDataFiles);
        Assert.True(cli.AllowWebData);
        Assert.Equal("C:\\data", cli.DataRoot);
        Assert.Equal("en-US", cli.Culture);
    }

    [Fact]
    public void Flags_start_off()
    {
        var cli = Parse("run", "r.rsr", "--pdf", "o.pdf");
        Assert.False(cli.Trust);
        Assert.False(cli.Quiet);
        Assert.False(cli.FailOnWarning);
        Assert.False(cli.AllowDataFiles);
        Assert.False(cli.AllowWebData);
    }

    [Fact]
    public void Every_format_is_accepted()
    {
        foreach (var format in CliArguments.Formats)
        {
            var cli = Parse("run", "r.rsr", "--" + format, "out." + format);
            Assert.Null(cli.Error);
            Assert.Equal(format, cli.Outputs.Single().Format);
        }
    }

    [Fact]
    public void A_run_without_an_output_is_an_error()
    {
        var cli = Parse("run", "r.rsr");
        Assert.NotNull(cli.Error);
        Assert.Contains("--pdf", cli.Error);
    }

    [Fact]
    public void A_run_without_a_report_is_an_error()
    {
        var cli = Parse("run", "--pdf", "o.pdf");
        Assert.NotNull(cli.Error);
        Assert.Contains("report file", cli.Error);
    }

    [Fact]
    public void Two_report_files_are_an_error()
    {
        var cli = Parse("run", "a.rsr", "b.rsr", "--pdf", "o.pdf");
        Assert.NotNull(cli.Error);
        Assert.Contains("b.rsr", cli.Error);
    }

    [Fact]
    public void A_format_given_twice_is_an_error()
    {
        var cli = Parse("run", "r.rsr", "--pdf", "a.pdf", "--pdf", "b.pdf");
        Assert.NotNull(cli.Error);
        Assert.Contains("twice", cli.Error);
    }

    [Fact]
    public void Two_outputs_to_the_same_file_are_an_error_whatever_the_case()
    {
        var cli = Parse("run", "r.rsr", "--pdf", "Out.dat", "--html", "out.DAT");
        Assert.NotNull(cli.Error);
        Assert.Contains("same file", cli.Error);
    }

    [Fact]
    public void A_parameter_given_twice_is_an_error()
    {
        var cli = Parse("run", "r.rsr", "--pdf", "o.pdf", "--param", "Year=1", "--param", "year=2");
        Assert.NotNull(cli.Error);
        Assert.Contains("twice", cli.Error);
    }

    [Theory]
    [InlineData("Year")]
    [InlineData("=2026")]
    public void A_parameter_without_a_name_or_equals_is_an_error(string text)
    {
        var cli = Parse("run", "r.rsr", "--pdf", "o.pdf", "--param", text);
        Assert.NotNull(cli.Error);
        Assert.Contains("Name=Value", cli.Error);
    }

    [Theory]
    [InlineData("10")]
    [InlineData("5000")]
    [InlineData("abc")]
    [InlineData("-5")]
    [InlineData("1.5")]
    public void A_dpi_outside_the_range_or_not_a_number_is_an_error(string dpi)
    {
        var cli = Parse("run", "r.rsr", "--pdf", "o.pdf", "--dpi", dpi);
        Assert.NotNull(cli.Error);
        Assert.Contains("--dpi", cli.Error);
    }

    [Theory]
    [InlineData("30", 30)]
    [InlineData("1200", 1200)]
    public void The_ends_of_the_dpi_range_are_allowed(string text, int expected)
    {
        var cli = Parse("run", "r.rsr", "--pdf", "o.pdf", "--dpi", text);
        Assert.Null(cli.Error);
        Assert.Equal(expected, cli.Dpi);
    }

    [Fact]
    public void An_unknown_option_is_an_error_that_names_it()
    {
        var cli = Parse("run", "r.rsr", "--pdf", "o.pdf", "--colour", "red");
        Assert.NotNull(cli.Error);
        Assert.Contains("--colour", cli.Error);
    }

    [Theory]
    [InlineData("-p")]
    [InlineData("--")]
    public void A_single_dash_or_a_bare_double_dash_is_an_error(string token)
    {
        var cli = Parse("run", "r.rsr", token);
        Assert.NotNull(cli.Error);
    }

    [Fact]
    public void An_option_without_its_value_is_an_error()
    {
        var cli = Parse("run", "r.rsr", "--pdf");
        Assert.NotNull(cli.Error);
        Assert.Contains("needs a value", cli.Error);
    }

    [Fact]
    public void An_option_followed_by_another_option_has_no_value()
    {
        var cli = Parse("run", "r.rsr", "--pdf", "--quiet");
        Assert.NotNull(cli.Error);
        Assert.Contains("needs a value", cli.Error);
    }

    [Fact]
    public void A_flag_with_a_value_is_an_error()
    {
        var cli = Parse("run", "r.rsr", "--pdf", "o.pdf", "--trust=yes");
        Assert.NotNull(cli.Error);
        Assert.Contains("does not take a value", cli.Error);
    }

    [Fact]
    public void The_params_command_only_needs_the_report()
    {
        var cli = Parse("params", "r.rsr");
        Assert.Null(cli.Error);
        Assert.Equal(CliArguments.CommandParams, cli.Command);
        Assert.Equal("r.rsr", cli.ReportPath);
    }

    [Fact]
    public void The_params_command_refuses_outputs()
    {
        var cli = Parse("params", "r.rsr", "--pdf", "o.pdf");
        Assert.NotNull(cli.Error);
    }

    [Fact]
    public void The_help_text_names_every_format_and_exit_code()
    {
        var help = CliArguments.HelpText;
        foreach (var format in CliArguments.Formats) Assert.Contains("--" + format, help);
        Assert.Contains("--trust", help);
        Assert.Contains("Exit codes", help);
    }
}
