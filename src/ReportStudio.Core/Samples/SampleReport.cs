using ReportStudio.Core.Model;

namespace ReportStudio.Core.Samples;

/// <summary>A ready-made "Sales by Country / Customer" report that exercises groups, aggregates and page fields.</summary>
public static class SampleReport
{
    public static ReportDefinition Create()
    {
        var def = new ReportDefinition { Name = "Sales by Country", Title = "Sales by Country and Customer" };
        def.DataSource.Provider = DbProvider.Sample;
        def.Parameters.Add(new ReportParameter { Name = "Company", Prompt = "Company name", DefaultValue = "Acme Industries" });

        def.Groups.Add(new GroupDefinition { Expression = "Country" });
        def.Groups.Add(new GroupDefinition { Expression = "Customer" });
        def.SortBy.Add("InvoiceNo");

        // ---- Report header ----
        def.ReportHeader.Height = 64;
        def.ReportHeader.Items.Add(T(0, 4, 500, 30, "{@Company}", size: 18, bold: true, color: "#1F3A5F"));
        def.ReportHeader.Items.Add(T(0, 36, 500, 20, "Sales by Country and Customer", size: 11, color: "#555555"));
        def.ReportHeader.Items.Add(T(514, 8, 200, 18, "Printed {Format(Now, 'dd-MMM-yyyy HH:mm')}", align: HAlign.Right, color: "#555555"));
        def.ReportHeader.Items.Add(Line(0, 60, 714));

        // ---- Page header (column titles) ----
        def.PageHeader.Height = 26;
        string[] cols = { "Invoice", "Date", "Product", "Qty", "Unit price", "Amount" };
        double[] x = { 0, 70, 160, 400, 470, 590 };
        double[] w = { 70, 90, 240, 70, 120, 124 };
        for (int i = 0; i < cols.Length; i++)
        {
            def.PageHeader.Items.Add(T(x[i], 4, w[i], 18, cols[i], bold: true, color: "#FFFFFF", back: "#1F3A5F",
                align: i >= 3 ? HAlign.Right : HAlign.Left));
        }

        // ---- Group 1: Country ----
        var country = def.Groups[0];
        country.Header.Height = 28;
        country.Header.Items.Add(T(0, 4, 714, 20, "{Country}", size: 12, bold: true, color: "#1F3A5F", back: "#E6EEF7"));
        country.Footer.Height = 30;
        country.Footer.Items.Add(T(300, 6, 280, 18, "Total {Country}  ({Count()} lines)", bold: true, align: HAlign.Right));
        country.Footer.Items.Add(T(590, 6, 124, 18, "{Sum(Amount)}", format: "N2", bold: true, align: HAlign.Right));
        country.Footer.Items.Add(Line(300, 2, 414));

        // ---- Group 2: Customer ----
        var customer = def.Groups[1];
        customer.Header.Height = 22;
        customer.Header.Items.Add(T(10, 2, 400, 18, "{Customer}", bold: true));
        customer.Footer.Height = 24;
        customer.Footer.Items.Add(T(300, 3, 280, 18, "Subtotal {Customer}", italic: true, align: HAlign.Right, color: "#555555"));
        customer.Footer.Items.Add(T(590, 3, 124, 18, "{Sum(Amount)}", format: "N2", italic: true, align: HAlign.Right, color: "#555555"));

        // ---- Detail ----
        def.Detail.Height = 20;
        def.Detail.Items.Add(T(0, 1, 70, 18, "{InvoiceNo}", format: "0"));
        def.Detail.Items.Add(T(70, 1, 90, 18, "{InvoiceDate}", format: "dd-MMM-yy"));
        def.Detail.Items.Add(T(160, 1, 240, 18, "{Product}"));
        def.Detail.Items.Add(T(400, 1, 70, 18, "{Quantity}", format: "0", align: HAlign.Right));
        def.Detail.Items.Add(T(470, 1, 120, 18, "{UnitPrice}", format: "N2", align: HAlign.Right));
        def.Detail.Items.Add(T(590, 1, 124, 18, "{Amount}", format: "N2", align: HAlign.Right));

        // ---- Report footer ----
        def.ReportFooter.Height = 40;
        def.ReportFooter.Items.Add(Line(300, 4, 414));
        def.ReportFooter.Items.Add(T(300, 10, 280, 22, "Grand total", size: 11, bold: true, align: HAlign.Right));
        def.ReportFooter.Items.Add(T(590, 10, 124, 22, "{Sum(Amount)}", format: "N2", size: 11, bold: true, align: HAlign.Right));

        // ---- Page footer ----
        def.PageFooter.Height = 24;
        def.PageFooter.Items.Add(Line(0, 2, 714));
        def.PageFooter.Items.Add(T(0, 6, 400, 16, "{@Company}", size: 8, color: "#777777"));
        def.PageFooter.Items.Add(T(514, 6, 200, 16, "Page {Page} of {PageCount}", size: 8, align: HAlign.Right, color: "#777777"));

        def.Normalize();
        return def;
    }

    private static ReportItem T(double x, double y, double w, double h, string text, string format = "",
        double size = 9, bool bold = false, bool italic = false, HAlign align = HAlign.Left,
        string color = "#000000", string back = "") => new()
    {
        Kind = ItemKind.Text, X = x, Y = y, Width = w, Height = h, Text = text, Format = format,
        FontSize = size, Bold = bold, Italic = italic, HAlign = align, ForeColor = color, BackColor = back,
        VAlign = VAlign.Middle, WordWrap = false
    };

    private static ReportItem Line(double x, double y, double w) => new()
    {
        Kind = ItemKind.Line, X = x, Y = y, Width = w, Height = 0, ForeColor = "#9AA5B1", BorderWidth = 1
    };
}
