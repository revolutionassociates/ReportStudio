namespace ReportStudio.Designer;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ReportStudio.Data.DatabaseLoader.Register();
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm(args.FirstOrDefault()));
    }
}
