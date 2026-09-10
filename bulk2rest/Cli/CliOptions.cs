namespace bulk2rest.Cli;

public sealed record CliOptions
{
    public string ConfigPath { get; init; } = "config.json";
    public string? InputPath { get; init; }
    public string OutDir { get; init; } = "out";
    public bool Send { get; init; }
    public bool RetryFailed { get; init; }

    /// Returns null when parsing fails or help is requested (caller prints usage).
    public static CliOptions? Parse(string[] args)
    {
        var o = new CliOptions();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--config": o = o with { ConfigPath = Next(args, ref i) }; break;
                case "--input": o = o with { InputPath = Next(args, ref i) }; break;
                case "--out": o = o with { OutDir = Next(args, ref i) }; break;
                case "--send": o = o with { Send = true }; break;
                case "--retry-failed": o = o with { RetryFailed = true }; break;
                case "-h" or "--help": return null;
                default:
                    Console.Error.WriteLine($"Unknown argument: {args[i]}");
                    return null;
            }
        }
        return o;
    }

    private static string Next(string[] args, ref int i)
    {
        if (i + 1 >= args.Length)
            throw new ArgumentException($"Missing value for {args[i]}");
        return args[++i];
    }

    public static void PrintUsage() => Console.WriteLine(
        """
        bulk2rest — config-driven CSV -> REST sync.

        Usage:
          bulk2rest --input <csv> [--config config.json] [--out out] [--send]
          bulk2rest --retry-failed [--config config.json] [--out out]

        Options:
          --config <path>   Config JSON (default: config.json)
          --input <path>    CSV export to process
          --out <dir>       Output dir for dry-run files / results (default: out)
          --send            Actually send the requests (default: dry-run only)
          --retry-failed    Resend requests from <out>/failed.json
          -h, --help        Show this help
        """);
}
