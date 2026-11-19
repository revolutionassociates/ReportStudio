using System.Text;
using Microsoft.Extensions.FileProviders;
using ReportStudio.Data;
using ReportStudio.Export;
using ReportStudio.Web;

var builder = WebApplication.CreateBuilder(args);
builder.Services.Configure<WebViewerOptions>(builder.Configuration.GetSection("ReportStudio"));
builder.Services.AddSingleton<ReportCatalog>();
builder.Services.AddSingleton(_ => new WebTextMeasurer());
builder.Services.AddSingleton<ReportService>();

DatabaseLoader.Register();   // only used for reports the server's owner has chosen to trust (ReportStudio:TrustReportQueries)

var app = builder.Build();
var fontsFolder = Path.Combine(AppContext.BaseDirectory, "Fonts");

// The pages carry no script and load nothing from other sites. Report pictures are data: addresses written into the page.
app.Use(async (context, next) =>
{
    var h = context.Response.Headers;
    h["Content-Security-Policy"] = "default-src 'none'; style-src 'unsafe-inline'; img-src data:; font-src 'self'; form-action 'self'; base-uri 'none'; frame-ancestors 'none'";
    h["X-Content-Type-Options"] = "nosniff";
    h["X-Frame-Options"] = "DENY";
    h["Referrer-Policy"] = "no-referrer";
    await next();
});

if (Directory.Exists(fontsFolder))
    app.UseStaticFiles(new StaticFileOptions { FileProvider = new PhysicalFileProvider(fontsFolder), RequestPath = "/fonts" });

static IResult Page(string html, int status = 200) => Results.Text(html, "text/html; charset=utf-8", Encoding.UTF8, status);

static IResult Problem(ReportView view) => Page(WebPages.Message(view.Status switch
{
    RunStatus.NotFound => "Not found",
    RunStatus.BadRequest => "Check the values",
    RunStatus.Refused => "This report is not available",
    RunStatus.Busy => "Busy",
    _ => "The report could not be shown"
}, view.Message), view.HttpStatus);

// What the visitor typed: the query values named p.<Parameter>.
static Dictionary<string, string[]> ParameterValues(HttpRequest request)
{
    var values = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
    foreach (var (key, value) in request.Query)
        if (key.StartsWith("p.", StringComparison.OrdinalIgnoreCase) && key.Length > 2)
            values[key[2..]] = value.Where(v => v != null).Select(v => v!).ToArray();
    return values;
}

app.MapGet("/healthz", () => Results.Text("ok"));

app.MapGet("/", (ReportCatalog catalog) => Page(WebPages.ReportList(catalog.List())));

app.MapGet("/r/{id}", (string id, ReportService service) =>
{
    var view = service.Open(id);
    if (view.Status != RunStatus.Ok || view.Definition == null) return Problem(view);
    if (view.Definition.Parameters.Count == 0) return Results.Redirect("/r/" + Uri.EscapeDataString(id) + "/view");
    return Page(WebPages.ParameterForm(id, view.Definition));
});

app.MapGet("/r/{id}/view", async (string id, HttpRequest request, ReportService service, CancellationToken ct) =>
{
    var view = await service.RunAsync(id, ParameterValues(request), ct);
    if (view.Status != RunStatus.Ok || view.Result == null) return Problem(view);
    return Page(WebPages.WithToolbar(HtmlExporter.Build(view.Result), id, view.Result, fontsFolder, request.QueryString.Value ?? ""));
});

app.MapGet("/r/{id}/download/{format}", async (string id, string format, HttpRequest request, ReportService service, CancellationToken ct) =>
{
    var chosen = ReportDownloads.Find(format);
    if (chosen == null) return Problem(new ReportView(RunStatus.NotFound, "There is no such download format."));

    var view = await service.RunAsync(id, ParameterValues(request), ct);
    if (view.Status != RunStatus.Ok || view.Result == null) return Problem(view);

    var file = service.Render(chosen, view.Result, id);
    if (file.Bytes == null) return Problem(new ReportView(RunStatus.Failed, file.Message));
    return Results.File(file.Bytes, chosen.ContentType, id + "." + chosen.Extension);
});

app.Run();

/// <summary>Makes the program reachable for the tests.</summary>
public partial class Program
{
}
