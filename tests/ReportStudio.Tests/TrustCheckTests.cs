using ReportStudio.Core.Data.Sources;
using ReportStudio.Core.Model;
using ReportStudio.Core.Samples;
using Xunit;

namespace ReportStudio.Tests;

/// <summary>What a program without a person at the console must ask before it runs a report file (requirements 48 and 49).</summary>
public class TrustCheckTests
{
    private static ReportDefinition WithSource(DbProvider provider, string query = "", string source = "")
    {
        var def = SampleReport.Create();
        def.DataSource.Provider = provider;
        def.DataSource.Query = query;
        def.DataSource.Source = source;
        return def;
    }

    [Fact]
    public void The_sample_report_needs_no_trust()
    {
        Assert.False(TrustCheck.NeedsDatabaseTrust(SampleReport.Create()));
        Assert.Empty(TrustCheck.Describe(SampleReport.Create(), 300));
    }

    [Theory]
    [InlineData(DbProvider.SqlServer)]
    [InlineData(DbProvider.MySql)]
    [InlineData(DbProvider.PostgreSql)]
    public void A_database_query_needs_trust(DbProvider provider)
    {
        var def = WithSource(provider, "SELECT 1 AS Marker");
        Assert.True(TrustCheck.NeedsDatabaseTrust(def));
        Assert.Contains("SELECT 1 AS Marker", string.Join("\n", TrustCheck.Describe(def, 300)));
    }

    [Fact]
    public void A_database_report_without_a_query_needs_no_trust()
    {
        Assert.False(TrustCheck.NeedsDatabaseTrust(WithSource(DbProvider.SqlServer)));
    }

    [Theory]
    [InlineData(DbProvider.Json)]
    [InlineData(DbProvider.Xml)]
    [InlineData(DbProvider.Csv)]
    [InlineData(DbProvider.Excel)]
    [InlineData(DbProvider.Rest)]
    [InlineData(DbProvider.OData)]
    public void A_file_or_web_source_is_not_a_database_query(DbProvider provider)
    {
        var def = WithSource(provider, source: "something");
        Assert.False(TrustCheck.NeedsDatabaseTrust(def));
        Assert.Single(TrustCheck.Describe(def, 300));   // it is still listed for a log
    }

    [Fact]
    public void A_drop_down_list_read_from_the_database_needs_trust()
    {
        var def = WithSource(DbProvider.SqlServer);
        def.Parameters.Add(new ReportParameter { Name = "Region", ChoicesQuery = "SELECT Region FROM Regions" });

        Assert.True(TrustCheck.NeedsDatabaseTrust(def));
        Assert.Contains("SELECT Region FROM Regions", string.Join("\n", TrustCheck.Describe(def, 300)));
    }

    [Fact]
    public void A_data_set_that_reads_a_database_needs_trust()
    {
        var def = SampleReport.Create();
        var set = new DataSetDefinition { Name = "Lines" };
        set.DataSource.Provider = DbProvider.PostgreSql;
        set.DataSource.Query = "SELECT * FROM lines";
        def.DataSets.Add(set);

        Assert.True(TrustCheck.NeedsDatabaseTrust(def));
        Assert.Contains("Data set Lines", string.Join("\n", TrustCheck.Describe(def, 300)));
    }

    [Fact]
    public void A_data_set_that_reads_a_file_does_not_need_database_trust()
    {
        var def = SampleReport.Create();
        var set = new DataSetDefinition { Name = "Lines" };
        set.DataSource.Provider = DbProvider.Csv;
        set.DataSource.Source = "lines.csv";
        def.DataSets.Add(set);

        Assert.False(TrustCheck.NeedsDatabaseTrust(def));
    }

    [Fact]
    public void A_long_query_is_cut_in_the_description()
    {
        var def = WithSource(DbProvider.SqlServer, "SELECT " + new string('x', 2000));
        var text = string.Join("\n", TrustCheck.Describe(def, 100));
        Assert.True(text.Length < 1000);
    }
}
