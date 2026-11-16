using System.Globalization;
using ReportStudio.Core.Layout;
using ReportStudio.Core.Model;
using ReportStudio.Core.Wizard;
using Xunit;

namespace ReportStudio.Tests;

/// <summary>Report wizard and templates (requirement 45a).</summary>
public class ReportWizardTests
{
    private static WizardOptions Basic() => new()
    {
        Name = "Pay list",
        DataSource = new DataSourceDefinition { Provider = DbProvider.Host },
        Fields = FieldDefinition.ParseLines(new[] { "Dept", "Name", "Amount:Number", "Joined:Date" })
    };

    private static Dictionary<string, object?> Row(string dept, string name, decimal amount) =>
        new(StringComparer.OrdinalIgnoreCase) { ["Dept"] = dept, ["Name"] = name, ["Amount"] = amount };

    private static ReportResult Run(ReportDefinition def, IEnumerable<Dictionary<string, object?>> rows,
        IDictionary<string, string?>? parameters = null) =>
        ReportEngine.Run(def, parameters, rows, new ReportOptions { Culture = CultureInfo.InvariantCulture });

    private static List<string> Texts(ReportResult result, BandKind band) =>
        result.Pages.SelectMany(p => p.Items).Where(i => i.Band == band).Select(i => i.Text).ToList();

    private static List<string> Texts(Band band) => band.Items.Where(i => i.Kind == ItemKind.Text).Select(i => i.Text).ToList();

    // ---- names ----

    [Theory]
    [InlineData("InvoiceNo", "Invoice No")]
    [InlineData("invoice_no", "Invoice no")]
    [InlineData("unit_price", "Unit price")]
    [InlineData("PFAmount", "PF Amount")]
    [InlineData("Order Date", "Order Date")]
    [InlineData("Net2Pay", "Net2 Pay")]
    [InlineData("  ", "")]
    [InlineData(null, "")]
    public void A_field_name_becomes_a_column_title(string? name, string expected)
    {
        Assert.Equal(expected, ReportWizard.Humanize(name));
    }

    [Theory]
    [InlineData("Amount", "Amount")]
    [InlineData("net_pay", "net_pay")]
    [InlineData("Order Date", "[Order Date]")]
    [InlineData("1st", "[1st]")]
    [InlineData("a.b", "[a.b]")]
    public void A_field_that_is_not_a_plain_name_is_written_in_brackets(string name, string expected)
    {
        Assert.Equal(expected, ReportWizard.Ref(name));
    }

    // ---- table layout ----

    [Fact]
    public void A_table_has_a_title_and_a_cell_for_every_column()
    {
        var o = Basic();
        o.Columns = new List<string> { "Name", "Amount", "Joined" };

        var def = ReportWizard.Build(o);

        Assert.Equal(new[] { "Name", "Amount", "Joined" }, Texts(def.PageHeader));
        Assert.Equal(new[] { "{Name}", "{Amount}", "{Joined}" }, Texts(def.Detail));
        Assert.Empty(def.Groups);
    }

    [Fact]
    public void Without_a_choice_every_field_is_a_column_in_the_order_of_the_data()
    {
        var def = ReportWizard.Build(Basic());

        Assert.Equal(new[] { "{Dept}", "{Name}", "{Amount}", "{Joined}" }, Texts(def.Detail));
    }

    [Fact]
    public void Numbers_and_dates_get_a_format_and_numbers_line_up_at_the_right()
    {
        var def = ReportWizard.Build(Basic());

        var amount = def.Detail.Items.Single(i => i.Text == "{Amount}");
        var joined = def.Detail.Items.Single(i => i.Text == "{Joined}");
        var name = def.Detail.Items.Single(i => i.Text == "{Name}");

        Assert.Equal("N2", amount.Format);
        Assert.Equal(HAlign.Right, amount.HAlign);
        Assert.Equal("dd-MMM-yyyy", joined.Format);
        Assert.Equal(HAlign.Left, name.HAlign);
        Assert.Equal("", name.Format);
    }

    [Fact]
    public void A_column_title_sits_above_its_column()
    {
        var o = Basic();
        o.Columns = new List<string> { "Name", "Amount" };

        var def = ReportWizard.Build(o);

        var title = def.PageHeader.Items.Single(i => i.Text == "Amount");
        var cell = def.Detail.Items.Single(i => i.Text == "{Amount}");
        Assert.Equal(cell.X, title.X);
        Assert.Equal(cell.Width, title.Width);
    }

