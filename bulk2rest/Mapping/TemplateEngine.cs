using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace bulk2rest.Mapping;

/// Renders a JSON body template for one group of rows.
///
/// Constructs:
///   {{group:COL}}                       -> group-key column value
///   {{row:COL}}                         -> current row value (inside $each, or anywhere
///                                          when the caller supplies `currentRow`)
///   { "$each": { "where": {..}, "as": {..} } }
///                                       -> array: rows matching `where`, each rendered via `as`
public static partial class TemplateEngine
{
    [GeneratedRegex(@"\{\{\s*(group|row)\s*:\s*([^}]+?)\s*\}\}")]
    private static partial Regex TokenPattern();

    /// `currentRow` lets row mode render a flat template with no $each wrapper.
    public static JsonNode Render(
        JsonNode template,
        IReadOnlyDictionary<string, string> groupKey,
        IReadOnlyList<IReadOnlyDictionary<string, string>> groupRows,
        IReadOnlyDictionary<string, string>? currentRow = null)
        => RenderNode(template, groupKey, groupRows, currentRow)
           ?? throw new InvalidOperationException("Template rendered to null root.");

    private static JsonNode? RenderNode(
        JsonNode? node,
        IReadOnlyDictionary<string, string> groupKey,
        IReadOnlyList<IReadOnlyDictionary<string, string>> groupRows,
        IReadOnlyDictionary<string, string>? currentRow)
    {
        switch (node)
        {
            case JsonObject obj when obj.ContainsKey("$each"):
                return RenderEach(obj["$each"]!.AsObject(), groupKey, groupRows);

            case JsonObject obj:
                var result = new JsonObject();
                foreach (var (key, value) in obj)
                    result[key] = RenderNode(value, groupKey, groupRows, currentRow);
                return result;

            case JsonArray arr:
                var outArr = new JsonArray();
                foreach (var item in arr)
                    outArr.Add(RenderNode(item, groupKey, groupRows, currentRow));
                return outArr;

            case JsonValue val when val.TryGetValue<string>(out var s):
                return JsonValue.Create(Substitute(s, groupKey, currentRow));

            default:
                return node?.DeepClone();
        }
    }

    private static JsonArray RenderEach(
        JsonObject each,
        IReadOnlyDictionary<string, string> groupKey,
        IReadOnlyList<IReadOnlyDictionary<string, string>> groupRows)
    {
        var where = each["where"]?.AsObject();
        var asTemplate = each["as"]
                         ?? throw new InvalidOperationException("$each requires an 'as' template.");

        var array = new JsonArray();
        foreach (var row in groupRows)
        {
            if (!Matches(row, where)) continue;
            array.Add(RenderNode(asTemplate, groupKey, groupRows, row));
        }
        return array;
    }

    private static bool Matches(IReadOnlyDictionary<string, string> row, JsonObject? where)
    {
        if (where is null) return true;
        foreach (var (col, expected) in where)
        {
            var want = expected?.GetValue<string>() ?? "";
            if (!string.Equals(Column(row, col), want, StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    private static string Substitute(
        string input,
        IReadOnlyDictionary<string, string> groupKey,
        IReadOnlyDictionary<string, string>? currentRow)
        => TokenPattern().Replace(input, m =>
        {
            var scope = m.Groups[1].Value;
            var col = m.Groups[2].Value;
            return scope switch
            {
                "group" => Column(groupKey, col),
                "row" => currentRow is null
                    ? throw new InvalidOperationException($"{{{{row:{col}}}}} used outside $each.")
                    : Column(currentRow, col),
                _ => m.Value
            };
        });

    /// A misspelled column would otherwise render "" and merge or empty requests silently.
    internal static string Column(IReadOnlyDictionary<string, string> row, string col)
        => row.TryGetValue(col, out var value)
            ? value
            : throw new InvalidOperationException(
                $"Unknown column '{col}' - check the CSV header and grouping.by.");
}
