using System.Globalization;
using CsvHelper;
using CsvHelper.Configuration;
using bulk2rest.Config;

namespace bulk2rest.Csv;

/// Reads a CSV export into header-keyed rows. Header row is required so
/// body-template tokens can reference columns by name.
public static class CsvRecordReader
{
    public static List<Dictionary<string, string>> Read(string path, SourceConfig source)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Input CSV not found: {path}");
        using var reader = new StreamReader(path);
        return Read(reader, source);
    }

    public static List<Dictionary<string, string>> Read(TextReader reader, SourceConfig source)
    {
        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            Delimiter = source.Delimiter,
            HasHeaderRecord = source.HasHeader,
        };

        using var csv = new CsvReader(reader, config);

        csv.Read();
        csv.ReadHeader();
        var headers = csv.HeaderRecord
                      ?? throw new InvalidOperationException("CSV missing header row.");

        var rows = new List<Dictionary<string, string>>();
        while (csv.Read())
        {
            var row = new Dictionary<string, string>(headers.Length, StringComparer.OrdinalIgnoreCase);
            foreach (var h in headers)
                row[h] = csv.GetField(h) ?? "";
            rows.Add(row);
        }
        return rows;
    }
}
