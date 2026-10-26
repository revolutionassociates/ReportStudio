using ReportStudio.Core.Model;

namespace ReportStudio.Core.Data;

using Row = Dictionary<string, object?>;

/// <summary>
/// Plug-in point for database access. Core contains no database drivers; the optional ReportStudio.Data
/// project implements this for SQL Server, MySQL and PostgreSQL and registers itself.
/// </summary>
public interface IDatabaseLoader
{
    List<Row> Load(DataSourceDefinition ds, IReadOnlyDictionary<string, object?> parameters);
    List<string> GetFieldNames(DataSourceDefinition ds);
    void TestConnection(DataSourceDefinition ds);
}

/// <summary>Resolves a report's rows: built-in sample data, the host (not here), or a registered database loader.</summary>
public static class DataLoader
{
    /// <summary>Set by ReportStudio.Data (DatabaseLoader.Register). Stays null when only Core is referenced.</summary>
    public static IDatabaseLoader? Database { get; set; }

    public static List<Row> Load(DataSourceDefinition ds, IReadOnlyDictionary<string, object?> parameters)
    {
        if (ds.Provider == DbProvider.Sample) return SampleData.Generate();
        if (ds.Provider == DbProvider.Host)
            throw new InvalidOperationException("The Host data source has no database; the host supplies the rows.");
        if (string.IsNullOrWhiteSpace(ds.Query)) return new List<Row>();
        return Require(ds).Load(ds, parameters);
    }

    /// <summary>Column names the query returns (used by the designer's field explorer). No data is read.</summary>
    public static List<string> GetFieldNames(DataSourceDefinition ds)
    {
        if (ds.Provider == DbProvider.Sample) return SampleData.FieldNames.ToList();
        // Host reports (and database reports without a query) use the schema stored in the file: no database needed.
        if (ds.Provider == DbProvider.Host || string.IsNullOrWhiteSpace(ds.Query))
            return ds.Fields.Select(f => f.Name).ToList();
        return Require(ds).GetFieldNames(ds);
    }

    public static void TestConnection(DataSourceDefinition ds)
    {
        if (ds.Provider is DbProvider.Sample or DbProvider.Host) return;
        Require(ds).TestConnection(ds);
    }

    private static IDatabaseLoader Require(DataSourceDefinition ds) =>
        Database ?? throw new InvalidOperationException(
            $"This report uses the {ds.Provider} data source, but no database support is installed. " +
            "Reference ReportStudio.Data and call DatabaseLoader.Register(), or use the Host data source.");
}