    [Fact]
    public void The_columns_fill_the_width_of_the_page_with_the_text_column_taking_the_rest()
    {
        var o = Basic();
        o.Columns = new List<string> { "Name", "Amount" };

        var def = ReportWizard.Build(o);

        var name = def.Detail.Items.Single(i => i.Text == "{Name}");
        var amount = def.Detail.Items.Single(i => i.Text == "{Amount}");
        Assert.Equal(0, name.X);
        Assert.Equal(90, amount.Width);
        Assert.True(Math.Abs(amount.X + amount.Width - def.Page.ContentWidth) < 0.1);
        Assert.True(name.Width > amount.Width);
    }

    [Fact]
    public void A_field_with_a_space_in_its_name_is_written_in_brackets()
    {
        var o = Basic();
        o.Fields.Add(new FieldDefinition { Name = "Order Date", Type = FieldType.Date });
        o.Columns = new List<string> { "Name", "Order Date" };

        var def = ReportWizard.Build(o);

        Assert.Contains("{[Order Date]}", Texts(def.Detail));
        Assert.Contains("Order Date", Texts(def.PageHeader));
    }

    // ---- groups and totals ----

    [Fact]
    public void A_grouped_field_is_printed_in_the_group_header_and_is_not_a_column()
    {
        var o = Basic();
        o.GroupBy = new List<string> { "Dept" };
        o.Columns = new List<string> { "Dept", "Name", "Amount" };

        var def = ReportWizard.Build(o);

        var group = Assert.Single(def.Groups);
        Assert.Equal("Dept", group.Expression);
        Assert.Equal(new[] { "{Dept}" }, Texts(group.Header));
        Assert.Equal(new[] { "{Name}", "{Amount}" }, Texts(def.Detail));
        Assert.DoesNotContain("Dept", Texts(def.PageHeader));
    }

    [Fact]
    public void Inner_groups_are_indented_and_smaller()
    {
        var o = Basic();
        o.GroupBy = new List<string> { "Dept", "Name" };
        o.Columns = new List<string> { "Amount" };

        var def = ReportWizard.Build(o);

        var outer = def.Groups[0].Header.Items.Single();
        var inner = def.Groups[1].Header.Items.Single();
        Assert.True(inner.X > outer.X);
        Assert.True(inner.FontSize < outer.FontSize);
        Assert.True(def.Groups[0].Header.Height > def.Groups[1].Header.Height);
    }

    [Fact]
    public void A_total_sits_under_its_column_in_every_group_and_at_the_end()
    {
        var o = Basic();
        o.GroupBy = new List<string> { "Dept" };
        o.Columns = new List<string> { "Name", "Amount" };
        o.TotalFields = new List<string> { "Amount" };

        var def = ReportWizard.Build(o);

        var column = def.Detail.Items.Single(i => i.Text == "{Amount}");
        var groupTotal = def.Groups[0].Footer.Items.Single(i => i.Text == "{Sum(Amount)}");
        var grandTotal = def.ReportFooter.Items.Single(i => i.Text == "{Sum(Amount)}");

        Assert.Equal(column.X, groupTotal.X);
        Assert.Equal(column.Width, groupTotal.Width);
        Assert.Equal(column.X, grandTotal.X);
        Assert.Equal("N2", groupTotal.Format);
        Assert.Contains("Total {Dept}", Texts(def.Groups[0].Footer));
        Assert.Contains("Grand total", Texts(def.ReportFooter));
    }

    [Fact]
    public void A_group_without_totals_has_no_footer_space()
    {
        var o = Basic();
        o.GroupBy = new List<string> { "Dept" };

        var def = ReportWizard.Build(o);

        Assert.Equal(0, def.Groups[0].Footer.Height);
        Assert.Empty(def.Groups[0].Footer.Items);
    }

    [Fact]
    public void Without_totals_the_end_of_the_report_counts_the_records()
    {
        var def = ReportWizard.Build(Basic());

        Assert.Contains("Records: {Count()}", Texts(def.ReportFooter));
    }

    [Fact]
    public void The_grand_total_can_be_left_out()
    {
        var o = Basic();
        o.GrandTotal = false;

        var def = ReportWizard.Build(o);

        Assert.Equal(0, def.ReportFooter.Height);
        Assert.Empty(def.ReportFooter.Items);
    }

    // ---- form layout ----

    [Fact]
    public void A_form_prints_the_name_and_the_value_of_every_field_one_under_the_other()
    {
        var o = Basic();
        o.Layout = WizardLayout.Form;
        o.Columns = new List<string> { "Name", "Amount" };

        var def = ReportWizard.Build(o);

        Assert.Equal(0, def.PageHeader.Height);
        Assert.Equal(50, def.Detail.Height);
        Assert.Equal(new[] { "Name", "{Name}", "Amount", "{Amount}" }, Texts(def.Detail));
        var second = def.Detail.Items.Single(i => i.Text == "{Amount}");
        var first = def.Detail.Items.Single(i => i.Text == "{Name}");
        Assert.True(second.Y > first.Y);
    }

