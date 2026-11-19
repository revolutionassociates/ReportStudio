using ReportStudio.Core.Model;
using ReportStudio.Core.Wizard;
using Xunit;

namespace ReportStudio.Tests;

/// <summary>The person's own templates (requirement 45c).</summary>
public sealed class UserTemplatesTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "rs-templates-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    private static ReportDefinition Report(string name = "Sales list")
    {
        var def = ReportWizard.Build(WizardOptions.ForSampleData());
        def.Name = name;
        return def;
    }

    [Fact]
    public void A_saved_template_is_listed_and_makes_the_same_report()
    {
        var report = Report();

        UserTemplates.Save(_folder, report, "My sales");
        var template = Assert.Single(UserTemplates.List(_folder));

        Assert.Equal("My sales", template.Name);
        Assert.Equal(UserTemplates.Category, template.Category);
        Assert.Contains("Your own template", template.Description);
        Assert.Equal(report.ToJson(), template.Create().ToJson());
    }

    [Fact]
    public void Every_creation_gives_a_fresh_report()
    {
        UserTemplates.Save(_folder, Report(), "Fresh");
        var template = Assert.Single(UserTemplates.List(_folder));

        Assert.NotSame(template.Create(), template.Create());
    }

    [Fact]
    public void A_folder_that_does_not_exist_has_no_templates()
    {
        Assert.Empty(UserTemplates.List(Path.Combine(_folder, "nothing")));
        Assert.Empty(UserTemplates.List(""));
        Assert.Empty(UserTemplates.List(null));
    }

    [Fact]
    public void Saving_creates_the_folder()
    {
        Assert.False(Directory.Exists(_folder));

        UserTemplates.Save(_folder, Report(), "First");

        Assert.True(Directory.Exists(_folder));
    }

    [Fact]
    public void Saving_again_under_the_same_name_replaces_the_template()
    {
        UserTemplates.Save(_folder, Report("Old"), "Same");
        UserTemplates.Save(_folder, Report("New"), "Same");

        var template = Assert.Single(UserTemplates.List(_folder));
        Assert.Equal("New", template.Create().Name);
    }

    [Fact]
    public void Templates_are_listed_by_name_and_only_rsr_files_count()
    {
        UserTemplates.Save(_folder, Report(), "b");
        UserTemplates.Save(_folder, Report(), "A");
        UserTemplates.Save(_folder, Report(), "c");
        File.WriteAllText(Path.Combine(_folder, "notes.txt"), "not a template");

        Assert.Equal(new[] { "A", "b", "c" }, UserTemplates.List(_folder).Select(t => t.Name));
    }

    [Fact]
    public void Your_templates_are_told_apart_from_the_built_in_ones()
    {
        UserTemplates.Save(_folder, Report(), "Mine");
        var mine = Assert.Single(UserTemplates.List(_folder));

        Assert.True(UserTemplates.IsUser(mine));
        Assert.All(ReportTemplates.All, t => Assert.False(UserTemplates.IsUser(t)));
        Assert.DoesNotContain(ReportTemplates.All, t => t.Id == mine.Id);
    }

    [Fact]
    public void A_template_can_be_deleted_once()
    {
        UserTemplates.Save(_folder, Report(), "Gone");
        Assert.True(UserTemplates.Exists(_folder, "Gone"));

        Assert.True(UserTemplates.Delete(_folder, "Gone"));

        Assert.False(UserTemplates.Exists(_folder, "Gone"));
        Assert.False(UserTemplates.Delete(_folder, "Gone"));
        Assert.Empty(UserTemplates.List(_folder));
    }

    [Fact]
    public void Characters_a_file_name_cannot_hold_never_leave_the_folder()
    {
        var path = UserTemplates.Save(_folder, Report(), @"..\Q1/Q2: sales?");

        Assert.Equal(_folder, Path.GetDirectoryName(path));
        Assert.True(File.Exists(path));
        var file = Path.GetFileName(path);
        Assert.DoesNotContain('/', file);
        Assert.DoesNotContain(':', file);
        Assert.DoesNotContain('?', file);
        Assert.Single(UserTemplates.List(_folder));
    }

    [Theory]
    [InlineData("Sales", "Sales")]
    [InlineData("a/b", "a_b")]
    [InlineData("  padded  ", "padded")]
    [InlineData("v1.2", "v1.2")]
    [InlineData("end. ", "end")]
    [InlineData("Console", "Console")]
    [InlineData("CON", "CON_")]
    [InlineData("com1", "com1_")]
    public void A_name_becomes_a_safe_file_name(string name, string expected)
    {
        Assert.Equal(expected, UserTemplates.FileNameFor(name));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("???")]
    [InlineData("...")]
    [InlineData(null)]
    public void A_name_with_nothing_usable_is_refused(string? name)
    {
        Assert.Throws<ArgumentException>(() => UserTemplates.FileNameFor(name));
        Assert.Throws<ArgumentException>(() => UserTemplates.Save(_folder, Report(), name!));
    }

    [Fact]
    public void A_long_name_is_cut_to_80_characters()
    {
        var file = UserTemplates.FileNameFor(new string('x', 200));

        Assert.Equal(80, file.Length);
    }

    [Fact]
    public void A_file_is_only_read_when_the_template_is_created()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "Broken.rsr"), "this is not json");

        var template = Assert.Single(UserTemplates.List(_folder));

        Assert.Equal("Broken", template.Name);
        Assert.ThrowsAny<Exception>(() => template.Create());
    }
}
