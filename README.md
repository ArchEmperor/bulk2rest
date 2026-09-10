# bulk2rest

Turns a CSV export into REST requests described by a JSON config. Nothing about the
target API is compiled in — the URL, method, headers, auth, how rows map to requests,
and the request body all come from the config file.

Run it with no arguments and it opens a local web UI for editing that config,
previewing the rendered requests, and sending them. The same engine backs the CLI.

## Usage

```
bulk2rest                                           # open the UI (also: bulk2rest ui)
bulk2rest --input <csv> [--config config.json] [--out out] [--send]
bulk2rest --retry-failed [--config config.json] [--out out]
```

| Option | Meaning |
| --- | --- |
| `--config <path>` | Config JSON (default `config.json`) |
| `--input <path>` | CSV export to process |
| `--out <dir>` | Where dry-run files and results go (default `out`) |
| `--send` | Actually send. Without it you get a dry run |
| `--retry-failed` | Resend the requests recorded in `<out>/failed.json` |

A dry run writes one JSON file per request to `<out>/requests/` plus a `manifest.json`.
A live run appends to `<out>/results.log` and writes every failure to `<out>/failed.json`,
which `--retry-failed` picks up.

`ui` accepts `--config <path>` and `--port <n>` (default 5088, loopback only).

## Config

See [`bulk2rest/examples/`](bulk2rest/examples) for complete files.

### `source`

| Key | Meaning |
| --- | --- |
| `delimiter` | Column separator, default `,` |
| `hasHeader` | Must be `true` |

The input is always a CSV **with a header row** — template tokens reference columns by
name. A `format` key is accepted but ignored, and `hasHeader: false` is not honoured.

### `request`

| Key | Meaning |
| --- | --- |
| `url` | Target URL, identical for every request |
| `method` | `POST`, `PUT`, `PATCH`, … |
| `headers` | Sent as-is. `Content-Type` is set from the JSON body, so an entry for it is ignored |

An `Authorization` header here is overwritten whenever `auth` produces one.

### `auth`

Omit the block entirely for no auth. `type` picks the preset:

```jsonc
"auth": { "type": "basic", "userName": "u", "password": "p" }
```

| `type` | Behaviour |
| --- | --- |
| `none` | Nothing added. Put an API key or a pre-issued token in `request.headers` |
| `basic` | `Authorization: Basic base64(userName:password)` |
| `token` | Mints a token as described by the `token` block, then sends `Authorization: prefix + token` |

Omitting `type` infers `token` when the `token` block is present, otherwise `none`.

#### `auth.token`

Describes the login request that mints the token:

| Key | Meaning |
| --- | --- |
| `url` | Where the login request goes |
| `method` | HTTP method, default `POST` |
| `body` | JSON body template; `{{userName}}` and `{{password}}` are substituted from `auth`. Default: `{ "username": …, "password": … }` |
| `from` | Where the response carries the token: `body`, `json` or `header` (default `body`) |
| `path` | `from: "json"` — dot path to the token string, e.g. `data.accessToken` |
| `header` | `from: "header"` — name of the response header carrying the token |
| `prefix` | Prepended to the token, default `"Bearer "` |
| `retryOn401` | Re-mint once and retry a request that came back `401`, default `true` |

`from: "body"` treats the whole response body as the token: surrounding whitespace
and the quotes of a bare JSON string literal are stripped. The token is minted
once per run and cached.

```jsonc
"auth": {
  "type": "token",
  "userName": "u",
  "password": "p",
  "token": { "url": "https://host/api/login", "from": "json", "path": "token" }
}
```

### `grouping`

| Key | Meaning |
| --- | --- |
| `mode` | `group` or `row` |
| `by` | Columns forming the group key (`group` mode) |
| `batchSize` | Max rows per request inside a group; `0` = no split (`group` mode) |

- **`row`** — one request per CSV row. `{{row:COL}}` works at the top level of the
  template, no `$each` needed. `by` and `batchSize` are ignored but preserved.
- **`group`** — rows sharing the values of `by` render one request together, which is
  what `$each` pivots over. A group larger than `batchSize` is split across several
  requests, each suffixed `#1`, `#2`, … in the logs and dry-run filenames.

Omitting `mode` infers `group` when `by` is non-empty, otherwise `row`.

### `bodyTemplate`

Any JSON. Three constructs are substituted while rendering:

| Construct | Renders as |
| --- | --- |
| `{{group:COL}}` | The group key's value for `COL` (in `row` mode, the row's value) |
| `{{row:COL}}` | The current row's value for `COL` — inside `$each`, or anywhere in `row` mode |
| `{ "$each": { "where": {…}, "as": {…} } }` | An array: every row in the group matching `where`, each rendered through `as` |

`where` is optional and compares columns for exact string equality. Every value
rendered is a JSON string, since CSV has no types.

```jsonc
{
  "folderId": "{{group:FolderId}}",
  "add":    { "$each": { "where": { "Operation": "1" },  "as": { "sku": "{{row:Sku}}" } } },
  "delete": { "$each": { "where": { "Operation": "-1" }, "as": { "sku": "{{row:Sku}}" } } }
}
```

## Examples

| File | Shows |
| --- | --- |
| `bulk2rest/examples/row-per-request.json` | `row` mode, no auth, API key in a header |
| `bulk2rest/examples/basic-auth.json` | `group` mode with `$each`, HTTP Basic |
| `bulk2rest/examples/token-auth.json` | `group` mode batched at 50, token auth with a custom login body |

## Build

```
dotnet build bulk2rest.sln
```

Requires .NET 10. The UI page is embedded in the assembly, so the published exe is
self-contained apart from the config file.
