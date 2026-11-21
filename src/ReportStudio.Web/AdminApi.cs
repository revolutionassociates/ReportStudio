using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using ReportStudio.Core.Server;

namespace ReportStudio.Web;

/// <summary>
/// The administrator's JSON interface to the subscriptions (requirement 50): list, read, add, change, remove, and run one now. It exists only
/// when the server is switched on and an admin key of at least <see cref="ServerOptions.MinAdminKeyChars"/> characters is set; without that every
/// address under /admin answers 404, as if it were not there. The key travels in the X-Admin-Key header and is compared in constant time.
/// Visitors of the viewer have no part in this: they cannot see or create subscriptions.
/// </summary>
public static class AdminApi
{
    public const string KeyHeader = "X-Admin-Key";
    private const int MaxBodyBytes = 64 * 1024;

    public static void MapAdminApi(this WebApplication app)
    {
        // The key is checked in the pipeline, before the endpoint reads any of the request body.
        app.Use(Guard);

        var admin = app.MapGroup("/admin/api");

        admin.MapGet("/subscriptions", (SubscriptionStore store) => Results.Ok(store.All().Select(View)));

        admin.MapGet("/subscriptions/{id}", (string id, SubscriptionStore store) =>
            store.Get(id) is { } s ? Results.Ok(View(s)) : Results.NotFound(Error("There is no such subscription.")));

        admin.MapPost("/subscriptions", (Subscription body, SubscriptionStore store, SubscriptionRunner runner) =>
        {
            body.Normalize();
            var refusal = runner.Refusal(body);
            if (refusal != null) return Results.BadRequest(Error(refusal));
            try
            {
                var added = store.Add(body);
                return Results.Created("/admin/api/subscriptions/" + added.Id, View(added));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(Error(ex.Message));
            }
        });

        admin.MapPut("/subscriptions/{id}", (string id, Subscription body, SubscriptionStore store, SubscriptionRunner runner) =>
        {
            body.Normalize();
            body.Id = id;
            var refusal = runner.Refusal(body);
            if (refusal != null) return Results.BadRequest(Error(refusal));
            try
            {
                return store.Update(body) ? Results.Ok(View(store.Get(id)!)) : Results.NotFound(Error("There is no such subscription."));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(Error(ex.Message));
            }
        });

        admin.MapDelete("/subscriptions/{id}", (string id, SubscriptionStore store) =>
            store.Remove(id) ? Results.NoContent() : Results.NotFound(Error("There is no such subscription.")));

        // Runs it now, whatever its schedule says, and records the result like a scheduled run would.
        admin.MapPost("/subscriptions/{id}/run", async (string id, SubscriptionStore store, SubscriptionRunner runner, CancellationToken ct) =>
        {
            var s = store.Get(id);
            if (s == null) return Results.NotFound(Error("There is no such subscription."));
            var outcome = await runner.RunAsync(s, ct);
            store.RecordRun(id, outcome.Ok, outcome.Ok ? null : outcome.Message);
            return Results.Json(new { ok = outcome.Ok, message = outcome.Message }, statusCode: outcome.Ok ? 200 : 422);
        });
    }

    private static object Error(string message) => new { error = message };

    // What the interface shows of a subscription: all of it, plus a plain-words schedule. The mail password is never part of it.
    private static object View(Subscription s) => new
    {
        s.Id, s.Name, s.ReportId, s.Parameters, s.Format, s.Recipients, s.Subject, s.Body, s.Schedule, s.Enabled,
        scheduleText = s.Schedule.Describe(), s.NextRun, s.LastRun, s.LastStatus, s.LastError
    };

    private static async Task Guard(HttpContext http, Func<Task> next)
    {
        if (!http.Request.Path.StartsWithSegments("/admin"))
        {
            await next();
            return;
        }

        var options = http.RequestServices.GetRequiredService<IOptions<ServerOptions>>().Value;
        var access = http.RequestServices.GetRequiredService<AccessService>();
        http.Response.Headers.CacheControl = "no-store";

        // Open to scripts that hold the admin key, and to signed-in users whose role is Admin (requirement 51c).
        if (!options.Enabled || !(options.AdminEnabled || access.Enabled))
        {
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        bool keyOk = options.AdminEnabled && KeyMatches(http.Request.Headers[KeyHeader].ToString(), options.AdminKey);
        if (!keyOk)
        {
            var check = access.Enabled ? access.Check(http) : new AccessCheck(null, false);
            if (check.Blocked)
            {
                http.Response.Headers.RetryAfter = access.LockoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
                await Results.Json(Error("Too many wrong passwords. Try again later."), statusCode: 429).ExecuteAsync(http);
                return;
            }
            if (check.User == null || !AccessPolicy.IsAdmin(check.User))
            {
                await Results.Json(Error("The admin key is missing or wrong, or the user is not an administrator."), statusCode: 401).ExecuteAsync(http);
                return;
            }
        }
        if (http.Request.ContentLength > MaxBodyBytes)
        {
            await Results.Json(Error("The request is too big."), statusCode: 413).ExecuteAsync(http);
            return;
        }

        var limit = http.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (limit is { IsReadOnly: false }) limit.MaxRequestBodySize = MaxBodyBytes;
        await next();
    }

    /// <summary>Compares the keys without stopping at the first difference. Hashing first makes both the same length.</summary>
    public static bool KeyMatches(string given, string expected)
    {
        if (string.IsNullOrEmpty(given) || string.IsNullOrEmpty(expected)) return false;
        var a = SHA256.HashData(Encoding.UTF8.GetBytes(given));
        var b = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        return CryptographicOperations.FixedTimeEquals(a, b);
    }
}
