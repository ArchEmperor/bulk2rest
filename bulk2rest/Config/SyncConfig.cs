using System.Text.Json;
using System.Text.Json.Nodes;

namespace bulk2rest.Config;

public sealed record SyncConfig
{
    public SourceConfig Source { get; init; } = new();
    public RequestConfig Request { get; init; } = new();
    public AuthConfig? Auth { get; init; }
    public GroupingConfig Grouping { get; init; } = new();
    public JsonNode BodyTemplate { get; init; } = new JsonObject();

    public static SyncConfig Load(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Config not found: {path}");
        return Parse(File.ReadAllText(path));
    }

    public static SyncConfig Parse(string json)
    {
        var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        return JsonSerializer.Deserialize<SyncConfig>(json, opts)
               ?? throw new InvalidOperationException("Config parsed to null.");
    }
}

public sealed record SourceConfig
{
    public string Format { get; init; } = "csv";
    public string Delimiter { get; init; } = ",";
    public bool HasHeader { get; init; } = true;
}

public sealed record RequestConfig
{
    public string Url { get; init; } = "";
    public string Method { get; init; } = "POST";
    public Dictionary<string, string> Headers { get; init; } = new();
}

/// Picks how `Authorization` is produced. Anything it produces overrides an
/// Authorization entry in request.headers.
public sealed record AuthConfig
{
    /// none | basic | token. Empty infers token when the `token` block is present.
    public string Type { get; init; } = "";
    public string UserName { get; init; } = "";
    public string Password { get; init; } = "";

    /// token only: describes the minting request.
    public TokenAuthConfig? Token { get; init; }

    public AuthType Resolved => Type.ToLowerInvariant() switch
    {
        "none" => AuthType.None,
        "basic" => AuthType.Basic,
        "token" => AuthType.Token,
        "" => Token is not null ? AuthType.Token : AuthType.None,
        _ => throw new InvalidOperationException(
            $"Unknown auth.type '{Type}'. Expected none, basic or token.")
    };
}

public enum AuthType { None, Basic, Token }

/// Describes how `auth.type: "token"` mints its token: one HTTP request whose
/// response carries the token in its body, a JSON field, or a header.
public sealed record TokenAuthConfig
{
    public string Url { get; init; } = "";
    public string Method { get; init; } = "POST";

    /// JSON body of the minting request. `{{userName}}`/`{{password}}` are
    /// substituted from auth. Default: `{ "username": ..., "password": ... }`.
    public JsonNode? Body { get; init; }

    /// body | json | header - where the response carries the token.
    public string From { get; init; } = "body";

    /// `from: "json"`: dot path to the token string, e.g. "data.accessToken".
    public string Path { get; init; } = "";

    /// `from: "header"`: name of the response header carrying the token.
    public string Header { get; init; } = "";

    /// Prepended to the token to form the Authorization value.
    public string Prefix { get; init; } = "Bearer ";

    /// Minted tokens expire, so a 401 is worth one re-mint and retry.
    public bool RetryOn401 { get; init; } = true;
}

public sealed record GroupingConfig
{
    public List<string> By { get; init; } = new();

    /// Rows per request in group mode. Large groups otherwise render one huge payload that
    /// the server takes minutes to chew through; the reference pipeline chunks at 50
    /// (appsettings.json "updateFolderButchSize"). 0 disables splitting. Unused in row mode.
    public int BatchSize { get; init; }

    /// group | row. Empty is inferred from By, so pre-`mode` configs keep working.
    /// Stays explicit so switching to row mode in the UI need not erase By.
    public string Mode { get; init; } = "";

    public GroupingMode Resolved => Mode.ToLowerInvariant() switch
    {
        "group" => GroupingMode.Group,
        "row" => GroupingMode.Row,
        "" => By.Count > 0 ? GroupingMode.Group : GroupingMode.Row,
        _ => throw new InvalidOperationException(
            $"Unknown grouping.mode '{Mode}'. Expected group or row.")
    };
}

public enum GroupingMode { Group, Row }
