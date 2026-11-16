using ReportStudio.Core.Model;
using ReportStudio.Core.Samples;

namespace ReportStudio.Core.Wizard;

/// <summary>A starting point for a new report. <see cref="Create"/> gives a fresh, independent report every time it is called.</summary>
public sealed record ReportTemplate(string Id, string Name, string Category, string Description, Func<ReportDefinition> Create)
{
    public override string ToString() => Name;
}

/// <summary>The templates the designer offers under File &gt; New from template (requirement 45).</summary>
public static class ReportTemplates
{
    public const string Blank = "blank";
    public const string SimpleList = "simple-list";
    public const string GroupedTotals = "grouped-totals";
    public const string RecordCards = "record-cards";
    public const string SalesSample = "sales-sample";
    public const string Payslip = "payslip";
    public const string AddressLabels = "address-labels";

    public static IReadOnlyList<ReportTemplate> All { get; } = new[]
    {
        new ReportTemplate(Blank, "Blank report", "Basic",
            "An empty page with the usual bands, using the sample data.", CreateBlank),
        new ReportTemplate(SimpleList, "Simple list", "Lists",
            "One line for each record with column titles, a total and page numbers.", CreateSimpleList),
        new ReportTemplate(GroupedTotals, "Grouped list with totals", "Lists",
            "Records grouped by country and customer, with a total for every group.", CreateGroupedTotals),
        new ReportTemplate(RecordCards, "Record cards", "Lists",
            "One block for each record, the field name at the left and its value next to it.", CreateRecordCards),
        new ReportTemplate(SalesSample, "Sales by country (full example)", "Examples",
            "The finished sales report with two groups, totals, a parameter and page fields.", SampleReport.Create),
        new ReportTemplate(Payslip, "Payslip (one page per employee)", "HR",
            "One page for each employee with its own page numbers and a net pay total. The rows come from your application.", CreatePayslip),
        new ReportTemplate(AddressLabels, "Address labels (Avery L7160, 3 x 7)", "Labels",
            "A sheet of address labels. The rows come from your application.", CreateAddressLabels)
    };

    public static ReportTemplate? Find(string? id) =>
        All.FirstOrDefault(t => string.Equals(t.Id, id?.Trim(), StringComparison.OrdinalIgnoreCase));

    private static ReportDefinition CreateBlank()
    {
        var def = new ReportDefinition { Name = "Blank report", Title = "Blank report" };
        def.DataSource.Provider = DbProvider.Sample;
        def.Normalize();
        return def;
    }

    private static ReportDefinition CreateSimpleList()
    {
        var o = WizardOptions.ForSampleData();
        o.Name = "Sales list";
        o.Style = WizardStyle.Simple;
        o.Columns = new List<string> { "InvoiceNo", "InvoiceDate", "Customer", "Product", "Quantity", "Amount" };
        o.TotalFields = new List<string> { "Amount" };
        return ReportWizard.Build(o);
    }

    private static ReportDefinition CreateGroupedTotals()
    {
        var o = WizardOptions.ForSampleData();
        o.Name = "Sales by country and customer";
        o.GroupBy = new List<string> { "Country", "Customer" };
        o.Columns = new List<string> { "InvoiceNo", "InvoiceDate", "Product", "Quantity", "UnitPrice", "Amount" };
        o.TotalFields = new List<string> { "Quantity", "Amount" };
        return ReportWizard.Build(o);
    }

    private static ReportDefinition CreateRecordCards()
    {
        var o = WizardOptions.ForSampleData();
        o.Name = "Invoice lines";
        o.Layout = WizardLayout.Form;
        o.Style = WizardStyle.Modern;
        o.Columns = new List<string> { "Customer", "InvoiceNo", "InvoiceDate", "Product", "Quantity", "Amount" };
        return ReportWizard.Build(o);
    }