    [Fact]
    public void A_form_stacks_its_totals_at_the_right()
    {
        var o = Basic();
        o.Layout = WizardLayout.Form;
        o.TotalFields = new List<string> { "Amount" };

        var def = ReportWizard.Build(o);

        Assert.Contains("Grand total Amount", Texts(def.ReportFooter));
        Assert.Contains("{Sum(Amount)}", Texts(def.ReportFooter));
    }

    // ---- page ----

    [Fact]
    public void The_paper_and_orientation_set_the_page_size()
    {
        var portrait = ReportWizard.Build(Basic());
        var o = Basic();
        o.Paper = WizardPaper.Letter;
        o.Landscape = true;
        var landscape = ReportWizard.Build(o);

        Assert.Equal(794, portrait.Page.Width);
        Assert.Equal(1123, portrait.Page.Height);
        Assert.Equal(1056, landscape.Page.Width);
        Assert.Equal(816, landscape.Page.Height);
    }

    [Fact]
    public void Page_numbers_and_the_printed_date_can_be_left_out()
    {
        var o = Basic();
        o.PageNumbers = false;
        o.PrintedDate = false;

        var def = ReportWizard.Build(o);

        Assert.Equal(0, def.PageFooter.Height);
        Assert.DoesNotContain(def.ReportHeader.Items, i => i.Text.StartsWith("Printed"));
    }

    [Fact]
    public void The_title_defaults_to_the_name()
    {
        var o = Basic();
        o.Title = "  ";

        Assert.Equal("Pay list", ReportWizard.Build(o).Title);

        o.Title = "Monthly pay";
        Assert.Equal("Monthly pay", ReportWizard.Build(o).Title);
    }

    [Fact]
    public void The_style_sets_the_colours()
    {
        var corporate = ReportWizard.Build(Basic());
        var o = Basic();
        o.Style = WizardStyle.Classic;
        var classic = ReportWizard.Build(o);

        Assert.Equal("#1F3A5F", corporate.PageHeader.Items[0].BackColor);
        Assert.Equal("", classic.PageHeader.Items[0].BackColor);
        Assert.Contains(classic.Detail.Items, i => i.Kind == ItemKind.Line);
        Assert.DoesNotContain(corporate.Detail.Items, i => i.Kind == ItemKind.Line);
    }

    // ---- data source ----

    [Fact]
    public void The_report_gets_its_own_copy_of_the_data_source_and_the_field_list()
    {
        var o = Basic();

        var def = ReportWizard.Build(o);

        Assert.NotSame(o.DataSource, def.DataSource);
        Assert.Equal(DbProvider.Host, def.DataSource.Provider);
        Assert.Equal(new[] { "Dept:Text", "Name:Text", "Amount:Number", "Joined:Date" }, def.DataSource.Fields.Select(f => f.ToString()));
    }

    [Fact]
    public void A_field_list_the_data_source_already_has_is_kept()
    {
        var o = Basic();
        o.DataSource.Fields = FieldDefinition.ParseLines(new[] { "Dept", "Name", "Amount:Number", "Joined:Date", "Extra" });

        var def = ReportWizard.Build(o);

        Assert.Equal(5, def.DataSource.Fields.Count);
    }

    [Fact]
    public void The_report_survives_saving_and_loading()
    {
        var o = Basic();
        o.GroupBy = new List<string> { "Dept" };
        o.TotalFields = new List<string> { "Amount" };
        var def = ReportWizard.Build(o);

        var copy = ReportDefinition.FromJson(def.ToJson());

        Assert.Equal(def.ToJson(), copy.ToJson());
    }

    // ---- problems ----

    [Fact]
    public void A_fine_set_of_options_has_no_problems()
    {
        Assert.Empty(ReportWizard.Check(Basic()));
    }

    [Fact]
    public void Without_fields_there_is_nothing_to_build()
    {
        var o = Basic();
        o.Fields.Clear();

        var problem = Assert.Single(ReportWizard.Check(o));
        Assert.Contains("fields", problem);
        Assert.Throws<InvalidOperationException>(() => ReportWizard.Build(o));
    }

    [Fact]
    public void An_unknown_field_is_named()
    {
        var o = Basic();
        o.Columns = new List<string> { "Name", "Salary" };

        var problem = Assert.Single(ReportWizard.Check(o));
        Assert.Contains("Salary", problem);
    }

