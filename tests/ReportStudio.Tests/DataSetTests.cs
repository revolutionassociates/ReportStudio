using System.Globalization;
using ReportStudio.Core.Data;
using ReportStudio.Core.Layout;
using ReportStudio.Core.Model;
using Xunit;

namespace ReportStudio.Tests;

/// <summary>Several data sets in one report (requirement 39): the catalog, the Lookup functions and joins.</summary>
public class DataSetTests
{
    private static Dictionary<string, object?> Row(params (string Field, object? Value)[] values)
    {
        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (field, value) in values) row[field] = value;
        return row;
    }

    private static List<Dictionary<string, object?>> Employees() => new()
    {
        Row(("EmpCode", "E1"), ("Name", "Asha"), ("DeptCode", 10)),
        Row(("EmpCode", "E2"), ("Name", "Ravi"), ("DeptCode", 20)),
        Row(("EmpCode", "E3"), ("Name", "Meena"), ("DeptCode", 30)),
    };

    private static Dictionary<string, List<Dictionary<string, object?>>> Sets() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["Departments"] = new()
        {
            Row(("Code", 10), ("Name", "Sales")),
            Row(("Code", 20), ("Name", "Operations")),
        },
        ["Leaves"] = new()
        {
            Row(("EmpCode", "E1"), ("Days", 2), ("Type", "Sick")),
            Row(("EmpCode", "e1 "), ("Days", 3), ("Type", "Casual")),
            Row(("EmpCode", "E2"), ("Days", 1), ("Type", "Sick")),
        },
    };

    private static ReportDefinition Definition(params string[] templates)
    {
        var def = new ReportDefinition { Name = "Sets" };
        def.DataSource.Provider = DbProvider.Host;
        def.Page.Height = 3000;
        def.ReportHeader.Height = 0; def.PageHeader.Height = 0; def.ReportFooter.Height = 0; def.PageFooter.Height = 0;
        def.Detail.Height = 20 * Math.Max(1, templates.Length);
        for (int i = 0; i < templates.Length; i++)
            def.Detail.Items.Add(new ReportItem { Kind = ItemKind.Text, Text = templates[i], Y = 20 * i, Width = 400, Height = 18 });
        return def;
    }

    private static ReportResult Run(ReportDefinition def, List<Dictionary<string, object?>>? rows = null) =>
        ReportEngine.Run(def, null, (rows ?? Employees()).Select(r => (IReadOnlyDictionary<string, object?>)r),
            new ReportOptions { Culture = CultureInfo.InvariantCulture, DataSets = Sets() });

    /// <summary>The text of every detail element, row by row and top to bottom inside a row.</summary>
    private static string[] Texts(ReportResult result) =>
        result.Pages.SelectMany(p => p.Items).Where(i => i.Band == BandKind.Detail).OrderBy(i => i.Y).ThenBy(i => i.X).Select(i => i.Text).ToArray();

    // ---- keys ----

    [Fact]
    public void Numbers_compare_by_value_and_text_without_case_or_edge_spaces()
    {
        Assert.Equal(DataSetCatalog.KeyOf(5), DataSetCatalog.KeyOf(5.0));
        Assert.Equal(DataSetCatalog.KeyOf(5), DataSetCatalog.KeyOf(5m));
        Assert.Equal(DataSetCatalog.KeyOf(5), DataSetCatalog.KeyOf(" 5 "));
        Assert.Equal(DataSetCatalog.KeyOf("e1"), DataSetCatalog.KeyOf(" E1 "));
        Assert.NotEqual(DataSetCatalog.KeyOf("E1"), DataSetCatalog.KeyOf("E2"));
    }

    [Fact]
    public void An_empty_value_has_no_key_and_matches_nothing()
    {
        Assert.Null(DataSetCatalog.KeyOf(null));
        Assert.Null(DataSetCatalog.KeyOf(DBNull.Value));
        Assert.Null(DataSetCatalog.KeyOf("   "));

        var catalog = new DataSetCatalog(Sets());
        Assert.Empty(catalog.Matches("Leaves", "EmpCode", null));
    }

    [Fact]
    public void The_catalog_finds_rows_by_key_in_the_sets_own_order()
    {
        var catalog = new DataSetCatalog(Sets());

        var found = catalog.Matches("leaves", "empcode", "E1");

        Assert.Equal(new object?[] { "Sick", "Casual" }, found.Select(r => r["Type"]).ToArray());
        Assert.True(catalog.Contains("DEPARTMENTS"));
        Assert.False(catalog.Contains("Nope"));
        Assert.Empty(catalog.Matches("Nope", "x", 1));
    }

    // ---- lookups ----

    [Fact]
    public void Lookup_gives_the_field_of_the_matching_row_even_when_the_key_types_differ()
    {
        var def = Definition("{Lookup('Departments', 'Code', DeptCode, 'Name')}");

        Assert.Equal(new[] { "Sales", "Operations", "" }, Texts(Run(def)));
    }

    [Fact]
    public void Lookup_matches_text_keys_without_regard_to_case()
    {
        var def = Definition("{Lookup('Leaves', 'EmpCode', Upper(EmpCode), 'Type')}");

        Assert.Equal(new[] { "Sick", "Sick", "" }, Texts(Run(def)));
    }

    [Fact]
    public void LookupSum_adds_over_all_matches_and_LookupCount_counts_them()
    {
        var def = Definition("{LookupSum('Leaves', 'EmpCode', EmpCode, 'Days')}", "{LookupCount('Leaves', 'EmpCode', EmpCode)}");

        Assert.Equal(new[] { "5", "2", "1", "1", "0", "0" }, Texts(Run(def)));
    }

    [Fact]
    public void LookupList_joins_the_matches_with_a_separator()
    {
        var def = Definition("{LookupList('Leaves', 'EmpCode', EmpCode, 'Type')}", "{LookupList('Leaves', 'EmpCode', EmpCode, 'Type', '; ')}");

        Assert.Equal(new[] { "Sick, Casual", "Sick; Casual", "Sick", "Sick", "", "" }, Texts(Run(def)));
    }

    [Fact]
    public void A_lookup_of_a_data_set_that_does_not_exist_is_an_error_with_a_warning()
    {
        var def = Definition("{Lookup('Nope', 'Code', DeptCode, 'Name')}");

        var result = Run(def);

        Assert.All(Texts(result), t => Assert.Equal("#ERR", t));
        Assert.Contains(result.Warnings, w => w.Contains("no data set 'Nope'"));
    }

    [Fact]
    public void A_lookup_with_too_few_arguments_is_an_error_with_a_warning()
    {
        var def = Definition("{Lookup('Departments', 'Code')}");

        var result = Run(def);

        Assert.All(Texts(result), t => Assert.Equal("#ERR", t));
        Assert.Contains(result.Warnings, w => w.Contains("Lookup needs a data set"));
    }

    [Fact]
    public void A_lookup_can_be_used_in_the_report_filter()
    {
        var def = Definition("{EmpCode}");
        def.Filter = "LookupCount('Leaves', 'EmpCode', EmpCode) > 0";

        Assert.Equal(new[] { "E1", "E2" }, Texts(Run(def)));
    }

    [Fact]
    public void A_data_set_the_host_did_not_supply_leaves_a_lookup_in_error()
    {
        var def = Definition("{Lookup('Departments', 'Code', DeptCode, 'Name')}");

        var result = ReportEngine.Run(def, null, Employees().Select(r => (IReadOnlyDictionary<string, object?>)r),
            new ReportOptions { Culture = CultureInfo.InvariantCulture });

        Assert.All(Texts(result), t => Assert.Equal("#ERR", t));
    }

    // ---- joins ----

    [Fact]
    public void A_left_join_adds_the_fields_and_keeps_every_row()
    {
        var def = Definition("{EmpCode} {Dept_Name}");
        def.Joins.Add(new JoinDefinition { DataSet = "Departments", LeftField = "DeptCode", RightField = "Code", Prefix = "Dept_" });

        Assert.Equal(new[] { "E1 Sales", "E2 Operations", "E3 " }, Texts(Run(def)));
    }

    [Fact]
    public void An_inner_join_drops_the_rows_without_a_match()
    {
        var def = Definition("{EmpCode} {Dept_Name}");
        def.Joins.Add(new JoinDefinition { DataSet = "Departments", LeftField = "DeptCode", RightField = "Code", Prefix = "Dept_", Kind = JoinKind.Inner });

        Assert.Equal(new[] { "E1 Sales", "E2 Operations" }, Texts(Run(def)));
    }

    [Fact]
    public void A_row_with_several_matches_becomes_several_rows()
    {
        var def = Definition("{EmpCode} {Type}");
        def.Joins.Add(new JoinDefinition { DataSet = "Leaves", LeftField = "EmpCode" });

        Assert.Equal(new[] { "E1 Sick", "E1 Casual", "E2 Sick", "E3 " }, Texts(Run(def)));
    }

    [Fact]
    public void Without_a_prefix_a_field_the_main_row_has_keeps_its_own_value()
    {
        var def = Definition("{Name}|{Code}");
        def.Joins.Add(new JoinDefinition { DataSet = "Departments", LeftField = "DeptCode", RightField = "Code" });

        Assert.Equal(new[] { "Asha|10", "Ravi|20", "Meena|" }, Texts(Run(def)));
    }

    [Fact]
    public void The_right_field_defaults_to_the_left_field()
    {
        var def = Definition("{EmpCode} {Days}");
        def.Joins.Add(new JoinDefinition { DataSet = "Leaves", LeftField = "EmpCode", Kind = JoinKind.Inner });

        Assert.Equal(new[] { "E1 2", "E1 3", "E2 1" }, Texts(Run(def)));
    }

    [Fact]
    public void Joins_are_applied_in_order_so_a_later_one_can_use_an_earlier_ones_field()
    {
        var def = Definition("{EmpCode} {Dept_Name}");
        def.Joins.Add(new JoinDefinition { DataSet = "Leaves", LeftField = "EmpCode", Kind = JoinKind.Inner, Prefix = "L_" });
        def.Joins.Add(new JoinDefinition { DataSet = "Departments", LeftField = "DeptCode", RightField = "Code", Prefix = "Dept_" });

        Assert.Equal(new[] { "E1 Sales", "E1 Sales", "E2 Operations" }, Texts(Run(def)));
    }

    [Fact]
    public void A_join_to_a_missing_data_set_is_left_out_with_a_warning()
    {
        var def = Definition("{EmpCode}");
        def.Joins.Add(new JoinDefinition { DataSet = "Nope", LeftField = "EmpCode" });

        var result = Run(def);

        Assert.Equal(new[] { "E1", "E2", "E3" }, Texts(result));
        Assert.Contains(result.Warnings, w => w.Contains("Join 'Nope'") && w.Contains("no data set"));
    }

    [Fact]
    public void A_join_without_a_field_is_left_out_with_a_warning()
    {
        var def = Definition("{EmpCode}");
        def.Joins.Add(new JoinDefinition { DataSet = "Leaves", LeftField = "" });

        var result = Run(def);

        Assert.Equal(3, Texts(result).Length);
        Assert.Contains(result.Warnings, w => w.Contains("Join 'Leaves'"));
    }

    [Fact]
    public void A_join_does_not_change_the_rows_it_was_given()
    {
        var rows = Employees();
        var catalog = new DataSetCatalog(Sets());

        var joined = RowJoiner.Join(rows, catalog, new JoinDefinition { DataSet = "Departments", LeftField = "DeptCode", RightField = "Code", Prefix = "Dept_" });

        Assert.Equal(3, joined.Count);
        Assert.All(rows, r => Assert.False(r.ContainsKey("Dept_Name")));
        Assert.True(joined[0].ContainsKey("Dept_Name"));
    }

    // ---- the definition ----

    [Fact]
    public void Data_sets_and_joins_are_saved_and_read_back()
    {
        var def = new ReportDefinition();
        def.DataSets.Add(new DataSetDefinition { Name = "Departments" });
        def.Joins.Add(new JoinDefinition { DataSet = "Departments", LeftField = "DeptCode", RightField = "Code", Kind = JoinKind.Inner, Prefix = "Dept_" });

        var back = ReportDefinition.FromJson(def.ToJson());

        Assert.Equal("Departments", Assert.Single(back.DataSets).Name);
        var join = Assert.Single(back.Joins);
        Assert.Equal((JoinKind.Inner, "Dept_", "Code"), (join.Kind, join.Prefix, join.RightField));
    }

    [Fact]
    public void A_file_with_a_data_set_query_needs_trust_and_a_host_data_set_never_does()
    {
        var def = new ReportDefinition();
        def.DataSource.Provider = DbProvider.Host;
        def.DataSets.Add(new DataSetDefinition { Name = "Ok" });
        Assert.False(def.HasOwnQuery);

        def.DataSets.Add(new DataSetDefinition
        {
            Name = "Own", DataSource = new DataSourceDefinition { Provider = DbProvider.SqlServer, ConnectionString = "Server=x", Query = "select 1" }
        });
        Assert.True(def.HasOwnQuery);
    }

    [Fact]
    public void A_host_data_set_drops_the_query_and_connection_a_file_carries()
    {
        var def = new ReportDefinition();
        def.DataSets.Add(new DataSetDefinition
        {
            Name = " Sneaky ", DataSource = new DataSourceDefinition { Provider = DbProvider.Host, ConnectionString = "Server=evil", Query = "drop table x" }
        });

        def.Normalize();

        var set = Assert.Single(def.DataSets);
        Assert.Equal("Sneaky", set.Name);
        Assert.Equal("", set.DataSource.Query);
        Assert.Equal("", set.DataSource.ConnectionString);
        Assert.False(def.HasOwnQuery);
    }
}
