using ReportStudio.Core.Text;
using Xunit;

namespace ReportStudio.Tests;

/// <summary>Which Indic script a text uses (requirement 15).</summary>
public class ComplexScriptTests
{
    [Theory]
    [InlineData("नमस्ते", IndicScript.Devanagari)]
    [InlineData("வணக்கம்", IndicScript.Tamil)]
    [InlineData("నమస్కారం", IndicScript.Telugu)]
    [InlineData("ನಮಸ್ಕಾರ", IndicScript.Kannada)]
    [InlineData("നമസ്കാരം", IndicScript.Malayalam)]
    [InlineData("નમસ્તે", IndicScript.Gujarati)]
    [InlineData("নমস্কার", IndicScript.Bengali)]
    [InlineData("ਸਤ ਸ੍ਰੀ ਅਕਾਲ", IndicScript.Gurmukhi)]
    [InlineData("ନମସ୍କାର", IndicScript.Odia)]
    public void Each_script_is_recognised(string text, IndicScript expected)
    {
        Assert.Equal(expected, ComplexScript.Detect(text));
        Assert.True(ComplexScript.ContainsIndic(text));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("Asha Patel")]
    [InlineData("₹ 12,34,567.00")]
    public void Latin_text_is_not_indic(string? text)
    {
        Assert.Equal(IndicScript.None, ComplexScript.Detect(text));
        Assert.False(ComplexScript.ContainsIndic(text));
    }

    [Fact]
    public void Mixed_text_reports_the_indic_script()
    {
        Assert.Equal(IndicScript.Devanagari, ComplexScript.Detect("Name: आशा"));
    }

    [Fact]
    public void Every_script_has_a_noto_font_first()
    {
        foreach (var script in Enum.GetValues<IndicScript>().Where(s => s != IndicScript.None))
            Assert.StartsWith("Noto Sans ", ComplexScript.PreferredFamilies(script)[0]);

        Assert.Empty(ComplexScript.PreferredFamilies(IndicScript.None));
    }
}
