using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AFKtor.FakeLcu;

/// <summary>
/// A stand-in for the League Client's local API (LCU), for tests and manual runs without League.
/// It serves HTTPS and the WAMP event socket on 127.0.0.1 with a self-signed certificate and
/// Basic auth (<c>riot:&lt;password&gt;</c>), like the real client, and only knows the routes
/// AFKtor uses: the matchmaking ready check, its accept action and the gameflow phase.
/// Scenario methods move the client through the same states League shows during a queue.
/// </summary>
public sealed class FakeLcuServer : IAsyncDisposable
{
    public const string ReadyCheckRoute = "/lol-matchmaking/v1/ready-check";
    public const string AcceptRoute = "/lol-matchmaking/v1/ready-check/accept";
    public const string GameflowPhaseRoute = "/lol-gameflow/v1/gameflow-phase";
    public static string TopicFor(string route) => "OnJsonApiEvent" + route.Replace('/', '_');

    private const string AllEventsTopic = "OnJsonApiEvent";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private readonly object gate = new();
    private readonly SemaphoreSlim publishing = new(1, 1);
    private readonly ConcurrentDictionary<Guid, EventSocket> sockets = new();
    private readonly ConcurrentQueue<string> requests = new();
    private readonly TimeProvider time;
    private readonly int requestedPort;
    private readonly Action<string>? log;
    private X509Certificate2? certificate;
    private WebApplication? app;
    private ReadyCheck? readyCheck;
    private string phase = "None";
    private int acceptCount, unauthorizedCount;

    private FakeLcuServer(int port, TimeProvider? timeProvider, Action<string>? log)
        => (requestedPort, time, this.log) = (port, timeProvider ?? TimeProvider.System, log);

    /// <summary>The port of the running server. A restart picks a new one unless a fixed port was requested.</summary>
    public int Port { get; private set; }
    /// <summary>A new random password on every start, as League does.</summary>
    public string Password { get; private set; } = "";
    /// <summary>The process that "is" the League client. This process, so a PID liveness check passes.</summary>
    public int ProcessId { get; } = Environment.ProcessId;
    /// <summary>Contents in the real lockfile format: <c>LeagueClient:pid:port:password:https</c>.</summary>
    public string LockfileContents => $"LeagueClient:{ProcessId}:{Port}:{Password}:https";
    public bool IsRunning => app is not null;
    public string Phase { get { lock (gate) { return phase; } } }
    public int AcceptCount => Volatile.Read(ref acceptCount);
    public int UnauthorizedCount => Volatile.Read(ref unauthorizedCount);
    /// <summary>Every authenticated request as "METHOD /path", oldest first.</summary>
    public IReadOnlyList<string> Requests => requests.ToArray();
    /// <summary>When true, accept requests fail with HTTP 500 and change nothing.</summary>
    public bool FailAccepts { get; set; }
    /// <summary>Snapshot of the current ready-check resource, or null when the route returns 404.</summary>
    public JsonObject? ReadyCheckResource { get { lock (gate) { return readyCheck?.ToJson(time); } } }

    public static async Task<FakeLcuServer> StartAsync(int port = 0, TimeProvider? timeProvider = null, Action<string>? log = null)
    {
        var server = new FakeLcuServer(port, timeProvider, log);
        await server.StartCoreAsync().ConfigureAwait(false);
        return server;
    }

    /// <summary>Writes <see cref="LockfileContents"/> to <paramref name="path"/> and returns the path.</summary>
    public string WriteLockfile(string path)
    {
        File.WriteAllText(path, LockfileContents);
        return path;
    }

    // ---- Scenario steps -------------------------------------------------------------------

    /// <summary>Gameflow moves to <paramref name="value"/> (for example "Lobby", "Matchmaking", "ChampSelect").</summary>
    public Task SetPhaseAsync(string value)
    {
        lock (gate) { phase = value; }
        return PublishAsync(GameflowPhaseRoute, "Update", JsonValue.Create(value));
    }

    /// <summary>A match is found: the phase becomes ReadyCheck and an unanswered check appears.</summary>
    /// <param name="startedEarlier">
    /// How long before this call the check started. Real checks start on Riot's servers, so
    /// League's <c>timer</c> is already ahead of the moment AFKtor hears about the check.
    /// </param>
    public async Task StartReadyCheckAsync(TimeSpan startedEarlier = default)
    {
        JsonObject created;
        lock (gate)
        {
            readyCheck = new ReadyCheck(time.GetTimestamp() - (long)(startedEarlier.TotalSeconds * time.TimestampFrequency));
            created = readyCheck.ToJson(time);
        }
        await SetPhaseAsync("ReadyCheck").ConfigureAwait(false);
        await PublishAsync(ReadyCheckRoute, "Create", created).ConfigureAwait(false);
    }

    /// <summary>The local player accepts or declines in League's own UI.</summary>
    public Task AnswerInClientAsync(bool accept) => UpdateReadyCheckAsync(check => check.PlayerResponse = accept ? "Accepted" : "Declined");

