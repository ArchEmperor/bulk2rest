using System.Text.Json;
using System.Text.Json.Nodes;
using bulk2rest.Config;

namespace bulk2rest.Mapping;

public sealed record RenderedRequest(
    string GroupKey,
    string Url,
    string Method,
    IReadOnlyDictionary<string, string> Headers,
    string BodyJson);

/// Renders requests from rows: one per group key, or one per row.
public static class RequestBuilder
{
    private static readonly JsonSerializerOptions WriteOpts = new() { WriteIndented = true };

    public static List<RenderedRequest> Build(SyncConfig config, List<Dictionary<string, string>> rows)
        => config.Grouping.Resolved == GroupingMode.Row
            ? BuildPerRow(config, rows)
            : BuildPerGroup(config, rows);

    private static List<RenderedRequest> BuildPerRow(
        SyncConfig config, List<Dictionary<string, string>> rows)
    {
        var requests = new List<RenderedRequest>(rows.Count);
        for (var i = 0; i < rows.Count; i++)
        {
            // Both scopes point at the row, so flat and $each templates both work.
            var body = TemplateEngine.Render(config.BodyTemplate, rows[i], [rows[i]], rows[i]);
            requests.Add(Render(config, $"row-{i + 1}", body));
        }
        return requests;
    }

    private static List<RenderedRequest> BuildPerGroup(
        SyncConfig config, List<Dictionary<string, string>> rows)
    {
        var by = config.Grouping.By;
        if (by.Count == 0)
            throw new InvalidOperationException(
                "grouping.by must list at least one column in group mode " +
                "(use grouping.mode \"row\" for one request per row).");

        var batchSize = config.Grouping.BatchSize;
        var requests = new List<RenderedRequest>();
        foreach (var group in rows.GroupBy(r => KeyOf(r, by)))
        {
            var groupRows = group.Cast<IReadOnlyDictionary<string, string>>().ToArray();
            var groupKey = BuildGroupKeyMap(group.First(), by);
            var batches = batchSize > 0 ? groupRows.Chunk(batchSize).ToList() : [groupRows];

            for (var i = 0; i < batches.Count; i++)
            {
                var body = TemplateEngine.Render(config.BodyTemplate, groupKey, batches[i]);
                // Batch suffix keeps dry-run filenames unique within a group.
                var key = batches.Count > 1 ? $"{group.Key}#{i + 1}" : group.Key;
                requests.Add(Render(config, key, body));
            }
        }
        return requests;
    }

    private static RenderedRequest Render(SyncConfig config, string groupKey, JsonNode body)
        => new(
            GroupKey: groupKey,
            Url: config.Request.Url,
            Method: config.Request.Method,
            Headers: config.Request.Headers,
            BodyJson: body.ToJsonString(WriteOpts));

    private static string KeyOf(IReadOnlyDictionary<string, string> row, List<string> by)
        => string.Join("__", by.Select(c => row.TryGetValue(c, out var v) ? v : ""));

    private static Dictionary<string, string> BuildGroupKeyMap(
        IReadOnlyDictionary<string, string> row, List<string> by)
    {
        var map = new Dictionary<string, string>(by.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var c in by)
            map[c] = row.TryGetValue(c, out var v) ? v : "";
        return map;
    }
}
