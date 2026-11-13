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

    public static List<Row> Load(DataSourceDefinition ds, IReadOnlyDictionary<string, object?> parameters, Sources.SourceSettings? sources = null)
    {
        // A source that asks for it is read through the host's cache (requirement 41); the cache's key includes what the run may read.
        if (sources?.Cache is { } cache) return cache.GetOrLoad(ds, parameters, sources, () => LoadUncached(ds, parameters, sources));
        return LoadUncached(ds, parameters, sources);
    }

    private static List<Row> LoadUncached(DataSourceDefinition ds, IReadOnlyDictionary<string, object?> parameters, Sources.SourceSettings? sources)
    {
        if (ds.Provider == DbProvider.Sample) return SampleData.Generate();
        if (ds.Provider == DbProvider.Host)
            throw new InvalidOperationException("The Host data source has no database; the host supplies the rows.");
        if (ds.Provider.IsFile()) return Sources.FileSourceLoader.Load(ds, sources ?? new Sources.SourceSettings());
        if (ds.Provider.IsWeb()) return Sources.WebSourceLoader.Load(ds, parameters, sources ?? new Sources.SourceSettings());
        if (string.IsNullOrWhiteSpace(ds.Query)) return new List<Row>();
        return Require(ds).Load(ds, parameters);
    }

    /// <summary>Column names the query returns (used by the designer's field explorer). No data is read.</summary>
    public static List<string> GetFieldNames(DataSourceDefinition ds, Sources.SourceSettings? sources = null)
    {
        if (ds.Provider == DbProvider.Sample) return SampleData.FieldNames.ToList();
        if (ds.Provider.IsFile() && !string.IsNullOrWhiteSpace(ds.Source) && (sources?.Access.AllowFileSources ?? true))
            return Sources.FileSourceLoader.GetFieldNames(ds, sources ?? new Sources.SourceSettings());
        // A web address is never read just to list fields: that is DetectFieldNames, on the user's request.
        if (ds.Provider.IsFile() || ds.Provider.IsWeb()) return ds.Fields.Select(f => f.Name).ToList();
        // Host reports (and database reports without a query) use the schema stored in the file: no database needed.
        if (ds.Provider == DbProvider.Host || string.IsNullOrWhiteSpace(ds.Query))
            return ds.Fields.Select(f => f.Name).ToList();
        return Require(ds).GetFieldNames(ds);
    }

    /// <summary>Checks the source can be read. A web source is asked once (first page only); {Name} in its address needs <paramref name="parameters"/>.</summary>
    public static void TestConnection(DataSourceDefinition ds, Sources.SourceSettings? sources = null,
        IReadOnlyDictionary<string, object?>? parameters = null)
    {
        if (ds.Provider is DbProvider.Sample or DbProvider.Host) return;
        if (ds.Provider.IsFile()) { Sources.FileSourceLoader.Test(ds, sources ?? new Sources.SourceSettings()); return; }
        if (ds.Provider.IsWeb()) { Sources.WebSourceLoader.Test(ds, parameters ?? new Dictionary<string, object?>(), sources ?? new Sources.SourceSettings()); return; }
        Require(ds).TestConnection(ds);
    }

    /// <summary>
    /// The fields (names and types) of a file or web source, found from its first rows. Reads the file or the network: only for a user's
    /// click on "Test and load fields". Other sources give their names as Text.
    /// </summary>
    public static List<FieldDefinition> DetectFields(DataSourceDefinition ds, Sources.SourceSettings? sources = null,
        IReadOnlyDictionary<string, object?>? parameters = null)
    {
        sources ??= new Sources.SourceSettings();
        if (ds.Provider.IsFile()) return Sources.FieldSampler.Infer(Sources.FileSourceLoader.Sample(ds, sources));
        if (ds.Provider.IsWeb())
            return Sources.FieldSampler.Infer(Sources.WebSourceLoader.Sample(ds, parameters ?? new Dictionary<string, object?>(), sources));
        return GetFieldNames(ds, sources).Select(n => new FieldDefinition { Name = n }).ToList();
    }

    /// <summary>Reads the first rows of a web source to find its field names. Only for a user's click on "Detect fields": it uses the network.</summary>
    public static List<string> DetectFieldNames(DataSourceDefinition ds, Sources.SourceSettings? sources = null,
        IReadOnlyDictionary<string, object?>? parameters = null) =>
        ds.Provider.IsWeb()
            ? Sources.WebSourceLoader.GetFieldNames(ds, parameters ?? new Dictionary<string, object?>(), sources ?? new Sources.SourceSettings())
            : GetFieldNames(ds, sources);

    private static IDatabaseLoader Require(DataSourceDefinition ds) =>
        Database ?? throw new InvalidOperationException(
            $"This report uses the {ds.Provider} data source, but no database support is installed. " +
            "Reference ReportStudio.Data and call DatabaseLoader.Register(), or use the Host data source.");
}