    /// <summary>Everyone accepted: the check reports EveryoneReady, ends, and champ select starts.</summary>
    public async Task EveryoneReadyAsync()
    {
        await UpdateReadyCheckAsync(check => check.State = "EveryoneReady").ConfigureAwait(false);
        await EndReadyCheckAsync().ConfigureAwait(false);
        await SetPhaseAsync("ChampSelect").ConfigureAwait(false);
    }

    /// <summary>Another player declined or timed out: the check ends and League puts the player back in queue.</summary>
    public async Task DeclinedByOtherPlayerAsync()
    {
        await UpdateReadyCheckAsync(check =>
        {
            check.State = "StrangerNotReady";
            check.DeclinerIds.Add(JsonValue.Create(4242L));
        }).ConfigureAwait(false);
        await EndReadyCheckAsync().ConfigureAwait(false);
        await SetPhaseAsync("Matchmaking").ConfigureAwait(false);
    }

    /// <summary>The local player never answered: the timer runs out and League drops the party out of queue.</summary>
    public async Task ExpireAsync()
    {
        await UpdateReadyCheckAsync(check =>
        {
            check.State = "PartyNotReady";
            if (check.PlayerResponse == "None") { check.PlayerResponse = "Declined"; }
            check.DodgeWarning = "Warning";
        }).ConfigureAwait(false);
        await EndReadyCheckAsync().ConfigureAwait(false);
        await SetPhaseAsync("Lobby").ConfigureAwait(false);
    }

    /// <summary>Sends a raw event frame, for malformed-event tests.</summary>
    public Task SendRawAsync(string frame) => BroadcastAsync(_ => true, frame);

    /// <summary>Aborts every event socket, as when the League UX process crashes but the API stays up.</summary>
    public void DropEventStreams()
    {
        foreach (var socket in sockets.Values) { socket.Socket.Abort(); }
    }

    /// <summary>League restarts: everything stops, then the API comes back on a new port and password.</summary>
    public async Task RestartAsync(bool keepReadyCheck = false)
    {
        await StopAsync().ConfigureAwait(false);
        lock (gate)
        {
            if (!keepReadyCheck) { readyCheck = null; phase = "None"; }
        }
        await StartCoreAsync().ConfigureAwait(false);
    }

    /// <summary>True when a connected event socket subscribed to <paramref name="topic"/>.</summary>
    public bool HasSubscriber(string topic) => sockets.Values.Any(socket => socket.Topics.ContainsKey(topic));

    /// <summary>Waits until every topic in <paramref name="topics"/> has a subscriber.</summary>
    public Task WaitForSubscribersAsync(TimeSpan timeout, params string[] topics)
        => WaitUntilAsync(() => topics.All(HasSubscriber), timeout);

    /// <summary>Waits until at least <paramref name="count"/> accept requests have arrived in total.</summary>
    public Task WaitForAcceptsAsync(int count, TimeSpan timeout) => WaitUntilAsync(() => AcceptCount >= count, timeout);

