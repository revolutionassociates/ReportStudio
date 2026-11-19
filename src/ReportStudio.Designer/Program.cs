namespace ReportStudio.Designer;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ReportStudio.Data.DatabaseLoader.Register();
        ApplicationConfiguration.Initialize();
        DesignerUi.Initialize();   // colour theme and language (requirement 47)
        Application.Run(new MainForm(args.FirstOrDefault()));
    }
}
