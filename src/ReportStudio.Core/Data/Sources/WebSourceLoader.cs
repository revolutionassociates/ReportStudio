using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using ReportStudio.Core.Model;

namespace ReportStudio.Core.Data.Sources;

using Row = Dictionary<string, object?>;

/// <summary>
/// Reads the rows of a REST or OData address (requirement 40). The answer must be JSON. Safety rules, all on by default:
/// https only; addresses of this computer and the local network refused (checked on the address the name really resolves to, so a
/// name that points inside cannot get through); no user name or password in the address; redirects followed a few times and checked
/// again each time, with headers sent only to the first server; the answer limited in size and time; error messages never show
/// the address or the headers, which may hold keys.
/// </summary>
public static class WebSourceLoader
{
    public const int MaxPages = 100;
    public const int MaxRedirects = 3;

    private static readonly Regex Token = new(@"\{([A-Za-z_][\w.]*)\}", RegexOptions.Compiled);
    private static readonly Regex HeaderName = new(@"^[A-Za-z0-9!#$%&'*+.^_`|~-]+$", RegexOptions.Compiled);
    private static readonly HashSet<string> BlockedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Content-Length", "Transfer-Encoding", "Connection", "Upgrade", "Expect", "TE", "Trailer"
    };

    public static List<Row> Load(DataSourceDefinition ds, IReadOnlyDictionary<string, object?> parameters, SourceSettings settings) =>
        Read(ds, parameters, settings, SourceLimits.MaxRows, firstPageOnly: false);

    /// <summary>Checks the address answers with usable JSON; throws a message the user can act on.</summary>
    public static void Test(DataSourceDefinition ds, IReadOnlyDictionary<string, object?> parameters, SourceSettings settings) =>
        Read(ds, parameters, settings, 1, firstPageOnly: true);

    /// <summary>The first rows of the first page (for finding field names and types). Reads the network: only on the user's request.</summary>
    public static List<Row> Sample(DataSourceDefinition ds, IReadOnlyDictionary<string, object?> parameters, SourceSettings settings) =>
        Read(ds, parameters, settings, FileSourceLoader.SampleRows, firstPageOnly: true).Take(FileSourceLoader.SampleRows).ToList();

    /// <summary>The field names of the first rows of the first page. Reads the network: only on the user's request (a Test or Detect button).</summary>
    public static List<string> GetFieldNames(DataSourceDefinition ds, IReadOnlyDictionary<string, object?> parameters, SourceSettings settings)
    {
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in Read(ds, parameters, settings, FileSourceLoader.SampleRows, firstPageOnly: true).Take(FileSourceLoader.SampleRows))
            foreach (var name in row.Keys)
                if (seen.Add(name)) names.Add(name);
        return names;
    }

    private static List<Row> Read(DataSourceDefinition ds, IReadOnlyDictionary<string, object?> parameters, SourceSettings settings,
        int maxRows, bool firstPageOnly)
    {
        if (!ds.Provider.IsWeb()) throw new InvalidOperationException($"{ds.Provider} is not a web source.");
        var access = settings.Access;
        if (!access.AllowWebSources) throw new SourceException(SourceAccess.WebOffMessage);

        var first = ParseAddress(BuildAddress(ds.Source, parameters), access);
        var headers = ParseHeaders(ds.Headers);
        var odata = ds.Provider == DbProvider.OData;

        using var client = CreateClient(access);
        var rows = new List<Row>();
        var uri = first;
        for (int page = 1; ; page++)
        {
            var bytes = Fetch(client, uri, first, headers, access);

            var selector = ds.Selector;
            string? next = null;
            if (odata) ReadODataPage(bytes, ref selector, out next);

            var wanted = firstPageOnly ? int.MaxValue : maxRows - rows.Count;
            rows.AddRange(JsonSource.Read(new MemoryStream(bytes), selector, wanted));

            if (firstPageOnly || next == null || rows.Count >= maxRows) break;
            if (page >= MaxPages) throw new SourceException($"The web source has more than {MaxPages} pages; only {MaxPages} can be read.");
            uri = NextPage(uri, next, first, access);
        }
        if (firstPageOnly && rows.Count > maxRows) rows.RemoveRange(maxRows, rows.Count - maxRows);
        return rows;
    }

    // ---- the address ----

    /// <summary>Puts the report's parameters into the address: {Name} becomes the parameter's value, written so it cannot change the shape of the address.</summary>
    public static string BuildAddress(string? source, IReadOnlyDictionary<string, object?> parameters)
    {
        var text = (source ?? "").Trim();
        if (text.Length == 0) throw new SourceException("The data source has no web address.");
        return Token.Replace(text, m =>
        {
            var name = m.Groups[1].Value;
            var key = parameters.Keys.FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));
            if (key == null) throw new SourceException($"The web address uses {{{name}}} but the report has no parameter of that name.");
            return Uri.EscapeDataString(Format(parameters[key]));
        });
    }

    private static string Format(object? value) => value switch
    {
        null => "",
        string s => s,
        bool b => b ? "true" : "false",
        DateTime d => d.TimeOfDay == TimeSpan.Zero ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : d.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""
    };

    /// <summary>Checks an address against the rules above (apart from what a name resolves to, which is checked when connecting).</summary>
    public static Uri ParseAddress(string address, SourceAccess access)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri))
            throw new SourceException("The web address is not valid. It must start with https://.");
        Guard(uri, access);
        return uri;
    }

    private static void Guard(Uri uri, SourceAccess access)
    {
        if (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && access.AllowPrivateAddresses))
            throw new SourceException("Web sources must use https://.");
        if (uri.UserInfo.Length > 0)
            throw new SourceException("A user name or password in the web address is not allowed. Send keys in a header instead.");
        if (access.AllowPrivateAddresses) return;

        var host = uri.IdnHost.Trim('[', ']');
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
            throw new SourceException(PrivateMessage);
        if (IPAddress.TryParse(host, out var ip) && IsPrivate(ip)) throw new SourceException(PrivateMessage);
    }

    private const string PrivateMessage =
        "The web address points to this computer or to the local network, which reports are not allowed to read here.";

    private static Uri NextPage(Uri current, string link, Uri first, SourceAccess access)
    {
        if (!Uri.TryCreate(current, link, out var next)) throw new SourceException("The web source gave a next-page address that is not valid.");
        if (!SameServer(next, first)) throw new SourceException("The web source's next page is on a different server, which is not followed.");
        Guard(next, access);
        return next;
    }

    private static bool SameServer(Uri a, Uri b) =>
        a.Scheme == b.Scheme && a.Port == b.Port && string.Equals(a.IdnHost, b.IdnHost, StringComparison.OrdinalIgnoreCase);

    // ---- the request ----

    /// <summary>Header lines "Name: value". Blank lines are skipped; a name that is not a header name or that may not be set is refused.</summary>
    public static List<(string Name, string Value)> ParseHeaders(string? text)
    {
        var list = new List<(string, string)>();
        foreach (var raw in (text ?? "").Split('\n'))
        {
            var line = raw.Trim('\r', ' ', '\t');
            if (line.Length == 0) continue;
            int colon = line.IndexOf(':');
            if (colon <= 0) throw new SourceException("A header must be written as 'Name: value'.");
            var name = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (!HeaderName.IsMatch(name)) throw new SourceException($"'{name}' is not a valid header name.");
            if (BlockedHeaders.Contains(name)) throw new SourceException($"The header {name} cannot be set.");
            list.Add((name, value));
        }
        return list;
    }

    private static HttpClient CreateClient(SourceAccess access)
    {
        HttpMessageHandler handler = access.HttpHandler ?? new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
            UseProxy = false,                    // a proxy would do the name lookup, out of sight of the address check
            UseCookies = false,
            ConnectTimeout = access.WebTimeout,
            ConnectCallback = (context, token) => ConnectChecked(context, access, token)
        };
        return new HttpClient(handler, disposeHandler: access.HttpHandler == null) { Timeout = access.WebTimeout };
    }

    private static byte[] Fetch(HttpClient client, Uri uri, Uri first, List<(string Name, string Value)> headers, SourceAccess access)
    {
        var clock = Stopwatch.StartNew();
        for (int hop = 0; ; hop++)
        {
            Guard(uri, access);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            request.Headers.TryAddWithoutValidation("User-Agent", "ReportStudio");
            if (SameServer(uri, first))                       // keys go to the server the report names, never to where a redirect leads
                foreach (var (name, value) in headers) request.Headers.TryAddWithoutValidation(name, value);

            using var response = Send(client, request, access);
            var status = (int)response.StatusCode;

            if (status is >= 300 and < 400 && response.Headers.Location != null)
            {
                if (hop >= MaxRedirects) throw new SourceException("The web source redirected too many times.");
                var target = response.Headers.Location;
                uri = target.IsAbsoluteUri ? target : new Uri(uri, target);
                continue;
            }
            if (!response.IsSuccessStatusCode)
                throw new SourceException("The web source answered " + status +
                    (string.IsNullOrWhiteSpace(response.ReasonPhrase) ? "" : " " + response.ReasonPhrase) + ".");

            return ReadBody(response, access, clock);
        }
    }

    private static HttpResponseMessage Send(HttpClient client, HttpRequestMessage request, SourceAccess access)
    {
        using var cts = new CancellationTokenSource(access.WebTimeout);
        try
        {
            // On a pool thread so a window's message loop can never be the one waiting for the answer.
            return Task.Run(() => client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token)).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            throw new SourceException($"The web source did not answer within {access.WebTimeout.TotalSeconds:0} seconds.");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or SocketException)
        {
            for (Exception? e = ex; e != null; e = e.InnerException)
                if (e is SourceException known) throw known;
            throw new SourceException("The web source could not be reached: " + (ex.InnerException?.Message ?? ex.Message), ex);
        }
    }

    private static byte[] ReadBody(HttpResponseMessage response, SourceAccess access, Stopwatch clock)
    {
        if (response.Content.Headers.ContentLength > SourceLimits.MaxBytes) throw TooBig();

        using var body = new MemoryStream();
        try
        {
            using var stream = response.Content.ReadAsStream();
            var buffer = new byte[81920];
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (body.Length + read > SourceLimits.MaxBytes) throw TooBig();
                if (clock.Elapsed > access.WebTimeout) throw new SourceException($"The web source did not finish within {access.WebTimeout.TotalSeconds:0} seconds.");
                body.Write(buffer, 0, read);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            throw new SourceException("The web source's answer was cut off: " + ex.Message, ex);
        }
        return body.ToArray();
    }

    private static SourceException TooBig() =>
        new($"The web source's answer is larger than the {SourceLimits.MaxBytes / (1024 * 1024)} MB that can be read.");

    // ---- OData ----

    /// <summary>OData answers are {"value":[...],"@odata.nextLink":"..."}. With no selector the rows are the "value" list; a single entity has none and is one row.</summary>
    private static void ReadODataPage(byte[] bytes, ref string selector, out string? next)
    {
        next = null;
        try
        {
            using var doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = SourceLimits.MaxDepth });
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return;
            foreach (var p in doc.RootElement.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.String &&
                    (p.NameEquals("@odata.nextLink") || p.NameEquals("odata.nextLink")))
                    next = p.Value.GetString();
            if (string.IsNullOrWhiteSpace(selector) && doc.RootElement.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array)
                selector = "value";
        }
        catch (JsonException ex)
        {
            throw new SourceException("The web source did not answer with valid JSON: " + ex.Message, ex);
        }
    }

    // ---- connecting ----

    private static async ValueTask<Stream> ConnectChecked(SocketsHttpConnectionContext context, SourceAccess access, CancellationToken token)
    {
        var host = context.DnsEndPoint.Host;
        var addresses = await Dns.GetHostAddressesAsync(host, token).ConfigureAwait(false);
        if (addresses.Length == 0) throw new SourceException("The web address's server name was not found.");
        if (!access.AllowPrivateAddresses && addresses.Any(IsPrivate)) throw new SourceException(PrivateMessage);

        Exception? last = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), token).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);   // the very address that was checked
            }
            catch (Exception ex) when (ex is SocketException)
            {
                socket.Dispose();
                last = ex;
            }
        }
        throw new SourceException("The web source could not be reached: " + last?.Message, last!);
    }

    /// <summary>True for an address that is not on the public internet: this computer, the local network, link-local, shared, multicast and reserved ranges.</summary>
    public static bool IsPrivate(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return b[0] == 0 || b[0] == 10 || b[0] == 127
                || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)            // shared address space (carrier-grade NAT)
                || (b[0] == 169 && b[1] == 254)                          // link-local, including cloud metadata services
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 192 && b[1] == 0 && b[2] == 0)
                || (b[0] == 198 && (b[1] == 18 || b[1] == 19))
                || b[0] >= 224;                                          // multicast, reserved, broadcast
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.IPv6None) || address.Equals(IPAddress.IPv6Any)) return true;
            if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast) return true;
            var b = address.GetAddressBytes();
            if ((b[0] & 0xFE) == 0xFC) return true;                      // unique local fc00::/7
            if (b[0] == 0x20 && b[1] == 0x02) return IsPrivate(new IPAddress(b[2..6]));   // 6to4 carries an IPv4 address
            bool nat64 = b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xFF && b[3] == 0x9B && b[4..12].All(x => x == 0);
            if (nat64) return IsPrivate(new IPAddress(b[12..16]));       // 64:ff9b::/96 carries an IPv4 address
            return false;
        }
        return true;   // an address family that is not understood is not trusted
    }
}