    /// <summary>Polls <paramref name="condition"/> until it holds, or throws <see cref="TimeoutException"/>.</summary>
    public static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) { throw new TimeoutException("Condition not met in time."); }
            await Task.Delay(20).ConfigureAwait(false);
        }
    }

    // ---- Lifecycle ------------------------------------------------------------------------

    public async Task StopAsync()
    {
        var running = app;
        app = null;
        if (running is null) { return; }
        DropEventStreams();
        await running.StopAsync().ConfigureAwait(false);
        await running.DisposeAsync().ConfigureAwait(false);
        certificate?.Dispose();
        certificate = null;
        log?.Invoke("Fake League client stopped.");
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private async Task StartCoreAsync()
    {
        Password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        certificate = CreateCertificate();
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel =>
            kestrel.Listen(IPAddress.Loopback, requestedPort, listen => listen.UseHttps(certificate)));
        var web = builder.Build();
        web.UseWebSockets();
        web.Use(AuthenticateAsync);
        web.Map("/", HandleRootAsync);
        web.MapGet(ReadyCheckRoute, () =>
        {
            lock (gate)
            {
                return readyCheck is null
                    ? Error(404, "Not attached to a matchmaking queue.")
                    : Results.Content(readyCheck.ToJson(time).ToJsonString(Json), "application/json");
            }
        });
        web.MapPost(AcceptRoute, AcceptAsync);
        web.MapGet(GameflowPhaseRoute, () => Results.Content(JsonSerializer.Serialize(Phase, Json), "application/json"));
        await web.StartAsync().ConfigureAwait(false);
        var address = web.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        Port = new Uri(address).Port;
        app = web;
        log?.Invoke($"Fake League client listening on https://127.0.0.1:{Port}/");
    }

    private async Task AuthenticateAsync(HttpContext context, RequestDelegate next)
    {
        var expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"riot:{Password}"));
        if (context.Request.Headers.Authorization.ToString() != expected)
        {
            Interlocked.Increment(ref unauthorizedCount);
            log?.Invoke($"401 {context.Request.Method} {context.Request.Path}");
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
        requests.Enqueue($"{context.Request.Method} {context.Request.Path}");
        await next(context).ConfigureAwait(false);
    }

    private async Task<IResult> AcceptAsync()
    {
        Interlocked.Increment(ref acceptCount);
        log?.Invoke("Accept received.");
        if (FailAccepts) { return Error(500, "Simulated accept failure."); }
        JsonObject updated;
        lock (gate)
        {
            if (readyCheck is null) { return Error(404, "Not attached to a matchmaking queue."); }
            readyCheck.PlayerResponse = "Accepted";
            updated = readyCheck.ToJson(time);
        }
        await PublishAsync(ReadyCheckRoute, "Update", updated).ConfigureAwait(false);
        return Results.NoContent();
    }

    private async Task HandleRootAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        using var socket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        var id = Guid.NewGuid();
        var entry = new EventSocket(socket);
        sockets[id] = entry;
        log?.Invoke("Event socket connected.");
        try
        {
            var buffer = new byte[4096];
            while (socket.State == WebSocketState.Open)
            {
                var result = await socket.ReceiveAsync(buffer, context.RequestAborted).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close) { break; }
                HandleWampMessage(entry, Encoding.UTF8.GetString(buffer, 0, result.Count));
            }
        }
        catch (Exception error) when (error is WebSocketException or OperationCanceledException or IOException) { }
        finally
        {
            sockets.TryRemove(id, out _);
            log?.Invoke("Event socket disconnected.");
        }
    }

    private void HandleWampMessage(EventSocket socket, string message)
    {
        // WAMP 1: [5, topic] subscribes and [6, topic] unsubscribes. League never acknowledges either.
        try
        {
            if (JsonNode.Parse(message) is JsonArray { Count: >= 2 } frame &&
                frame[0]?.GetValue<int>() is int type && frame[1]?.GetValue<string>() is { } topic)
            {
                if (type == 5) { socket.Topics[topic] = true; }
                else if (type == 6) { socket.Topics.TryRemove(topic, out _); }
                log?.Invoke($"WAMP {type} {topic}");
            }
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException) { }
    }

    private async Task UpdateReadyCheckAsync(Action<ReadyCheck> change)
    {
        JsonObject updated;
        lock (gate)
        {
            if (readyCheck is null) { throw new InvalidOperationException("No ready check is showing."); }
            change(readyCheck);
            updated = readyCheck.ToJson(time);
        }
        await PublishAsync(ReadyCheckRoute, "Update", updated).ConfigureAwait(false);
    }

    private async Task EndReadyCheckAsync()
    {
        lock (gate) { readyCheck = null; }
        await PublishAsync(ReadyCheckRoute, "Delete", null).ConfigureAwait(false);
    }

    private Task PublishAsync(string route, string eventType, JsonNode? data)
    {
        var topic = TopicFor(route);
        var payload = new JsonObject { ["data"] = data, ["eventType"] = eventType, ["uri"] = route };
        // A specific subscription receives its own topic name; the catch-all gets "OnJsonApiEvent".
        return Task.WhenAll(
            BroadcastAsync(s => s.Topics.ContainsKey(topic), new JsonArray(8, topic, payload.DeepClone()).ToJsonString(Json)),
            BroadcastAsync(s => s.Topics.ContainsKey(AllEventsTopic), new JsonArray(8, AllEventsTopic, payload.DeepClone()).ToJsonString(Json)));
    }

    private async Task BroadcastAsync(Func<EventSocket, bool> wants, string frame)
    {
        var bytes = Encoding.UTF8.GetBytes(frame);
        await publishing.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var socket in sockets.Values.Where(wants))
            {
                try
                {
                    if (socket.Socket.State == WebSocketState.Open)
                    {
                        await socket.Socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None).ConfigureAwait(false);
                    }
                }
                catch (Exception error) when (error is WebSocketException or ObjectDisposedException or IOException) { }
            }
        }
        finally { publishing.Release(); }
    }

    private static IResult Error(int status, string message) => Results.Json(
        new { errorCode = "RPC_ERROR", httpStatus = status, implementationDetails = new { }, message }, statusCode: status);

    private static X509Certificate2 CreateCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=127.0.0.1", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        using var ephemeral = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        // Windows SChannel cannot serve TLS from an ephemeral key, so round-trip through PKCS#12.
        return X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pkcs12), null);
    }

    private sealed record EventSocket(WebSocket Socket)
    {
        public ConcurrentDictionary<string, bool> Topics { get; } = new();
    }

    private sealed class ReadyCheck(long startedAt)
    {
        public string State { get; set; } = "InProgress";
        public string PlayerResponse { get; set; } = "None";
        public string DodgeWarning { get; set; } = "None";
        public JsonArray DeclinerIds { get; } = [];

        // League reports whole seconds elapsed since the check appeared.
        public JsonObject ToJson(TimeProvider time) => new()
        {
            ["declinerIds"] = DeclinerIds.DeepClone(),
            ["dodgeWarning"] = DodgeWarning,
            ["playerResponse"] = PlayerResponse,
            ["state"] = State,
            ["suppressUx"] = false,
            ["timer"] = Math.Floor(time.GetElapsedTime(startedAt).TotalSeconds)
        };
    }
}
