using System.Globalization;
using ReportStudio.Core.Data;
using ReportStudio.Core.Expressions;
using ReportStudio.Core.Layout;
using ReportStudio.Core.Model;
using Xunit;

namespace ReportStudio.Tests;

/// <summary>Parameter upgrades: lists, pick-several, expression defaults, lists from data (requirement 31).</summary>
public class ParameterTests
{
    // ---- fixed lists ----

    [Fact]
    public void A_list_can_be_plain_values_or_value_equals_shown_text()
    {
        var plain = ParameterChoices.Parse("Active; Inactive ;;Pending");
        var named = ParameterChoices.Parse("A=Active;I=Inactive");

        Assert.Equal(new[] { "Active", "Inactive", "Pending" }, plain.Select(c => c.Value));
        Assert.Equal(plain.Select(c => c.Value), plain.Select(c => c.Label));
        Assert.Equal(new[] { ("A", "Active"), ("I", "Inactive") }, named.Select(c => (c.Value, c.Label)));
    }

    [Fact]
    public void A_repeated_value_is_listed_once_and_an_empty_list_is_empty()
    {
        Assert.Equal(new[] { "A", "B" }, ParameterChoices.Parse("A;a;B").Select(c => c.Value));
        Assert.Empty(ParameterChoices.Parse(""));
        Assert.Empty(ParameterChoices.Parse(null));
    }

    [Fact]
    public void Depends_on_names_are_read_with_or_without_the_at_sign()
    {
        Assert.Equal(new[] { "Country", "Year" }, ParameterChoices.ParseNames("@Country; Year, "));
    }

    // ---- several values ----

    [Fact]
    public void Several_values_are_stored_with_a_bar_between_and_trimmed()
    {
        Assert.Equal("Sales|Stores", ValueList.Join(new[] { " Sales ", "", "Stores" }));
        Assert.Equal(new[] { "Sales", "Stores" }, ValueList.Split("Sales| Stores |"));
        Assert.Empty(ValueList.Split(""));
    }

    [Fact]
    public void A_pick_several_parameter_gives_a_list_of_typed_values()
    {
        var text = new ReportParameter { Name = "Depts", MultiValue = true };
        var numbers = new ReportParameter { Name = "Ids", MultiValue = true, Type = ParameterType.Number };

        var a = Assert.IsType<ValueList>(text.Parse("Sales|HR"));
        var b = Assert.IsType<ValueList>(numbers.Parse("1|2.5"));

        Assert.Equal(new object?[] { "Sales", "HR" }, a.ToArray());
        Assert.Equal(new object?[] { 1m, 2.5m }, b.ToArray());
        Assert.Equal("Sales, HR", a.ToString());
        Assert.Empty(Assert.IsType<ValueList>(text.Parse("")));
    }

    [Fact]
    public void A_single_value_parameter_is_unchanged()
    {
        Assert.Equal("x", new ReportParameter { Name = "P" }.Parse("x"));
        Assert.Null(new ReportParameter { Name = "P" }.Parse(""));
        Assert.Equal(5m, new ReportParameter { Name = "P", Type = ParameterType.Number }.Parse("5"));
    }

    [Fact]
    public void In_is_true_when_the_value_is_one_of_the_options_or_one_of_a_picked_list()
    {
        var ctx = new EvalContext
        {
            Row = new Dictionary<string, object?> { ["Dept"] = "sales", ["N"] = 2m },
            Parameters = new Dictionary<string, object?>
            {
                ["Depts"] = new ValueList(new object?[] { "Sales", "HR" }),
                ["Ids"] = new ValueList(new object?[] { 1m, 2m }),
                ["None"] = new ValueList(Array.Empty<object?>())
            }
        };

        Assert.True(Expr.IsTrue(Expr.Evaluate("In(Dept, @Depts)", ctx)));       // case does not matter
        Assert.True(Expr.IsTrue(Expr.Evaluate("In(N, @Ids)", ctx)));
        Assert.True(Expr.IsTrue(Expr.Evaluate("In(Dept, 'x', 'Sales')", ctx)));
        Assert.False(Expr.IsTrue(Expr.Evaluate("In(Dept, 'x', 'y')", ctx)));
        Assert.False(Expr.IsTrue(Expr.Evaluate("In(Dept, @None)", ctx)));
    }

    [Fact]
    public void A_picked_list_can_filter_a_report_and_print_as_a_comma_list()
    {
        var def = new ReportDefinition { Name = "Multi", Filter = "In(Dept, @Depts)" };
        def.DataSource.Provider = DbProvider.Host;
        def.Parameters.Add(new ReportParameter { Name = "Depts", MultiValue = true });
        def.ReportHeader.Height = 20;
        def.ReportHeader.Items.Add(new ReportItem { Kind = ItemKind.Text, Text = "Picked: {@Depts}", Width = 200, Height = 18 });
        def.PageHeader.Height = 0; def.PageFooter.Height = 0; def.ReportFooter.Height = 0;
        var rows = new[] { "Sales", "HR", "Stores", "Sales" }
            .Select(d => new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["Dept"] = d }).ToList();

        var result = ReportEngine.Run(def, new Dictionary<string, string?> { ["Depts"] = "Sales|HR" }, rows,
            new ReportOptions { Culture = CultureInfo.InvariantCulture });