    [Fact]
    public void Field_names_are_matched_without_regard_to_case()
    {
        var o = Basic();
        o.Columns = new List<string> { "name", "AMOUNT" };

        Assert.Empty(ReportWizard.Check(o));
        Assert.Equal(new[] { "{Name}", "{Amount}" }, Texts(ReportWizard.Build(o).Detail));
    }

    [Fact]
    public void At_most_three_group_levels_are_allowed()
    {
        var o = Basic();
        o.Fields = FieldDefinition.ParseLines(new[] { "A", "B", "C", "D", "Value:Number" });
        o.GroupBy = new List<string> { "A", "B", "C", "D" };

        Assert.Contains(ReportWizard.Check(o), p => p.Contains("at most 3"));
    }

    [Fact]
    public void Grouping_by_every_field_leaves_no_column()
    {
        var o = Basic();
        o.GroupBy = new List<string> { "Dept", "Name", "Amount", "Joined" };

        Assert.Contains(ReportWizard.Check(o), p => p.Contains("column"));
    }

    [Fact]
    public void A_total_must_be_a_number_that_is_shown()
    {
        var text = Basic();
        text.TotalFields = new List<string> { "Name" };
        var hidden = Basic();
        hidden.Columns = new List<string> { "Name" };
        hidden.TotalFields = new List<string> { "Amount" };

        Assert.Contains(ReportWizard.Check(text), p => p.Contains("Name"));
        Assert.Contains(ReportWizard.Check(hidden), p => p.Contains("Amount"));
    }

    [Fact]
    public void Braces_in_the_name_or_title_are_refused_because_they_would_be_read_as_expressions()
    {
        var named = Basic();
        named.Name = "Pay {2026}";
        var titled = Basic();
        titled.Title = "Pay {Now}";

        Assert.NotEmpty(ReportWizard.Check(named));
        Assert.NotEmpty(ReportWizard.Check(titled));
    }

    [Fact]
    public void A_field_name_with_a_brace_or_bracket_is_refused()
    {
        var o = Basic();
        o.Fields.Add(new FieldDefinition { Name = "Pay]ment" });

        Assert.Contains(ReportWizard.Check(o), p => p.Contains("Pay]ment"));
    }

    [Fact]
    public void Too_many_columns_for_the_page_are_refused_until_it_is_landscape()
    {
        var o = Basic();
        o.Fields = FieldDefinition.ParseLines(Enumerable.Range(1, 10).Select(n => "Field" + n));

        Assert.Contains(ReportWizard.Check(o), p => p.Contains("too many columns"));

        o.Landscape = true;
        Assert.Empty(ReportWizard.Check(o));
    }

    [Fact]
    public void A_form_has_no_limit_on_the_number_of_fields_across()
    {
        var o = Basic();
        o.Fields = FieldDefinition.ParseLines(Enumerable.Range(1, 10).Select(n => "Field" + n));
        o.Layout = WizardLayout.Form;

        Assert.Empty(ReportWizard.Check(o));
    }

    // ---- run in the engine ----

