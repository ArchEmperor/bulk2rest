using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using bulk2rest.Config;

namespace bulk2rest.Http;

/// Produces the `Authorization` header value for the configured preset, caching
/// whatever it had to fetch.
public sealed class Authenticator
{
    private readonly AuthConfig? _config;
    private readonly AuthType _type;
    private string? _cached;

    public Authenticator(AuthConfig? config)
    {
        _config = config;
        _type = config?.Resolved ?? AuthType.None;
        if (_type == AuthType.Token && _config!.Token is null)
            throw new InvalidOperationException(
                "auth.type is 'token' but the auth.token block is missing.");
    }

    /// A minted token expires, so a 401 is worth one retry with a fresh one.
    /// Basic would re-derive an identical header, so retrying it is pointless.
    public bool CanRefresh => _type == AuthType.Token && _config!.Token!.RetryOn401;

    public void Invalidate() => _cached = null;

    /// Null when the preset adds nothing — request.headers is then left untouched.
    public async Task<string?> GetAsync(HttpClient http)
    {
        if (_type == AuthType.None) return null;
        return _cached ??= _type switch
        {
            AuthType.Basic => BasicHeader(_config!),
            AuthType.Token => _config!.Token!.Prefix + await MintTokenAsync(http, _config!),
            _ => null
        };
    }

    private static string BasicHeader(AuthConfig auth)
    {
        var raw = Encoding.UTF8.GetBytes($"{auth.UserName}:{auth.Password}");
        return "Basic " + Convert.ToBase64String(raw);
    }

    /// Builds the minting request described by auth.token and reads the token
    /// back out of the response (body, a JSON field, or a header).
    private static async Task<string> MintTokenAsync(HttpClient http, AuthConfig auth)
    {
        var t = auth.Token!;
        if (t.Url.Length == 0)
            throw new InvalidOperationException("auth.token.url is empty.");

        using var req = new HttpRequestMessage(
            new HttpMethod(t.Method.Length == 0 ? "POST" : t.Method), t.Url)
        {
            Content = new StringContent(RenderBody(t, auth), Encoding.UTF8, "application/json")
        };
        using var resp = await http.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Auth failed at {t.Url}: {(int)resp.StatusCode} {resp.StatusCode} " +
                $"| {ResponseText.Excerpt(body)}");

        var token = t.From.ToLowerInvariant() switch
        {
            // The response body is the token itself, usually a bare JSON string
            // literal, so surrounding whitespace and quotes are stripped.
            "body" => body.Trim().Trim('"'),
            "json" => FromJson(body, t.Path),
            "header" => FromHeader(resp, t.Header),
            _ => throw new InvalidOperationException(
                $"Unknown auth.token.from '{t.From}'. Expected body, json or header.")
        };

        if (token.Length == 0)
            throw new InvalidOperationException($"Auth returned an empty token from {t.Url}.");

        Console.WriteLine($"Authenticated: token acquired ({token.Length} chars).");
        return token;
    }

    /// Serializes the minting request body, substituting {{userName}}/{{password}}
    /// from the auth block. The default mirrors the common "POST the credentials
    /// as JSON" login shape.
    private static string RenderBody(TokenAuthConfig t, AuthConfig auth)
    {
        var node = t.Body is null
            ? new JsonObject { ["username"] = auth.UserName, ["password"] = auth.Password }
            : Substitute(t.Body, auth);
        return node.ToJsonString();
    }

    /// Rebuilds the template with {{userName}}/{{password}} replaced in every
    /// string value, so a credential containing quotes stays properly JSON-escaped.
    private static JsonNode Substitute(JsonNode node, AuthConfig auth)
    {
        if (node is JsonObject obj)
            return new JsonObject(obj.Select(p =>
                new KeyValuePair<string, JsonNode?>(p.Key, p.Value is null ? null : Substitute(p.Value, auth))));
        if (node is JsonArray arr)
            return new JsonArray(arr.Select(n => n is null ? null : Substitute(n, auth)).ToArray());
        if (node is JsonValue value && value.TryGetValue<string>(out var s))
            return JsonValue.Create(s.Replace("{{userName}}", auth.UserName)
                                     .Replace("{{password}}", auth.Password));
        return node.DeepClone();
    }

    /// `from: "json"` - walks a dot path ("data.accessToken", "items.0.token")
    /// to the token string.
    private static string FromJson(string body, string path)
    {
        if (path.Length == 0)
            throw new InvalidOperationException(
                "auth.token.from is 'json' but auth.token.path is empty.");

        JsonNode? node = null;
        try { node = JsonNode.Parse(body); }
        catch (JsonException) { /* reported below, where the shape can be named */ }

        foreach (var segment in path.Split('.'))
        {
            node = node switch
            {
                JsonObject o => o[segment],
                JsonArray a when int.TryParse(segment, out var i) && i >= 0 && i < a.Count => a[i],
                _ => null
            };
            if (node is null) break;
        }

        if (node is not JsonValue result || !result.TryGetValue<string>(out var token))
            throw new InvalidOperationException($"Auth response has no string token at '{path}'.");
        return token;
    }

    private static string FromHeader(HttpResponseMessage resp, string name)
    {
        if (name.Length == 0)
            throw new InvalidOperationException(
                "auth.token.from is 'header' but auth.token.header is empty.");

        if (!resp.Headers.TryGetValues(name, out var values))
            resp.Content.Headers.TryGetValues(name, out values);
        return values?.FirstOrDefault() ?? "";
    }
}
