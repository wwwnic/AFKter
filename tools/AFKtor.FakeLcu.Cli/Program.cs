using AFKtor.FakeLcu;

// Runs a fake League client so the real tray app can be tried without League.
// Usage: dotnet run --project tools/AFKtor.FakeLcu.Cli [lockfile-path]
var lockfile = Path.GetFullPath(args.Length > 0 ? args[0] : "fake-lcu.lockfile");
await using var server = await FakeLcuServer.StartAsync(log: message => Console.WriteLine($"  · {message}"));
server.WriteLockfile(lockfile);

Console.WriteLine($"""

    Fake League client running. Start AFKtor pointed at it, in another PowerShell window:

      $env:AFKTOR_LCU_LOCKFILE = "{lockfile}"
      $env:AFKTOR_DATA_DIR = "{Path.Combine(Path.GetTempPath(), "AFKtor-fake")}"
      dotnet run --project src/AFKtor.Tray

    (Exit any normal AFKtor first: only one copy runs at a time.)
    """);

var steps = new Dictionary<string, (string Help, Func<Task> Run)>
{
    ["q"] = ("enter the queue (Matchmaking)", () => server.SetPhaseAsync("Matchmaking")),
    ["r"] = ("a match is found: ready check appears", server.StartReadyCheckAsync),
    ["a"] = ("you click Accept in League", () => server.AnswerInClientAsync(accept: true)),
    ["d"] = ("you click Decline in League", () => server.AnswerInClientAsync(accept: false)),
    ["e"] = ("everyone accepted: champ select", server.EveryoneReadyAsync),
    ["o"] = ("another player declined: back in queue", server.DeclinedByOtherPlayerAsync),
    ["x"] = ("the check times out: out of queue", server.ExpireAsync),
    ["g"] = ("the game starts (InProgress)", () => server.SetPhaseAsync("InProgress")),
    ["c"] = ("the game is running, needs reconnect", () => server.SetPhaseAsync("Reconnect")),
    ["l"] = ("back to the lobby", () => server.SetPhaseAsync("Lobby")),
    ["k"] = ("League restarts (new port and password)", async () =>
    {
        await server.RestartAsync();
        server.WriteLockfile(lockfile);
    }),
    ["s"] = ("drop the event stream", () => { server.DropEventStreams(); return Task.CompletedTask; }),
    ["f"] = ("toggle failing accept requests", () =>
    {
        server.FailAccepts = !server.FailAccepts;
        Console.WriteLine($"  accept requests now {(server.FailAccepts ? "fail with HTTP 500" : "succeed")}");
        return Task.CompletedTask;
    }),
    ["?"] = ("show state", () =>
    {
        Console.WriteLine($"  phase {server.Phase}, accepts {server.AcceptCount}, rejected logins {server.UnauthorizedCount}");
        Console.WriteLine($"  ready check {server.ReadyCheckResource?.ToJsonString() ?? "(none, 404)"}");
        return Task.CompletedTask;
    }),
};
PrintHelp();

try
{
    while (Console.ReadLine() is { } line && line.Trim() is not ("exit" or "quit"))
    {
        var key = line.Trim().ToLowerInvariant();
        if (key.Length == 0) { continue; }
        if (!steps.TryGetValue(key, out var step)) { PrintHelp(); continue; }
        try { await step.Run(); }
        catch (InvalidOperationException error) { Console.WriteLine($"  {error.Message}"); }
    }
}
finally
{
    File.Delete(lockfile);
}

void PrintHelp()
{
    Console.WriteLine("Commands (then Enter):");
    foreach (var (key, step) in steps) { Console.WriteLine($"  {key}  {step.Help}"); }
    Console.WriteLine("  exit");
}