        Assert.Equal(3, result.RowCount);
        Assert.Contains(result.Pages[0].Items, i => i.Text == "Picked: Sales, HR");
    }

    // ---- defaults that are expressions ----

    [Fact]
    public void A_plain_default_is_used_as_it_is()
    {
        Assert.Equal("Acme", new ReportParameter { DefaultValue = "Acme" }.ResolveDefault());
        Assert.Equal("", new ReportParameter().ResolveDefault());
    }

    [Fact]
    public void A_default_starting_with_equals_is_worked_out_for_the_day()
    {
        var now = new DateTime(2026, 11, 4, 17, 30, 0);

        Assert.Equal("2026-11-01", new ReportParameter { DefaultValue = "=MonthStart(Today())" }.ResolveDefault(now));
        Assert.Equal("2026-27", new ReportParameter { DefaultValue = "=FinancialYear(Today())" }.ResolveDefault(now));
        Assert.Equal("2026-01-01", new ReportParameter { DefaultValue = "=YearStart(Today())" }.ResolveDefault(now));
        Assert.Equal("2", new ReportParameter { DefaultValue = "=1+1" }.ResolveDefault(now));
    }

    [Fact]
    public void A_default_that_cannot_be_worked_out_is_empty()
    {
        Assert.Equal("", new ReportParameter { DefaultValue = "=NoSuchFunction()" }.ResolveDefault());
        Assert.Equal("", new ReportParameter { DefaultValue = "=(" }.ResolveDefault());
    }

    [Fact]
    public void A_run_without_a_value_uses_the_worked_out_default()
    {
        var def = new ReportDefinition();
        def.Parameters.Add(new ReportParameter { Name = "From", Type = ParameterType.Date, DefaultValue = "=MonthStart(Today())" });

        var values = ReportEngine.ResolveParameters(def, null);

        var from = Assert.IsType<DateTime>(values["From"]);
        Assert.Equal(1, from.Day);
    }

    // ---- lists from the database ----

    [Fact]
    public void Rows_become_value_and_label_skipping_empty_and_repeated_values()
    {
        var rows = new List<Dictionary<string, object?>>
        {
            new() { ["Code"] = "IN", ["Name"] = "India" },
            new() { ["Code"] = "IN", ["Name"] = "Again" },
            new() { ["Code"] = DBNull.Value, ["Name"] = "None" },
            new() { ["Code"] = "DE", ["Name"] = "" },
            new() { ["Code"] = new DateTime(2026, 4, 1), ["Name"] = "April" }
        };

        var choices = ParameterChoiceLoader.FromRows(rows);

        Assert.Equal(new[] { ("IN", "India"), ("DE", "DE"), ("2026-04-01", "April") }, choices.Select(c => (c.Value, c.Label)));
    }

    [Fact]
    public void One_column_gives_the_value_as_its_own_label()
    {
        var choices = ParameterChoiceLoader.FromRows(new[] { new Dictionary<string, object?> { ["Dept"] = "Sales" } });

        Assert.Equal(("Sales", "Sales"), (choices[0].Value, choices[0].Label));
    }

    [Fact]
    public void A_report_without_a_database_uses_the_fixed_list()
    {
        var def = new ReportDefinition();
        def.DataSource.Provider = DbProvider.Sample;
        var p = new ReportParameter { Name = "Status", Choices = "A=Active;I=Inactive", ChoicesQuery = "SELECT 1" };

        var choices = ParameterChoiceLoader.Load(def, p, new Dictionary<string, object?>());

        Assert.Equal(new[] { "A", "I" }, choices.Select(c => c.Value));
    }

    [Fact]
    public void A_list_query_counts_as_a_query_from_the_file_but_only_on_a_database()
    {
        var def = new ReportDefinition();
        def.DataSource.Provider = DbProvider.SqlServer;
        def.Parameters.Add(new ReportParameter { Name = "Dept", ChoicesQuery = "SELECT Name FROM Dept" });
        Assert.True(def.HasOwnQuery);

        def.Parameters[0].ChoicesQuery = "";
        Assert.False(def.HasOwnQuery);

        def.Parameters[0].ChoicesQuery = "SELECT Name FROM Dept";
        def.DataSource.Provider = DbProvider.Sample;
        Assert.False(def.HasOwnQuery);
        def.DataSource.Provider = DbProvider.Host;
        Assert.False(def.HasOwnQuery);
    }

    [Fact]
    public void The_new_settings_survive_saving_and_copying()
    {
        var def = new ReportDefinition();
        def.Parameters.Add(new ReportParameter
        {
            Name = "City", Choices = "A;B", ChoicesQuery = "SELECT City FROM C WHERE Country = @Country",
            DependsOn = "Country", MultiValue = true, DefaultValue = "=FinancialYear(Today())"
        });

        foreach (var copy in new[] { ReportDefinition.FromJson(def.ToJson()), def.Clone() })
        {
            var p = copy.Parameters[0];
            Assert.Equal("A;B", p.Choices);
            Assert.Equal("SELECT City FROM C WHERE Country = @Country", p.ChoicesQuery);
            Assert.Equal("Country", p.DependsOn);
            Assert.True(p.MultiValue);
            Assert.Equal("=FinancialYear(Today())", p.DefaultValue);
        }
    }
}
