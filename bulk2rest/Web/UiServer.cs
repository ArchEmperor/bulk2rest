using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using bulk2rest.Config;
using bulk2rest.Csv;
using bulk2rest.Http;
using bulk2rest.Mapping;

namespace bulk2rest.Web;

public sealed record PreviewRequest(string ConfigJson, string? CsvText, string? CsvPath);
public sealed record RunRequest(string ConfigJson, string? CsvText, string? CsvPath, string Mode, string? OutDir);

/// Local (127.0.0.1) web UI for editing the config, previewing rendered requests,
/// and running dry-run/send — all through the same C# engine as the CLI.
public static class UiServer
{
    private const int SampleCap = 20;
    private static readonly string IndexHtml = LoadIndexHtml();

    public static async Task<int> RunAsync(string[] args)
    {
        var configPath = "config.json";
        var port = 5088;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--config" when i + 1 < args.Length: configPath = args[++i]; break;
                case "--port" when i + 1 < args.Length: port = int.Parse(args[++i]); break;
            }
        }

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        // "localhost" binds both loopback stacks (127.0.0.1 + ::1), never external.
        builder.WebHost.UseUrls($"http://localhost:{port}");
        // Local single-user tool: allow large pasted-CSV bodies (default is 30 MB).
        builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = null);

        var app = builder.Build();
        app.MapGet("/", () => Results.Content(IndexHtml, "text/html; charset=utf-8"));
        MapEndpoints(app, configPath);

        var url = $"http://localhost:{port}";
        Console.WriteLine($"bulk2rest UI -> {url}");
        Console.WriteLine($"Config file: {Path.GetFullPath(configPath)}");
        Console.WriteLine("Ctrl+C to stop.");
        OpenBrowser(url);

        await app.RunAsync();
        return 0;
    }

    private static void MapEndpoints(WebApplication app, string configPath)
    {
        app.MapGet("/api/config", () =>
            File.Exists(configPath)
                ? Results.Text(File.ReadAllText(configPath), "application/json")
                : Results.NotFound($"Config not found: {Path.GetFullPath(configPath)}"));

        app.MapPost("/api/config", async (HttpContext ctx) =>
        {
            var text = await new StreamReader(ctx.Request.Body).ReadToEndAsync();
            try { SyncConfig.Parse(text); }
            catch (Exception ex) { return Results.BadRequest($"Invalid config: {ex.Message}"); }
            await File.WriteAllTextAsync(configPath, text);
            return Results.Ok(new { saved = Path.GetFullPath(configPath) });
        });

        app.MapPost("/api/pick", () =>
        {
            var path = NativeFilePicker.Pick();
            return Results.Json(new { path });
        });

        app.MapGet("/api/head", (string path, int? n) =>
        {
            try
            {
                if (!File.Exists(path)) return Results.BadRequest($"File not found: {path}");
                var take = n is > 0 ? n.Value : 100;
                var lines = new List<string>(take);
                using var reader = new StreamReader(path);
                for (var i = 0; i < take && reader.ReadLine() is { } line; i++)
                    lines.Add(line);
                return Results.Json(new { path, shown = lines.Count, text = string.Join("\n", lines) });
            }
            catch (Exception ex) { return Results.BadRequest(ex.Message); }
        });

        app.MapPost("/api/preview", (PreviewRequest req) =>
        {
            try
            {
                var (_, requests) = BuildRequests(req.ConfigJson, req.CsvText, req.CsvPath);
                return Results.Json(new
                {
                    total = requests.Count,
                    sample = requests.Take(SampleCap).Select(ToDto)
                });
            }
            catch (Exception ex) { return Results.BadRequest(ex.Message); }
        });

        app.MapPost("/api/run", async (RunRequest req) =>
        {
            try
            {
                var (config, requests) = BuildRequests(req.ConfigJson, req.CsvText, req.CsvPath);
                var outDir = string.IsNullOrWhiteSpace(req.OutDir) ? "out" : req.OutDir;
                var sender = new RequestSender(outDir, config.Auth);

                if (req.Mode == "send")
                {
                    var results = await sender.SendAsync(requests);
                    return Results.Json(new
                    {
                        mode = "send",
                        ok = results.Count(r => r.Ok),
                        failed = results.Count(r => !r.Ok),
                        outDir,
                        failures = results.Where(r => !r.Ok).Take(SampleCap)
                                          .Select(r => new { r.GroupKey, r.Status })
                    });
                }

                sender.DryRun(requests);
                return Results.Json(new
                {
                    mode = "dry",
                    total = requests.Count,
                    outDir,
                    sample = requests.Take(SampleCap).Select(ToDto)
                });
            }
            catch (Exception ex) { return Results.BadRequest(ex.Message); }
        });
    }

    private static (SyncConfig Config, List<RenderedRequest> Requests) BuildRequests(
        string configJson, string? csvText, string? csvPath)
    {
        var config = SyncConfig.Parse(configJson);
        List<Dictionary<string, string>> rows;
        if (!string.IsNullOrWhiteSpace(csvPath))
        {
            rows = CsvRecordReader.Read(csvPath, config.Source);
        }
        else
        {
            using var reader = new StringReader(csvText ?? "");
            rows = CsvRecordReader.Read(reader, config.Source);
        }
        return (config, RequestBuilder.Build(config, rows));
    }

    private static object ToDto(RenderedRequest r) =>
        new
        {
            r.GroupKey,
            r.Url,
            r.Method,
            r.BodyJson,
            Headers = r.Headers.ToDictionary(h => h.Key, h => MaskSecret(h.Key, h.Value))
        };

    /// Confirms auth is actually attached without echoing the token back to the browser.
    private static string MaskSecret(string name, string value)
    {
        if (!string.Equals(name, "Authorization", StringComparison.OrdinalIgnoreCase))
            return value;

        var parts = value.Split(' ', 2);
        if (parts.Length < 2) return $"<{value.Length} chars>";
        var secret = parts[1];
        var head = secret.Length <= 8 ? secret : secret[..8];
        return $"{parts[0]} {head}… <{secret.Length} chars>";
    }

    private static string LoadIndexHtml()
    {
        var asm = typeof(UiServer).Assembly;
        var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("index.html"))
                   ?? throw new InvalidOperationException("Embedded index.html not found.");
        using var stream = asm.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static void OpenBrowser(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* headless / no browser — user opens the printed URL manually */ }
    }
}