    private static ReportDefinition CreatePayslip()
    {
        var def = new ReportDefinition { Name = "Payslip", Title = "Payslip" };
        def.DataSource.Provider = DbProvider.Host;
        def.DataSource.Fields = new List<FieldDefinition>
        {
            new() { Name = "EmployeeCode" }, new() { Name = "EmployeeName" }, new() { Name = "Department" },
            new() { Name = "Designation" }, new() { Name = "PayPeriod" }, new() { Name = "Component" },
            new() { Name = "Amount", Type = FieldType.Number }
        };
        def.Parameters.Add(new ReportParameter { Name = "Company", Prompt = "Company name", DefaultValue = "Your Company" });

        double w = def.Page.ContentWidth;
        var group = new GroupDefinition
        {
            Expression = "EmployeeCode", StartNewPage = true, RestartPageNumbers = true, RepeatHeader = true
        };

        group.Header.Height = 100;
        group.Header.Items.Add(ReportWizard.T(0, 4, w, 24, "{@Company}", size: 16, bold: true, color: "#1F3A5F"));
        group.Header.Items.Add(ReportWizard.T(0, 30, w, 18, "Payslip for {PayPeriod}", size: 11, color: "#555555"));
        group.Header.Items.Add(ReportWizard.Line(0, 52, w));
        group.Header.Items.Add(ReportWizard.T(0, 58, w / 2, 18, "Employee: {EmployeeName} ({EmployeeCode})", bold: true));
        group.Header.Items.Add(ReportWizard.T(w / 2, 58, w / 2, 18, "{Department} - {Designation}", align: HAlign.Right));
        group.Header.Items.Add(ReportWizard.T(0, 78, 450, 18, "Component", bold: true, color: "#FFFFFF", back: "#1F3A5F"));
        group.Header.Items.Add(ReportWizard.T(464, 78, w - 464, 18, "Amount", bold: true, color: "#FFFFFF", back: "#1F3A5F", align: HAlign.Right));

        group.Footer.Height = 36;
        group.Footer.Items.Add(ReportWizard.T(0, 8, 290, 16, "Deductions are shown as negative amounts.", size: 8, italic: true, color: "#777777"));
        group.Footer.Items.Add(ReportWizard.Line(300, 2, w - 300));
        group.Footer.Items.Add(ReportWizard.T(300, 8, 160, 20, "Net pay", size: 11, bold: true, align: HAlign.Right));
        group.Footer.Items.Add(ReportWizard.T(464, 8, w - 464, 20, "{Sum(Amount)}", format: "N2", size: 11, bold: true, align: HAlign.Right));
        def.Groups.Add(group);

        def.ReportHeader.Height = 0;
        def.PageHeader.Height = 0;
        def.ReportFooter.Height = 0;

        def.Detail.Height = 20;
        def.Detail.Items.Add(ReportWizard.T(0, 1, 450, 18, "{Component}"));
        def.Detail.Items.Add(ReportWizard.T(464, 1, w - 464, 18, "{Amount}", format: "N2", align: HAlign.Right));

        def.PageFooter.Height = 24;
        def.PageFooter.Items.Add(ReportWizard.Line(0, 2, w));
        def.PageFooter.Items.Add(ReportWizard.T(0, 6, 400, 16, "This is a computer-generated payslip.", size: 8, color: "#777777"));
        def.PageFooter.Items.Add(ReportWizard.T(w - 200, 6, 200, 16, "Page {Page} of {PageCount}", size: 8, align: HAlign.Right, color: "#777777"));

        def.Normalize();
        return def;
    }

    private static ReportDefinition CreateAddressLabels()
    {
        var def = new ReportDefinition { Name = "Address labels", Title = "Address labels" };
        def.DataSource.Provider = DbProvider.Host;
        def.DataSource.Fields = new List<FieldDefinition>
        {
            new() { Name = "Name" }, new() { Name = "AddressLine1" }, new() { Name = "AddressLine2" },
            new() { Name = "City" }, new() { Name = "State" }, new() { Name = "Pincode" }
        };

        var sheet = LabelPreset.All[0];
        sheet.ApplyTo(def);

        double w = LabelPreset.ToPixels(sheet.LabelWidthMm) - 12;
        def.Detail.Items.Add(ReportWizard.T(6, 6, w, 18, "{Name}", size: 10, bold: true));
        def.Detail.Items.Add(ReportWizard.T(6, 26, w, 16, "{AddressLine1}"));
        def.Detail.Items.Add(ReportWizard.T(6, 44, w, 16, "{AddressLine2}"));
        def.Detail.Items.Add(ReportWizard.T(6, 62, w, 16, "{City} - {Pincode}"));
        def.Detail.Items.Add(ReportWizard.T(6, 80, w, 16, "{State}"));

        def.Normalize();
        return def;
    }
}
