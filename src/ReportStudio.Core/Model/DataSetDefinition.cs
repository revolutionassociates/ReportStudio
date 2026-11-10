namespace ReportStudio.Core.Model;

/// <summary>
/// A named set of rows besides the report's main data (requirement 39): a price list, an employee master, a list of departments.
/// It is read with the Lookup functions or joined onto the main rows. Where the rows come from is set like the main data source;
/// a Host data set is supplied by the host application under the same name (ReportOptions.DataSets).
/// </summary>
public class DataSetDefinition
{
    public string Name { get; set; } = "";
    public DataSourceDefinition DataSource { get; set; } = new() { Provider = DbProvider.Host };

    public void Normalize()
    {
        Name = (Name ?? "").Trim();
        DataSource ??= new() { Provider = DbProvider.Host };
        // A Host data set has no database: what a file carries as query or connection is dropped, as for the main source.
        if (DataSource.Provider == DbProvider.Host)
        {
            DataSource.Query = "";
            DataSource.ConnectionString = "";
            DataSource.Source = "";
        }
    }
}

public enum JoinKind
{
    /// <summary>Every main row stays; a row with no match gets empty fields from the data set.</summary>
    Left,
    /// <summary>Only the main rows that have a match stay.</summary>
    Inner
}

/// <summary>
/// Adds the fields of a data set to the main rows: each main row is matched to the data set's rows whose field equals the main row's field.
/// A main row with several matches becomes several rows, as in SQL.
/// </summary>
public class JoinDefinition
{
    /// <summary>Name of the data set to join.</summary>
    public string DataSet { get; set; } = "";

    /// <summary>The field of the main rows to match, e.g. EmpCode.</summary>
    public string LeftField { get; set; } = "";

    /// <summary>The field of the data set to match. Empty = the same name as the main field.</summary>
    public string RightField { get; set; } = "";

    public JoinKind Kind { get; set; } = JoinKind.Left;

    /// <summary>Put in front of the data set's field names ("Dept_" makes Name into Dept_Name). Without one, a field the main rows already have keeps its own value.</summary>
    public string Prefix { get; set; } = "";

    public void Normalize()
    {
        DataSet = (DataSet ?? "").Trim();
        LeftField = (LeftField ?? "").Trim();
        RightField = (RightField ?? "").Trim();
        Prefix = (Prefix ?? "").Trim();
    }

    [System.Text.Json.Serialization.JsonIgnore]
    public string EffectiveRightField => RightField.Length > 0 ? RightField : LeftField;
}
