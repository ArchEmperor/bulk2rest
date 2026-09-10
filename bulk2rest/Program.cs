using bulk2rest.Cli;
using bulk2rest.Config;
using bulk2rest.Csv;
using bulk2rest.Http;
using bulk2rest.Mapping;
using bulk2rest.Web;

try
{
    // No args → launch the UI (friendly for double-click); `ui` does the same.
    if (args.Length == 0 || args[0] == "ui")
        return await UiServer.RunAsync(args.Length == 0 ? [] : args[1..]);

    return await RunCli(args);
}
catch (Exception ex)
{
    Console.Error.WriteLine("Error: " + ex.Message);
    ConsoleGuard.PauseIfOwnWindow();
    return 1;
}

static async Task<int> RunCli(string[] args)
{
    var options = CliOptions.Parse(args);
    if (options is null)
    {
        CliOptions.PrintUsage();
        return 1;
    }

    var config = SyncConfig.Load(options.ConfigPath);
    var sender = new RequestSender(options.OutDir, config.Auth);

    List<RenderedRequest> requests;
    if (options.RetryFailed)
    {
        requests = sender.LoadFailed();
        if (requests.Count == 0)
        {
            Console.WriteLine("No failed requests to retry.");
            ConsoleGuard.PauseIfOwnWindow();
            return 0;
        }
        Console.WriteLine($"Retrying {requests.Count} failed request(s).");
    }
    else
    {
        if (options.InputPath is null)
        {
            Console.Error.WriteLine("--input <csv> is required (unless --retry-failed).");
            CliOptions.PrintUsage();
            return 1;
        }

        var rows = CsvRecordReader.Read(options.InputPath, config.Source);
        requests = RequestBuilder.Build(config, rows);
        Console.WriteLine($"Parsed {rows.Count} row(s) -> {requests.Count} request(s).");
    }

    if (options.Send || options.RetryFailed)
        await sender.SendAsync(requests);
    else
        sender.DryRun(requests);

    ConsoleGuard.PauseIfOwnWindow();
    return 0;
}
