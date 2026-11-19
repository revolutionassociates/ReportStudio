using ReportStudio.Data;
using ReportStudio.Export;

namespace ReportStudio.Cli;

internal static class Program
{
    private static int Main(string[] args)
    {
        DatabaseLoader.Register();   // lets reports that were trusted with --trust read SQL Server, MySQL and PostgreSQL
        return ReportCommand.Run(args, Console.Out, Console.Error);
    }
}
