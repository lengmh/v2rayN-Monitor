using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Numerics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using V2rayNMonitor.Core;

namespace V2rayNMonitor.Server;

public sealed class MonitorServer : IAsyncDisposable
{
    private WebApplication app = null!;
    private readonly string dataDir;
    private readonly bool demo;
    private readonly object settingsGate = new();
    private AppSettings settings;
    private CollectorStatus status;
    private readonly ConcurrentDictionary<string, DateTimeOffset> launches = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> sessions = new();
    private readonly ProviderImports imports;
    private readonly Ledger? legacyLedger;
    public Ledger Ledger => legacyLedger ?? throw new InvalidOperationException("This opt-in instance uses a v2 scope ledger.");
    public ScopeLedger? Scopes { get; }
    public string Address { get; private set; } = "";
    public CancellationToken Stopping => app.Lifetime.ApplicationStopping;
    public AppSettings Settings { get { lock (settingsGate) return settings; } }
    public void SetStatus(CollectorStatus value) => Volatile.Write(ref status, value with { Probes = value.Probes ?? DefaultProbeStatus });
    private ProbeCollectionStatus DefaultProbeStatus => Scopes?.SupportsProbes == true
        ? new(true, "waiting", "探测账本已启用，等待有效采样。")
        : new(false, "disabled", "此实例未启用探测历史。接入需配套核心和显式 v3 账本；当前业务采集不受影响。");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private MonitorServer(string directory, bool demoMode, bool useScopeLedger, bool useProbeLedger)
    {
        dataDir = directory; demo = demoMode; Directory.CreateDirectory(directory);
        var zone = TimeZoneInfo.Local.Id;
        if (TimeZoneInfo.TryConvertWindowsIdToIanaId(zone, out var iana)) zone = iana;
        settings = new("", zone, demo);
        var path = Path.Combine(dataDir, "settings.json");
        if (File.Exists(path)) settings = (JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json) ?? settings) with { Demo = demo };
        _ = TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZoneId);
        useScopeLedger |= useProbeLedger;
        if (demoMode && useScopeLedger) throw new ArgumentException("Scope mode cannot use the v1 demo initializer.");
        var historyPath = Path.Combine(directory, "history.db");
        if (useScopeLedger)
        {
            Scopes = File.Exists(historyPath) ? new ScopeLedger(historyPath) : ScopeLedger.CreateNew(historyPath,
                includeProbes: useProbeLedger, includeDisplayMetadata: true);
            if (Scopes.SupportsProbes != useProbeLedger)
            {
                Scopes.Dispose();
                throw new InvalidOperationException("v2 requires --scope-ledger; v3 requires --probe-ledger. Startup never migrates history.");
            }
        }
        else legacyLedger = new(historyPath, includeDisplayMetadata: !File.Exists(historyPath));
        imports = new(Path.Combine(directory, "provider.db"));
        status = new("setup", "选择 v2rayN 数据目录，开始只读采集。", Demo: demo,
            CollectionBasis: useScopeLedger ? "actual_member" : "legacy_proxy", Probes: DefaultProbeStatus);
        if (demo) { Ledger.SeedDemo(DateTimeOffset.UtcNow); status = new("demo", "合成演示数据，不连接真实代理。", Demo: true); }
    }
    public static Task<MonitorServer> StartAsync(string directory, string webRoot, bool demo = false, CancellationToken ct = default,
        bool useScopeLedger = false, bool useProbeLedger = false)
        // The WinForms tray joins this task synchronously; nested framework awaits must not
        // inherit its UI synchronization context (the same applies to Kestrel shutdown).
        => Task.Run(() => StartCoreAsync(directory, webRoot, demo, ct, useScopeLedger, useProbeLedger), ct);
    private static async Task<MonitorServer> StartCoreAsync(string directory, string webRoot, bool demo, CancellationToken ct, bool useScopeLedger, bool useProbeLedger)
    {
        var server = new MonitorServer(directory, demo, useScopeLedger, useProbeLedger);
        try { await server.StartWebAsync(webRoot, ct).ConfigureAwait(false); return server; }
        catch { await server.DisposeAsync().ConfigureAwait(false); throw; }
    }
    public string IssueLaunchUrl()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var token in launches.Where(p => p.Value <= now)) launches.TryRemove(token.Key, out _);
        var value = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        launches[value] = now.AddMinutes(2);
        return Address + "/#" + value;
    }
    public void UpdateSettings(AppSettings value)
    {
        _ = TimeZoneInfo.FindSystemTimeZoneById(value.TimeZoneId);
        if (value.V2rayNPath.Length > 1024) throw new ArgumentException("目录路径过长。");
        if (!string.IsNullOrWhiteSpace(value.V2rayNPath) && !Directory.Exists(value.V2rayNPath)) throw new ArgumentException("找不到该目录。请选择包含 guiConfigs 和 binConfigs 的 v2rayN 数据根目录。");
        lock (settingsGate)
        {
            var next = value with { Demo = demo, V2rayNPath = value.V2rayNPath.Trim() };
            var target = Path.Combine(dataDir, "settings.json"); var temp = target + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(next, Json)); File.Move(temp, target, true); settings = next;
        }
    }
    private async Task StartWebAsync(string webRoot, CancellationToken ct)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { WebRootPath = Path.GetFullPath(webRoot), ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, 0);
            // A CSV byte may be encoded as six JSON bytes (\uXXXX). Keep transport bounded,
            // with room for metadata; ProviderImports still enforces decoded bytes and rows.
            options.Limits.MaxRequestBodySize = 6L * ProviderImports.MaxCsvBytes + 100_000;
        });
        app = builder.Build();
        app.Use(async (context, next) =>
        {
            var request = context.Request; var response = context.Response;
            response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
            response.Headers["X-Content-Type-Options"] = "nosniff";
            response.Headers["Referrer-Policy"] = "no-referrer";
            response.Headers["Cache-Control"] = "no-store";
            if (request.Host.Value != new Uri(Address).Authority || (request.Headers.Origin.Count != 0 && request.Headers.Origin.ToString() != Address) || request.Headers["Sec-Fetch-Site"] == "cross-site")
            { response.StatusCode = 403; return; }
            if (request.Path.StartsWithSegments("/api"))
            {
                if (request.Method != "GET" && request.Headers["X-Monitor-Request"] != "1") { response.StatusCode = 403; return; }
                if (request.Path != "/api/session" && (!request.Cookies.TryGetValue("monitor_session", out var session) || !sessions.TryGetValue(session, out var until) || until <= DateTimeOffset.UtcNow))
                { response.StatusCode = 401; await response.WriteAsJsonAsync(new { error = "会话已过期，请从托盘重新打开页面。" }); return; }
            }
            try { await next(context); }
            catch (Exception ex) when (ex is ArgumentException or FormatException or TimeZoneNotFoundException or InvalidTimeZoneException or OverflowException or Microsoft.VisualBasic.FileIO.MalformedLineException or JsonException or BadHttpRequestException)
            {
                if (!response.HasStarted) { response.StatusCode = 400; await response.WriteAsJsonAsync(new { error = "请求无效。" + SafeError(ex) }); }
            }
            catch (Exception)
            {
                if (!response.HasStarted) { response.StatusCode = 500; await response.WriteAsJsonAsync(new { error = "本地操作失败，原有历史已保留。请检查数据目录权限和磁盘空间，再重试。" }); }
            }
        });
        app.MapPost("/api/session", async (HttpContext context) =>
        {
            var request = await context.Request.ReadFromJsonAsync<SessionRequest>();
            if (request is null || !launches.TryRemove(request.Token ?? "", out var expiry) || expiry <= DateTimeOffset.UtcNow) return Results.Unauthorized();
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            sessions[token] = DateTimeOffset.UtcNow.AddHours(12);
            foreach (var old in sessions.Where(p => p.Value < DateTimeOffset.UtcNow)) sessions.TryRemove(old.Key, out _);
            context.Response.Cookies.Append("monitor_session", token, new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Strict, IsEssential = true, Path = "/", MaxAge = TimeSpan.FromHours(12) });
            return Results.Ok(new { ok = true });
        });
        app.MapGet("/api/status", () => Volatile.Read(ref status));
        app.MapGet("/api/settings", () => Settings);
        app.MapPost("/api/settings", (AppSettings value) => { UpdateSettings(value); return Results.Ok(Settings); });
        app.MapGet("/api/timezones", () => TimeZoneInfo.GetSystemTimeZones().Select(z => new { id = TimeZoneInfo.TryConvertWindowsIdToIanaId(z.Id, out var iana) ? iana : z.Id, name = z.DisplayName }).DistinctBy(z => z.id));
        app.MapGet("/api/nodes", (HttpRequest request) =>
        {
            var basis = RequestedBasis(request);
            return Scopes is null ? Ledger.GetNodes() :
                Scopes.GetDisplayNodes(basis);
        });
        app.MapGet("/api/history", (HttpRequest request) => QueryHistory(ParseQuery(request), RequestedBasis(request)));
        app.MapGet("/api/scope-history", (HttpRequest request) =>
        {
            if (Scopes is null) throw new ArgumentException("当前实例未显式启用多范围账本。");
            return Scopes.Query(ToScopeQuery(ParseQuery(request), request.Query["basis"].ToString()));
        });
        app.MapGet("/api/export", (HttpRequest request) =>
        {
            var query = ParseQuery(request);
            var basis = RequestedBasis(request);
            var scopeRows = Scopes?.Export(ToScopeQuery(query, basis));
            var rows = scopeRows is null ? Ledger.Export(query) : scopeRows.Select(ToTraffic).ToArray();
            var csv = new StringBuilder("\uFEFFstart_utc,end_utc,node,upload_bytes,download_bytes,quality" + (Scopes is null ? "" : ",basis,scope_key") +
                ",original_name,group_labels,rate,estimated_billed_bytes,estimate_covered_bytes,estimate_unknown_bytes\r\n");
            for (var i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                csv.AppendLine(string.Join(',', Cell(row.Start), Cell(row.End), Cell(row.NodeName), row.Upload, row.Download,
                    Cell(row.PartialCoverage ? row.Quality + ";uncollected_outbounds" : row.Quality)) +
                    (scopeRows is null ? "" : "," + Cell(scopeRows[i].Basis) + "," + Cell(row.NodeKey)) +
                    "," + string.Join(',', Cell(row.Display?.OriginalName ?? ""), Cell(string.Join(" | ", row.Display?.Groups ?? [])),
                        Cell(row.Display?.Rate ?? ""), Cell(row.Display?.EstimatedBilledBytes ?? ""),
                        Cell(row.Display?.EstimateCoveredBytes ?? "0"), Cell(row.Display?.EstimateUnknownBytes ??
                            (BigInteger.Parse(row.Upload, CultureInfo.InvariantCulture) + BigInteger.Parse(row.Download, CultureInfo.InvariantCulture)).ToString(CultureInfo.InvariantCulture))));
            }
            return Results.File(Encoding.UTF8.GetBytes(csv.ToString()), "text/csv; charset=utf-8", "v2rayn-local-history.csv");
        });
        app.MapPost("/api/imports/preview", (ImportRequest request) => imports.Preview(request));
        app.MapPost("/api/imports/commit", (ImportRequest request) => imports.Commit(request));
        app.MapGet("/api/imports", () => imports.List());
        app.MapGet("/api/provider/sources", () => imports.Sources());
        app.MapGet("/api/provider/nodes", (HttpRequest request) => imports.GetNodes(request.Query["source"].ToString()));
        app.MapGet("/api/provider/history", (HttpRequest request) => imports.Query(request.Query["source"].ToString(), ParseQuery(request)));
        app.MapGet("/api/provider/export", (HttpRequest request) =>
        {
            var source = request.Query["source"].ToString();
            var rows = imports.Export(source, ParseQuery(request));
            var csv = new StringBuilder("\uFEFFsource,at_utc,node,rate,raw_bytes,billed_bytes\r\n");
            foreach (var row in rows)
                csv.AppendLine(string.Join(',', Cell(source), Cell(row.At), Cell(row.NodeName), row.Rate, row.RawBytes, row.BilledBytes));
            return Results.File(Encoding.UTF8.GetBytes(csv.ToString()), "text/csv; charset=utf-8", "v2rayn-provider-history.csv");
        });
        app.MapPost("/api/demo", () => { if (!demo) return Results.BadRequest(new { error = "真实账本禁止注入演示数据，请使用 --demo 启动独立演示目录。" }); Ledger.SeedDemo(DateTimeOffset.UtcNow); return Results.Ok(new { ok = true }); });
        app.UseDefaultFiles(); app.UseStaticFiles();
        await app.StartAsync(ct).ConfigureAwait(false);
        Address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single().TrimEnd('/');
    }
    private string PrimaryBasis => Scopes is null ? "legacy_proxy" : "local_proxy";
    private string RequestedBasis(HttpRequest request)
    {
        if (!request.Query.TryGetValue("basis", out var values)) return PrimaryBasis;
        if (values.Count != 1 || values[0] is not ("probe_urltest_member" or "probe_urltest_business" or
            "probe_urltest_excluded" or "probe_urltest_unknown"))
            throw new ArgumentException("显式 basis 仅支持单个 URLTest 探测维度。");
        if (Scopes?.SupportsProbes != true) throw new ArgumentException("此实例未启用 v3 探测历史，未知不能记为零。");
        return values[0]!;
    }
    private ScopeQuery ToScopeQuery(QuerySpec query, string basis)
    {
        long? id = null;
        long[]? ids = null;
        if (query.NodeKey is { } key)
        {
            if (key.StartsWith("merge:", StringComparison.Ordinal))
            {
                ids = Scopes!.GetScopes(basis).Where(x => x.Scope.NodeId is not null &&
                    key == "merge:" + x.Scope.Basis + ":" + XrayParser.Hash(Uri.EscapeDataString(x.InstanceId) + "/" + Uri.EscapeDataString(x.Scope.NodeId)))
                    .Select(x => x.Id).ToArray();
                if (ids.Length == 0) throw new ArgumentException("没有可核实的节点归并范围。");
            }
            else if (!key.StartsWith("scope:", StringComparison.Ordinal) ||
                !long.TryParse(key.AsSpan(6), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed <= 0)
                throw new ArgumentException("请选择有效的多范围历史 ID。");
            else id = parsed;
        }
        return new(basis, query.Start, query.End, query.TimeZoneId, query.Bucket, id, query.Page, query.PageSize)
        { ScopeName = query.NodeName, ScopeIds = ids, NonZeroRecords = query.NonZeroRecords };
    }
    private static TrafficRow ToTraffic(ScopeTrafficRow row) =>
        new(row.Start, row.End, "scope:" + row.ScopeId.ToString(CultureInfo.InvariantCulture),
            row.Name, row.Upload, row.Download, row.Quality, row.PartialCoverage) { Display = row.Display };
    private HistoryResult QueryHistory(QuerySpec query, string basis)
    {
        if (Scopes is null) return Ledger.Query(query);
        var result = Scopes.Query(ToScopeQuery(query, basis));
        string Add(string a, string b) => (BigInteger.Parse(a, CultureInfo.InvariantCulture) + BigInteger.Parse(b, CultureInfo.InvariantCulture)).ToString(CultureInfo.InvariantCulture);
        return new(query.Start.ToString("O", CultureInfo.InvariantCulture), query.End.ToString("O", CultureInfo.InvariantCulture),
            result.Upload, result.Download, Add(result.RecoveredUpload, result.PartialUpload), Add(result.RecoveredDownload, result.PartialDownload),
            result.Gaps.Count, result.Series,
            result.Scopes.Select(x => new NodeTotal("scope:" + x.ScopeId.ToString(CultureInfo.InvariantCulture), x.Name,
                x.Upload, x.Download, x.RecoveredUpload, x.RecoveredDownload) { Display = x.Display }).ToArray(),
            result.Records.Select(ToTraffic).ToArray(), checked((int)result.RecordCount))
        {
            Basis = result.Basis, RecoveredUpload = result.RecoveredUpload, RecoveredDownload = result.RecoveredDownload,
            PartialUpload = result.PartialUpload, PartialDownload = result.PartialDownload,
            UnbucketedUpload = result.UnbucketedUpload, UnbucketedDownload = result.UnbucketedDownload,
            Gaps = result.Gaps.Select(x => new GapInfo(x.Start, x.End, x.Reason, x.IsOpen, x.Origin)).ToArray(),
            PartialCoverage = result.Gaps.Any(x => x.Reason == "uncollected_outbounds"),
            EarliestRecord = result.EarliestRecord, Display = result.Display, ComparisonNodes = result.ComparisonNodes,
            DisplayMetadataEnabled = result.DisplayMetadataEnabled
        };
    }
    private QuerySpec ParseQuery(HttpRequest request)
    {
        var q = request.Query;
        if (!DateTimeOffset.TryParse(q["start"], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var start) || !DateTimeOffset.TryParse(q["end"], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var end) || end <= start || end - start > TimeSpan.FromDays(3660)) throw new ArgumentException("日期范围无效，最多查询 10 年。");
        var zone = q["timeZoneId"].FirstOrDefault() ?? Settings.TimeZoneId;
        _ = TimeZoneInfo.FindSystemTimeZoneById(zone);
        var bucket = q["bucket"].FirstOrDefault() ?? "day";
        if (bucket is not ("hour" or "day" or "week" or "month" or "year")) throw new ArgumentException("分桶方式无效。");
        var page = int.TryParse(q["page"], out var p) ? p : 0; var size = int.TryParse(q["pageSize"], out var s) ? s : 50;
        if (page < 0 || page > 100000 || size < 1 || size > 500) throw new ArgumentException("分页范围无效。");
        var name = q["nodeName"].FirstOrDefault();
        var nonZero = false;
        if (q.TryGetValue("nonZeroRecords", out var flag) && (flag.Count != 1 || !bool.TryParse(flag[0], out nonZero)))
            throw new ArgumentException("非零明细筛选必须为 true 或 false。");
        return new(start, end, zone, bucket, q["nodeKey"].FirstOrDefault() is { Length: > 0 } key ? key : null, page, size)
        {
            NodeName = string.IsNullOrEmpty(name) ? null : name,
            NonZeroRecords = nonZero
        };
    }
    public static string Cell(string text)
    {
        // Leading whitespace may hide formula prefixes in spreadsheet applications.
        var trimmed = text.TrimStart();
        if (trimmed.Length > 0 && ("=+-@".Contains(trimmed[0]) || text[0] is '\t' or '\r' or '\n')) text = "'" + text;
        return "\"" + text.Replace("\"", "\"\"") + "\"";
    }
    private static string SafeError(Exception ex) => ex is ArgumentException && ex.Message.Length < 300 && !ex.Message.Contains("Parameter") ? ex.Message : "请检查输入格式、时区和数值范围。";
    private sealed record SessionRequest(string Token);
    public ValueTask DisposeAsync() => new(Task.Run(DisposeCoreAsync));
    private async Task DisposeCoreAsync() { if (app is not null) { await app.StopAsync().ConfigureAwait(false); await app.DisposeAsync().ConfigureAwait(false); } imports.Dispose(); legacyLedger?.Dispose(); Scopes?.Dispose(); }
}
