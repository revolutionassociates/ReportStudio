using System.Data;
using System.Data.Common;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using MySqlConnector;
using Npgsql;
using ReportStudio.Core.Data;
using ReportStudio.Core.Expressions;
using ReportStudio.Core.Model;

namespace ReportStudio.Data;

using Row = Dictionary<string, object?>;

/// <summary>Runs a report's query against SQL Server, MySQL or PostgreSQL. Optional: only apps that need it reference this project.</summary>
public sealed class DatabaseLoader : IDatabaseLoader
{
    private static readonly Regex ParamToken = new(@"(?<![@\w])@([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Compiled);

    /// <summary>Makes DataLoader use this implementation. Call once at application start.</summary>
    public static void Register() => DataLoader.Database = new DatabaseLoader();

    public static DbProviderFactory GetFactory(DbProvider provider) => provider switch
    {
        DbProvider.SqlServer => SqlClientFactory.Instance,
        DbProvider.MySql => MySqlConnectorFactory.Instance,
        DbProvider.PostgreSql => NpgsqlFactory.Instance,
        _ => throw new NotSupportedException($"Provider {provider} is not a database provider.")
    };

    public List<Row> Load(DataSourceDefinition ds, IReadOnlyDictionary<string, object?> parameters)
    {
        using var conn = Open(ds);
        using var cmd = CreateCommand(conn, ds, parameters, schemaOnly: false);
        using var reader = cmd.ExecuteReader();

        var rows = new List<Row>();
        var names = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray();
        while (reader.Read())
        {
            var row = new Row(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < names.Length; i++)
                row[names[i]] = Expr.Normalize(reader.IsDBNull(i) ? null : reader.GetValue(i));
            rows.Add(row);
        }
        return rows;
    }

    public List<string> GetFieldNames(DataSourceDefinition ds)
    {
        using var conn = Open(ds);
        using var cmd = CreateCommand(conn, ds, new Dictionary<string, object?>(), schemaOnly: true);
        using var reader = cmd.ExecuteReader(CommandBehavior.SchemaOnly);
        return Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToList();
    }

    public void TestConnection(DataSourceDefinition ds)
    {
        using var conn = Open(ds);
    }

    private static DbConnection Open(DataSourceDefinition ds)
    {
        var conn = GetFactory(ds.Provider).CreateConnection()
                   ?? throw new InvalidOperationException("Could not create a database connection.");
        conn.ConnectionString = ds.ConnectionString;
        conn.Open();
        return conn;
    }

    private static DbCommand CreateCommand(DbConnection conn, DataSourceDefinition ds,
        IReadOnlyDictionary<string, object?> parameters, bool schemaOnly)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = ds.Query;
        cmd.CommandTimeout = ds.CommandTimeoutSeconds;

        // Bind ONLY parameters that the SQL text references, always as real DB parameters (never concatenated).
        var lookup = new Dictionary<string, object?>(parameters, StringComparer.OrdinalIgnoreCase);
        foreach (var name in ParamToken.Matches(ds.Query).Select(m => m.Groups[1].Value)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!lookup.TryGetValue(name, out var value) && !schemaOnly)
                throw new InvalidOperationException(
                    $"The query uses @{name} but no report parameter with that name was supplied.");
            var p = cmd.CreateParameter();
            p.ParameterName = "@" + name;
            p.Value = value ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }
        return cmd;
    }
}
