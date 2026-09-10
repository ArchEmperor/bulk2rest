using System.Net;
using System.Text;
using System.Text.Json;
using bulk2rest.Config;
using bulk2rest.Mapping;

namespace bulk2rest.Http;

public sealed record SendResult(string GroupKey, bool Ok, string Status);

/// Writes dry-run request files, sends live requests, and tracks failures for retry.
public sealed class RequestSender
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly string _outDir;
    private readonly Authenticator _auth;

    public RequestSender(string outDir, AuthConfig? auth = null)
    {
        _outDir = outDir;
        _auth = new Authenticator(auth);
    }

    private string FailedPath => Path.Combine(_outDir, "failed.json");

    public void DryRun(IReadOnlyList<RenderedRequest> requests)
    {
        var reqDir = Path.Combine(_outDir, "requests");
        Directory.CreateDirectory(reqDir);

        var manifest = new List<object>();
        foreach (var r in requests)
        {
            var file = Path.Combine(reqDir, SafeName(r.GroupKey) + ".json");
            File.WriteAllText(file, r.BodyJson);
            manifest.Add(new { r.GroupKey, r.Url, r.Method, File = file });
        }

        File.WriteAllText(Path.Combine(_outDir, "manifest.json"),
            JsonSerializer.Serialize(manifest, JsonOpts));

        Console.WriteLine($"Dry-run: wrote {requests.Count} request file(s) to {reqDir}");
        Console.WriteLine($"Manifest: {Path.Combine(_outDir, "manifest.json")}");
        Console.WriteLine("Review, then re-run with --send to send them.");
    }

    public async Task<List<SendResult>> SendAsync(IReadOnlyList<RenderedRequest> requests)
    {
        Directory.CreateDirectory(_outDir);
        // Default 100s aborts large folder updates client-side while the server is still
        // working on them, mirroring the reference client's `Timeout = -1`.
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

        // Mint up front so bad credentials fail before any request is sent.
        await _auth.GetAsync(http);

        var results = new List<SendResult>(requests.Count);
        var failed = new List<RenderedRequest>();
        var log = new StringBuilder();
        var ok = 0;

        foreach (var r in requests)
        {
            string status;
            var success = false;
            try
            {
                var (code, body) = await SendOneAsync(http, r);

                // A minted token expires; discard it and retry once with a fresh one.
                if (code == HttpStatusCode.Unauthorized && _auth.CanRefresh)
                {
                    _auth.Invalidate();
                    (code, body) = await SendOneAsync(http, r);
                }

                success = (int)code is >= 200 and < 300;
                status = $"{(int)code} {code}";
                if (!success) status += $" | {ResponseText.Excerpt(body)}";
            }
            catch (Exception ex)
            {
                status = $"ERROR {ex.Message}";
            }

            var line = $"{r.GroupKey}\t{status}";
            log.AppendLine(line);
            Console.WriteLine(line);
            results.Add(new SendResult(r.GroupKey, success, status));
            if (success) ok++;
            else failed.Add(r);
        }

        File.AppendAllText(Path.Combine(_outDir, "results.log"), log.ToString());
        File.WriteAllText(FailedPath, JsonSerializer.Serialize(failed, JsonOpts));

        Console.WriteLine($"Sent {requests.Count}: {ok} ok, {failed.Count} failed.");
        if (failed.Count > 0)
            Console.WriteLine($"Failures in {FailedPath}. Re-run with --retry-failed to resend.");

        return results;
    }

    public List<RenderedRequest> LoadFailed()
    {
        if (!File.Exists(FailedPath)) return new();
        var json = File.ReadAllText(FailedPath);
        return JsonSerializer.Deserialize<List<RenderedRequest>>(json, JsonOpts) ?? new();
    }

    private async Task<(HttpStatusCode Code, string Body)> SendOneAsync(HttpClient http, RenderedRequest r)
    {
        using var msg = new HttpRequestMessage(new HttpMethod(r.Method), r.Url)
        {
            Content = new StringContent(r.BodyJson, Encoding.UTF8, "application/json")
        };
        ApplyHeaders(msg, r.Headers);
        if (await _auth.GetAsync(http) is { } authorization)
        {
            msg.Headers.Remove("Authorization");
            AddHeader(msg, "Authorization", authorization);
        }

        using var resp = await http.SendAsync(msg);
        return (resp.StatusCode, await resp.Content.ReadAsStringAsync());
    }

    private static void ApplyHeaders(HttpRequestMessage msg, IReadOnlyDictionary<string, string> headers)
    {
        foreach (var (name, value) in headers)
        {
            // Content-Type is set on the StringContent above; adding it to
            // request headers would throw.
            if (string.Equals(name, "Content-Type", StringComparison.OrdinalIgnoreCase))
                continue;
            AddHeader(msg, name, value);
        }
    }

    /// A pasted token carrying a line break yields a header that is dropped or mangled on the wire —
    /// an unauthenticated request that looks exactly like a bad token. Fail loudly instead.
    private static void AddHeader(HttpRequestMessage msg, string name, string value)
    {
        if (value.Contains('\r') || value.Contains('\n'))
            throw new InvalidOperationException(
                $"Header '{name}' contains a line break — check for a stray newline in the pasted value.");

        if (!msg.Headers.TryAddWithoutValidation(name, value))
            throw new InvalidOperationException($"Header name '{name}' was rejected as invalid.");
    }

    private static string SafeName(string key)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            key = key.Replace(c, '_');
        return key;
    }
}
