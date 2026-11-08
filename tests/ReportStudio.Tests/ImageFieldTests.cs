using System.Globalization;
using ReportStudio.Core.Layout;
using ReportStudio.Core.Model;
using Xunit;

namespace ReportStudio.Tests;

/// <summary>Images from a data field (requirement 35, first part): the decoding rules and the engine.</summary>
public class ImageFieldTests
{
    // A real 1x1 PNG.
    private const string PngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

    private static byte[] Png() => Convert.FromBase64String(PngBase64);

    // ---- the decoding rules ----

    [Fact]
    public void Bytes_that_start_like_a_picture_are_accepted()
    {
        Assert.True(ImageData.LooksLikePicture(Png()));
        Assert.True(ImageData.LooksLikePicture(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0 }));          // JPEG
        Assert.True(ImageData.LooksLikePicture("GIF89a"u8.ToArray()));
        Assert.True(ImageData.LooksLikePicture("BM...."u8.ToArray()));
    }

    [Fact]
    public void Other_bytes_are_not_pictures()
    {
        Assert.False(ImageData.LooksLikePicture(null));
        Assert.False(ImageData.LooksLikePicture(Array.Empty<byte>()));
        Assert.False(ImageData.LooksLikePicture(new byte[] { 1, 2, 3 }));
        Assert.False(ImageData.LooksLikePicture("hello world"u8.ToArray()));
    }

    [Fact]
    public void Base64_text_is_decoded_with_or_without_the_data_address_start()
    {
        Assert.Equal(Png(), ImageData.FromText(PngBase64));
        Assert.Equal(Png(), ImageData.FromText("data:image/png;base64," + PngBase64));
        Assert.Equal(Png(), ImageData.FromText("  " + PngBase64[..20] + "\r\n" + PngBase64[20..] + "  "));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("logo.png")]
    [InlineData("C:\\pictures\\logo.png")]
    [InlineData("not base64 at all!!")]
    [InlineData("aGVsbG8gd29ybGQ=")]                      // valid base64, but "hello world" is not a picture
    public void Text_that_is_not_a_picture_gives_nothing(string? text)
    {
        Assert.Null(ImageData.FromText(text));
    }

    [Fact]
    public void A_field_value_may_be_bytes_or_text_and_nothing_else()
    {
        Assert.Equal(Png(), ImageData.FromValue(Png()));
        Assert.Equal(Png(), ImageData.FromValue(PngBase64));
        Assert.Null(ImageData.FromValue(null));
        Assert.Null(ImageData.FromValue(42m));
        Assert.Null(ImageData.FromValue(new byte[] { 1, 2, 3, 4 }));
    }

    [Theory]
    [InlineData("{Photo}", "Photo")]
    [InlineData("  {Photo}  ", "Photo")]
    [InlineData("{ Photo }", "Photo")]
    [InlineData("{Emp.Photo}", "Emp.Photo")]
    [InlineData("", null)]
    [InlineData("{}", null)]
    [InlineData("photos/{Code}.png", null)]
    [InlineData("{A}{B}", null)]
    [InlineData("{A} and", null)]
    [InlineData("logo.png", null)]
    public void A_single_expression_is_told_apart_from_a_text_around_it(string template, string? expected)
    {
        Assert.Equal(expected, ImageData.SingleExpression(template));
    }

    // ---- in a report ----

    private static ReportDefinition Definition(string imagePath)
    {
        var def = new ReportDefinition { Name = "Pictures" };
        def.DataSource.Provider = DbProvider.Host;
        def.ReportHeader.Height = 0; def.PageHeader.Height = 0; def.ReportFooter.Height = 0; def.PageFooter.Height = 0;
        def.Detail.Height = 22;
        def.Detail.Items.Add(new ReportItem { Name = "Pic", Kind = ItemKind.Image, ImagePath = imagePath, Width = 40, Height = 20 });
        return def;
    }

    private static ReportResult Run(string imagePath, object? value, string? baseDirectory = null) =>
        ReportEngine.Run(Definition(imagePath), null,
            new[] { (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["Photo"] = value } },
            new ReportOptions { Culture = CultureInfo.InvariantCulture, BaseDirectory = baseDirectory });

    private static byte[]? Picture(ReportResult r) => r.Pages[0].Items.First(i => i.Kind == ItemKind.Image).Image;

    [Fact]
    public void A_bytes_field_becomes_the_picture()
    {
        var result = Run("{Photo}", Png());

        Assert.Equal(Png(), Picture(result));
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void A_base64_field_becomes_the_picture()
    {
        Assert.Equal(Png(), Picture(Run("{Photo}", PngBase64)));
        Assert.Equal(Png(), Picture(Run("{Photo}", "data:image/png;base64," + PngBase64)));
    }

    [Fact]
    public void An_empty_field_prints_no_picture_and_no_warning()
    {
        foreach (var value in new object?[] { null, DBNull.Value, "", "  " })
        {
            var result = Run("{Photo}", value);
            Assert.Null(Picture(result));
            Assert.Empty(result.Warnings);
        }
    }

    [Fact]
    public void Bytes_that_are_not_a_picture_give_a_warning()
    {
        var result = Run("{Photo}", new byte[] { 1, 2, 3, 4, 5, 6 });

        Assert.Null(Picture(result));
        Assert.Contains(result.Warnings, w => w.Contains("does not hold a picture"));
    }

    [Fact]
    public void A_field_holding_a_file_name_still_loads_that_file()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rs-img-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "face.png"), Png());

            Assert.Equal(Png(), Picture(Run("{Photo}", "face.png", dir)));
            Assert.Equal(Png(), Picture(Run("{Photo}.png", "face", dir)));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void A_missing_file_still_warns()
    {
        var result = Run("{Photo}", "nowhere.png", Path.GetTempPath());

        Assert.Null(Picture(result));
        Assert.Contains(result.Warnings, w => w.Contains("Image not found"));
    }
}
