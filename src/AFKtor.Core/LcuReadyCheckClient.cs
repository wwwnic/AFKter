using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace AFKtor.Core;

public sealed class LcuReadyCheckClient(
    LcuConnection connection,
    HttpClient client,
    Func<CancellationToken, Task<WebSocket>>? connectSocket = null) : IReadyCheckClient
{
    private const string Route = "/lol-matchmaking/v1/ready-check";
    private const string Topic = "OnJsonApiEvent_lol-matchmaking_v1_ready-check";

    public async Task<ReadyCheckSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        using var request = Request(HttpMethod.Get, Route);
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return ReadyCheckSnapshot.Inactive;
        }
        response.EnsureSuccessStatusCode();
        return ReadyCheckSnapshot.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
    }

    public async Task AcceptAsync(CancellationToken cancellationToken)
    {
        using var request = Request(HttpMethod.Post, Route + "/accept");
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    private HttpRequestMessage Request(HttpMethod method, string route)
    {
        var request = new HttpRequestMessage(method, connection.Endpoint(route));
        request.Headers.Authorization = connection.Authorization;
        return request;
    }

    private async Task<WebSocket> ConnectAsync(CancellationToken cancellationToken)
    {
        var socket = new ClientWebSocket();
        try
        {
            socket.Options.SetRequestHeader("Authorization", connection.Authorization.ToString());
            socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(1);
            socket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(3);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            // The caller's loopback-only handler disables redirects and proxies and
            // trusts the LCU certificate. Sharing it also applies that policy to WSS.
            await socket.ConnectAsync(new Uri($"wss://127.0.0.1:{connection.Port}/"), client, timeout.Token).ConfigureAwait(false);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public async Task RunAsync(Action<ReadyCheckUpdate> receive, CancellationToken cancellationToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var socket = await (connectSocket ?? ConnectAsync)(lifetime.Token).ConfigureAwait(false);
        await socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes($"[5,\"{Topic}\"]")),
            WebSocketMessageType.Text, true, lifetime.Token).ConfigureAwait(false);
        var gate = new object();
        long version = 0;
        var receiving = ReceiveAsync(socket, update =>
        {
            lock (gate)
            {
                version++;
                receive(update);
            }
        }, lifetime.Token);
        var seeding = ReadAsync(lifetime.Token);
        try
        {
            if (await Task.WhenAny(seeding, receiving).ConfigureAwait(false) == receiving)
            {
                await receiving.ConfigureAwait(false);
            }
            var snapshot = await seeding.ConfigureAwait(false);
            lock (gate)
            {
                // Events win over the seed GET, which can have been captured earlier.
                if (version == 0 && !receiving.IsCompleted)
                {
                    receive(new(snapshot, IsInitial: true));
                }
            }
            await receiving.ConfigureAwait(false);
        }
        finally
        {
            lifetime.Cancel();
            try { await receiving.ConfigureAwait(false); }
            catch (Exception error) when (error is OperationCanceledException or WebSocketException or IOException or InvalidDataException) { }
            try { await seeding.ConfigureAwait(false); }
            catch (Exception error) when (error is OperationCanceledException or HttpRequestException or IOException or InvalidDataException) { }
        }
    }

    private static async Task ReceiveAsync(WebSocket socket, Action<ReadyCheckUpdate> receive, CancellationToken token)
    {
        var buffer = new byte[4096];
        while (true)
        {
            using var message = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    throw new IOException("LCU event stream closed.");
                }
                if (result.MessageType != WebSocketMessageType.Text || message.Length + result.Count > 65536)
                {
                    throw new InvalidDataException("Invalid LCU event frame.");
                }
                message.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);
            var update = DecodeEvent(Encoding.UTF8.GetString(message.ToArray()));
            if (update is not null) { receive(update); }
        }
    }

    public static ReadyCheckUpdate? DecodeEvent(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() < 3 ||
                root[0].ValueKind != JsonValueKind.Number || !root[0].TryGetInt32(out var type) || type != 8)
            {
                return null;
            }
            if (root[1].ValueKind != JsonValueKind.String || root[1].GetString() != Topic) { return null; }
            var payload = root[2];
            if (payload.ValueKind != JsonValueKind.Object ||
                !payload.TryGetProperty("uri", out var uri) || uri.ValueKind != JsonValueKind.String || uri.GetString() != Route ||
                !payload.TryGetProperty("eventType", out var eventType) || eventType.ValueKind != JsonValueKind.String)
            {
                throw new InvalidDataException("Malformed LCU ready-check event.");
            }
            if (eventType.GetString() == "Delete") { return new(ReadyCheckSnapshot.Inactive); }
            if (eventType.GetString() is not ("Create" or "Update") || !payload.TryGetProperty("data", out var data))
            {
                throw new InvalidDataException("Unknown LCU ready-check event.");
            }
            return new(ReadyCheckSnapshot.Parse(data.GetRawText()), eventType.GetString() == "Create");
        }
        catch (JsonException)
        {
            throw new InvalidDataException("Malformed LCU event JSON.");
        }
    }
}
