using System.Drawing;
using System.Drawing.Printing;
using ReportStudio.Core.Layout;
using ReportStudio.Export;

namespace ReportStudio.UI;

/// <summary>Prints a rendered report. Each page is rasterised at 200 dpi, which keeps output identical to the preview.</summary>
public static class PrintHelper
{
    public static void Print(ReportResult report, IWin32Window? owner = null)
    {
        if (report.Pages.Count == 0) return;

        using var doc = new PrintDocument { DocumentName = report.ReportName };
        var first = report.Pages[0];
        doc.DefaultPageSettings.Landscape = first.Width > first.Height;
        doc.DefaultPageSettings.PaperSize = new PaperSize(
            "Report", (int)Math.Round(Math.Min(first.Width, first.Height) * 100 / 96.0),
            (int)Math.Round(Math.Max(first.Width, first.Height) * 100 / 96.0));
        doc.DefaultPageSettings.Margins = new Margins(0, 0, 0, 0);

        using var dialog = new PrintDialog
        {
            Document = doc, AllowSomePages = true, UseEXDialog = true,
            PrinterSettings = { MinimumPage = 1, MaximumPage = report.Pages.Count, FromPage = 1, ToPage = report.Pages.Count }
        };
        if (dialog.ShowDialog(owner) != DialogResult.OK) return;

        bool range = doc.PrinterSettings.PrintRange == PrintRange.SomePages;
        int index = range ? Math.Max(0, doc.PrinterSettings.FromPage - 1) : 0;
        int last = range ? Math.Min(doc.PrinterSettings.ToPage, report.Pages.Count) : report.Pages.Count;

        doc.PrintPage += (_, e) =>
        {
            var page = report.Pages[index];
            using var bmp = ImageExporter.RenderPage(page, 200);
            var g = e.Graphics!;
            float hx = e.PageSettings.HardMarginX, hy = e.PageSettings.HardMarginY;
            g.DrawImage(bmp, new RectangleF(-hx, -hy, e.PageBounds.Width, e.PageBounds.Height));
            index++;
            e.HasMorePages = index < last;
        };
        doc.Print();
    }
}