    [Fact]
    public void A_grouped_report_with_totals_adds_up_in_the_engine()
    {
        var o = Basic();
        o.GroupBy = new List<string> { "Dept" };
        o.Columns = new List<string> { "Name", "Amount" };
        o.TotalFields = new List<string> { "Amount" };
        var def = ReportWizard.Build(o);

        var result = Run(def, new[] { Row("A", "Ann", 10), Row("A", "Bob", 20), Row("B", "Cy", 5) });

        Assert.Equal(new[] { "A", "B" }, Texts(result, BandKind.GroupHeader));
        Assert.Equal(new[] { "30.00", "5.00" }, Texts(result, BandKind.GroupFooter).Where(t => t.EndsWith(".00")));
        Assert.Equal(new[] { "35.00" }, Texts(result, BandKind.ReportFooter).Where(t => t.EndsWith(".00")));
        Assert.Equal(new[] { "Ann", "Bob", "Cy" }, Texts(result, BandKind.Detail).Where(t => !t.EndsWith(".00")));
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void A_form_report_runs_and_counts_its_records()
    {
        var o = Basic();
        o.Layout = WizardLayout.Form;
        o.Columns = new List<string> { "Name", "Amount" };
        var def = ReportWizard.Build(o);

        var result = Run(def, new[] { Row("A", "Ann", 10), Row("B", "Bob", 20) });

        Assert.Contains("Records: 2", Texts(result, BandKind.ReportFooter));
        Assert.Contains("Ann", Texts(result, BandKind.Detail));
        Assert.Contains("20.00", Texts(result, BandKind.Detail));
    }

    // ---- templates ----

    [Fact]
    public void Every_template_has_a_unique_id_a_name_and_a_description()
    {
        var ids = ReportTemplates.All.Select(t => t.Id).ToList();

        Assert.Equal(ids.Count, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(ReportTemplates.All, t =>
        {
            Assert.False(string.IsNullOrWhiteSpace(t.Name));
            Assert.False(string.IsNullOrWhiteSpace(t.Description));
            Assert.False(string.IsNullOrWhiteSpace(t.Category));
        });
    }

    [Fact]
    public void A_template_is_found_by_its_id_without_regard_to_case()
    {
        Assert.Same(ReportTemplates.All[0], ReportTemplates.Find(" BLANK "));
        Assert.Null(ReportTemplates.Find("nothing"));
        Assert.Null(ReportTemplates.Find(null));
    }

    [Fact]
    public void Every_template_gives_a_fresh_report_that_survives_saving_and_loading()
    {
        foreach (var template in ReportTemplates.All)
        {
            var first = template.Create();
            var second = template.Create();

            Assert.NotSame(first, second);
            Assert.Equal(first.ToJson(), second.ToJson());
            Assert.Equal(first.ToJson(), ReportDefinition.FromJson(first.ToJson()).ToJson());
        }
    }

    [Theory]
    [InlineData(ReportTemplates.Blank)]
    [InlineData(ReportTemplates.SimpleList)]
    [InlineData(ReportTemplates.GroupedTotals)]
    [InlineData(ReportTemplates.RecordCards)]
    [InlineData(ReportTemplates.SalesSample)]
    public void A_template_on_the_sample_data_runs_without_warnings(string id)
    {
        var def = ReportTemplates.Find(id)!.Create();

        var result = ReportEngine.Run(def, null, new ReportOptions { Culture = CultureInfo.InvariantCulture });

        Assert.NotEmpty(result.Pages);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void The_grouped_template_groups_by_country_and_customer_with_totals()
    {
        var def = ReportTemplates.Find(ReportTemplates.GroupedTotals)!.Create();

        Assert.Equal(new[] { "Country", "Customer" }, def.Groups.Select(g => g.Expression));
        Assert.Contains("{Sum(Amount)}", Texts(def.ReportFooter));
        Assert.Contains("{Sum(Quantity)}", Texts(def.ReportFooter));
    }

    [Fact]
    public void The_payslip_prints_one_page_for_each_employee_with_its_own_net_pay()
    {
        var def = ReportTemplates.Find(ReportTemplates.Payslip)!.Create();
        Dictionary<string, object?> Line(string code, string who, string component, decimal amount) =>
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["EmployeeCode"] = code, ["EmployeeName"] = who, ["Department"] = "Accounts", ["Designation"] = "Clerk",
                ["PayPeriod"] = "October 2026", ["Component"] = component, ["Amount"] = amount
            };

        var result = Run(def, new[]
        {
            Line("E1", "Asha", "Basic", 30000), Line("E1", "Asha", "HRA", 12000), Line("E1", "Asha", "PF", -3600),
            Line("E2", "Ravi", "Basic", 20000), Line("E2", "Ravi", "PT", -200)
        }, new Dictionary<string, string?> { ["Company"] = "Acme" });

        Assert.Equal(2, result.Pages.Count);
        Assert.Equal(new[] { "38400.00", "19800.00" },
            Texts(result, BandKind.GroupFooter).Where(t => t.EndsWith(".00")).Select(t => t.Replace(",", "")));
        Assert.Contains("Acme", Texts(result, BandKind.GroupHeader));
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void The_label_template_fits_21_labels_on_a_sheet()
    {
        var def = ReportTemplates.Find(ReportTemplates.AddressLabels)!.Create();
        Dictionary<string, object?> Label(int n) => new(StringComparer.OrdinalIgnoreCase)
        {
            ["Name"] = "Person " + n, ["AddressLine1"] = "1 Main Road", ["AddressLine2"] = "Near the park",
            ["City"] = "Pune", ["State"] = "Maharashtra", ["Pincode"] = "411001"
        };

        var oneSheet = Run(def, Enumerable.Range(1, 21).Select(Label));
        var twoSheets = Run(def, Enumerable.Range(1, 22).Select(Label));

        Assert.Single(oneSheet.Pages);
        Assert.Equal(2, twoSheets.Pages.Count);
        Assert.Equal(3, def.Page.Columns);
        Assert.Equal(ColumnOrder.Across, def.Page.ColumnOrder);
    }
}
