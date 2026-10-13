using System.Text.Json;
using PiSharp.Cli;

if (args[0] is "hold" or "write")
{
    var storage = new AuthStorage(args[1]);
    if (args[0] == "hold")
    {
        await storage.StoreApiKeyAsync("first", "fixture-key");
        await using var lease = await AuthFileLock.AcquireAsync(args[1] + ".pisharp.lock", default);
        Console.WriteLine("ready");
        _ = await Console.In.ReadLineAsync();
    }
    else
    {
        Console.WriteLine("writing");
        await storage.StoreApiKeyAsync("second", "fixture-key");
        Console.WriteLine(JsonSerializer.Serialize((await storage.ListAsync()).Keys.Order().ToArray()));
    }
    return;
}
var results = new List<object>();
foreach (var scenario in JsonSerializer.Deserialize<JsonElement[]>(File.ReadAllText(args[0]))!)
{
    var root = Path.Combine(Path.GetTempPath(), "pisharp-auth-policy-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    try
    {
        var id = scenario.GetProperty("id").GetString();
        var codes = new Queue<string>(scenario.GetProperty("codes").EnumerateArray().Select(value => value.GetString()!));
        var delays = new List<int>();
        var attempts = 0;
        var updates = 0;
        var releases = 0;
        var succeeded = false;
        var canceled = false;
        using var cancellation = new CancellationTokenSource();
        if (scenario.GetProperty("cancel").GetString() == "before") cancellation.Cancel();
        try
        {
            await using var lease = await AuthFileLock.AcquireAsync(Path.Combine(root, "lease"), cancellation.Token,
                windows: scenario.GetProperty("platform").GetString() == "win32", open: path =>
                {
                    attempts++;
                    if (codes.TryDequeue(out var code))
                    {
                        if (scenario.GetProperty("cancel").GetString() == "rejection") cancellation.Cancel();
                        throw new NativeLockException(code switch { "EPERM" => 5, "ELOCKED" => 32, "ENOENT" => 2, _ => 112 });
                    }
                    var file = new ObservedStream(path, () => releases++);
                    if (scenario.GetProperty("cancel").GetString() == "acquired") cancellation.Cancel();
                    return file;
                }, delay: (milliseconds, _) => { delays.Add(milliseconds); return Task.CompletedTask; }, random: () => 0);
            updates++;
            succeeded = true;
        }
        catch (OperationCanceledException) { canceled = true; }
        catch (IOException) { }
        results.Add(new { id, succeeded, canceled, attempts, updates, releases, delays });
    }
    finally { Directory.Delete(root, true); }
}
Console.WriteLine(JsonSerializer.Serialize(results));

sealed class NativeLockException : IOException
{
    internal NativeLockException(int code) : base("Fixture native lock error.") => HResult = unchecked((int)0x80070000) | code;
}

sealed class ObservedStream(string path, Action closed) : FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)
{
    private int _closed;
    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (Interlocked.Exchange(ref _closed, 1) == 0) closed();
    }
}
