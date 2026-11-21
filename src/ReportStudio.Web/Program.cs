using System.Text;
using ReportStudio.Core.Server;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using ReportStudio.Data;
using ReportStudio.Export;
using ReportStudio.Web;

// reportstudio-web --hash-password [password]: prints the hash to put in ReportStudio:Access:Users (the password is read from the
// standard input when it is not given, which keeps it out of the shell's history).
if (args.Length >= 1 && args[0] == "--hash-password")
{
    var typed = args.Length >= 2 ? args[1] : Console.In.ReadLine();
    if (string.IsNullOrEmpty(typed) || typed.Length > PasswordHash.MaxPasswordChars)
    {
        Console.Error.WriteLine($"Give a password of 1 to {PasswordHash.MaxPasswordChars} characters.");
        Environment.ExitCode = 2;
        return;
    }
    Console.WriteLine(PasswordHash.Create(typed));
    return;
}

var builder = WebApplication.CreateBuilder(args);
builder.Services.Configure<WebViewerOptions>(builder.Configuration.GetSection("ReportStudio"));
builder.Services.AddSingleton<ReportCatalog>();
builder.Services.AddSingleton(_ => new WebTextMeasurer());
builder.Services.AddSingleton<ReportService>();

// Requirement 51c: users and roles. With no users listed the viewer is open.
builder.Services.Configure<AccessOptions>(builder.Configuration.GetSection("ReportStudio:Access"));
builder.Services.AddSingleton<AccessService>();

// Requirement 50: scheduled reports by e-mail. Off until ReportStudio:Server:Enabled is set.
builder.Services.Configure<ServerOptions>(builder.Configuration.GetSection("ReportStudio:Server"));
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(sp => new SubscriptionStore(sp.GetRequiredService<IOptions<ServerOptions>>().Value.SubscriptionsFile, sp.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton<IMailSender, SmtpMailSender>();
builder.Services.AddSingleton<SubscriptionRunner>();
builder.Services.AddHostedService<SchedulerService>();

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

// Asked for a name and password on every address except /healthz and /admin (which has its own key or admin sign-in).
var access = app.Services.GetRequiredService<AccessService>();   // a broken user entry stops the server here
app.Use(async (context, next) =>
{
    if (!access.Enabled || context.Request.Path == "/healthz" || context.Request.Path.StartsWithSegments("/admin"))
    {
        await next();
        return;
    }

    var check = access.Check(context);
    if (check.Blocked)
    {
        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        context.Response.Headers.RetryAfter = access.LockoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        context.Response.ContentType = "text/plain; charset=utf-8";
        await context.Response.WriteAsync("Too many wrong passwords. Try again later.");
        return;
    }
    if (check.User == null)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate = access.Challenge;
        context.Response.ContentType = "text/plain; charset=utf-8";
        await context.Response.WriteAsync("Sign in to see the reports.");
        return;
    }

    context.Items[AccessService.UserKey] = check.User;
    await next();
});

if (Directory.Exists(fontsFolder))
    app.UseStaticFiles(new StaticFileOptions { FileProvider = new PhysicalFileProvider(fontsFolder), RequestPath = "/fonts" });

// A signed-in viewer sees only the reports the settings give them; a report they may not open is "not found", so its name is not given away.
static bool CanOpen(HttpContext http, string id) => http.Items[AccessService.UserKey] is not AccessUser user || AccessPolicy.CanView(user, id);

static IResult NoSuchReport() => Page(WebPages.Message("Not found", "There is no such report."), 404);

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

app.MapGet("/", (ReportCatalog catalog, HttpContext http) => Page(WebPages.ReportList(catalog.List().Where(e => CanOpen(http, e.Id)).ToList())));

app.MapGet("/r/{id}", (string id, ReportService service, HttpContext http) =>
{
    if (!CanOpen(http, id)) return NoSuchReport();
    var view = service.Open(id);
    if (view.Status != RunStatus.Ok || view.Definition == null) return Problem(view);
    if (view.Definition.Parameters.Count == 0) return Results.Redirect("/r/" + Uri.EscapeDataString(id) + "/view");
    return Page(WebPages.ParameterForm(id, view.Definition));
});

app.MapGet("/r/{id}/view", async (string id, HttpRequest request, ReportService service, CancellationToken ct) =>
{
    if (!CanOpen(request.HttpContext, id)) return NoSuchReport();
    var view = await service.RunAsync(id, ParameterValues(request), ct);
    if (view.Status != RunStatus.Ok || view.Result == null) return Problem(view);
    return Page(WebPages.WithToolbar(HtmlExporter.Build(view.Result), id, view.Result, fontsFolder, request.QueryString.Value ?? ""));
});

app.MapGet("/r/{id}/download/{format}", async (string id, string format, HttpRequest request, ReportService service, CancellationToken ct) =>
{
    if (!CanOpen(request.HttpContext, id)) return NoSuchReport();
    var chosen = ReportDownloads.Find(format);
    if (chosen == null) return Problem(new ReportView(RunStatus.NotFound, "There is no such download format."));

    var view = await service.RunAsync(id, ParameterValues(request), ct);
    if (view.Status != RunStatus.Ok || view.Result == null) return Problem(view);

    var file = service.Render(chosen, view.Result, id);
    if (file.Bytes == null) return Problem(new ReportView(RunStatus.Failed, file.Message));
    return Results.File(file.Bytes, chosen.ContentType, id + "." + chosen.Extension);
});

app.MapAdminApi();

app.Run();

/// <summary>Makes the program reachable for the tests.</summary>
public partial class Program
{
}
