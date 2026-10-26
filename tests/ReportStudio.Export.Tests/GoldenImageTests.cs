using System.Drawing;
using System.Drawing.Imaging;
using ReportStudio.Core.Data;
using ReportStudio.Core.Model;
using ReportStudio.Export;
using Xunit;

namespace ReportStudio.Export.Tests;

/// <summary>
/// Renders the sample reports to images and compares each page with a stored golden PNG.
/// A missing golden image is created from the current output (commit it); set UPDATE_GOLDEN=1 to refresh
/// after an intended layout change. The Ubuntu fonts must be in /fonts, or the text differs per machine.
/// </summary>
public class GoldenImageTests
{
    private const int Dpi = 96;
    private const int ChannelTolerance = 24;     // anti-aliasing differences below this are ignored
    private const double MaxDifferingShare = 0.0005;

    private static readonly string Root = FindRoot();

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "ReportStudio.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("ReportStudio.sln not found above " + AppContext.BaseDirectory);
    }

    private static string Render(string name, string folder)
    {
        var fonts = Path.Combine(Root, "fonts");
        Assert.True(File.Exists(Path.Combine(fonts, "Ubuntu-Regular.ttf")),
            "Add the Ubuntu font files to the fonts folder first (see fonts/README.txt); image output depends on them.");
        FontCatalog.AddDirectory(fonts);

        var def = ReportDefinition.Load(Path.Combine(Root, "samples", name + ".rsr"));
        var rows = JsonRows.Load(Path.Combine(Root, "samples", name + ".data.json"), def.DataSource);
        var result = ReportRunner.Render(def, rows);

        Directory.CreateDirectory(folder);
        ImageExporter.Export(result, Path.Combine(folder, name + ".png"), ImageFormatKind.Png, Dpi);
        return folder;
    }

    [Theory]
    [InlineData("payslip")]
    [InlineData("form16")]
    public void Sample_pages_match_the_golden_images(string name)
    {
        var temp = Path.Combine(Path.GetTempPath(), "rs-golden-" + Guid.NewGuid().ToString("N"));
        try
        {
            Render(name, temp);
            var goldenDir = Path.Combine(Root, "tests", "ReportStudio.Export.Tests", "Golden");
            Directory.CreateDirectory(goldenDir);
            bool update = Environment.GetEnvironmentVariable("UPDATE_GOLDEN") == "1";

            var files = Directory.GetFiles(temp, "*.png").OrderBy(f => f, StringComparer.Ordinal).ToList();
            Assert.NotEmpty(files);

            foreach (var file in files)
            {
                var golden = Path.Combine(goldenDir, Path.GetFileName(file));
                if (update || !File.Exists(golden))
                {
                    File.Copy(file, golden, overwrite: true);
                    continue;
                }
                var share = DifferingShare(golden, file);
                Assert.True(share <= MaxDifferingShare,
                    $"{Path.GetFileName(file)} differs from its golden image in {share:P3} of pixels (limit {MaxDifferingShare:P3}).");
            }
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch (IOException) { }
        }
    }

    private static double DifferingShare(string expectedPath, string actualPath)
    {
        using var expected = new Bitmap(expectedPath);
        using var actual = new Bitmap(actualPath);
        if (expected.Size != actual.Size) return 1.0;

        var a = Pixels(expected);
        var b = Pixels(actual);
        long differing = 0;
        for (int i = 0; i < a.Length; i += 4)
        {
            if (Math.Abs(a[i] - b[i]) > ChannelTolerance || Math.Abs(a[i + 1] - b[i + 1]) > ChannelTolerance ||
                Math.Abs(a[i + 2] - b[i + 2]) > ChannelTolerance)
                differing++;
        }
        return differing / (double)(a.Length / 4);
    }

    private static byte[] Pixels(Bitmap bmp)
    {
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var bytes = new byte[Math.Abs(data.Stride) * bmp.Height];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
            return bytes;
        }
        finally { bmp.UnlockBits(data); }
    }
}
