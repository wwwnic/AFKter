using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AFKtor.Core.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AFKtor.Diagnostics;

/// <summary>The same loopback-only host is composed by WinForms and Linux tests.</summary>
public sealed class DiagnosticHost : IAsyncDisposable
{
    private readonly DiagnosticCollector collector;
    private readonly IDiagnosticHistory history;
    private readonly string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private readonly SemaphoreSlim requests = new(DiagnosticLimits.ConcurrentRequests);
    private readonly CancellationTokenSource lifetime = new();
    private WebApplication? app;
    private int disposed;
    public string? Endpoint { get; private set; }
    public string? DiscoveryPath { get; private set; }

    private DiagnosticHost(DiagnosticCollector collector, IDiagnosticHistory? history)
    { this.collector = collector; this.history = history ?? collector; }

    public static async Task<DiagnosticHost> StartAsync(DiagnosticCollector collector, string discoveryDirectory,
        IDiagnosticHistory? history = null, CancellationToken cancellationToken = default)
    {
        var host = new DiagnosticHost(collector, history);
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, host.lifetime.Token);
        startup.CancelAfter(TimeSpan.FromSeconds(DiagnosticLimits.RequestSeconds));
        // Directory access and framework initialization cannot block the application thread.
        var startupToken = startup.Token;
        var work = Task.Run(() => host.StartCoreAsync(discoveryDirectory, startupToken));
        try { await work.WaitAsync(startup.Token).ConfigureAwait(false); }
        catch (Exception)
        {
            collector.SetApiHealth(false);
            collector.SetDiscoveryHealth(false);
            host.lifetime.Cancel();
            _ = work.ContinueWith(async _ => await host.DisposeAsync(), TaskScheduler.Default).Unwrap();
        }
        return host;
    }

    private async Task StartCoreAsync(string directory, CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders();
        builder.Configuration.Sources.Clear();
        builder.WebHost.ConfigureKestrel(server =>
        {
            server.Listen(IPAddress.Loopback, 0);
            server.Limits.MaxRequestBodySize = 0;
            server.Limits.MaxRequestHeadersTotalSize = 8192;
            server.Limits.MaxRequestLineSize = 2048;
            server.Limits.MaxConcurrentConnections = 32;
            server.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(2);
            server.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(2);
        });
        app = builder.Build();
        app.Run(HandleAsync);
        await app.StartAsync(cancellationToken).ConfigureAwait(false);
        Endpoint = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        collector.SetApiHealth(true);
        try
        {
            var path = DiscoveryFile.Publish(directory, new(1, collector.RunId, collector.Build, Endpoint, token), cancellationToken);
            if (lifetime.IsCancellationRequested || cancellationToken.IsCancellationRequested)
            { DiscoveryFile.Remove(path); return; }
            DiscoveryPath = path;
            collector.SetDiscoveryHealth(true);
        }
        catch (Exception) { collector.SetDiscoveryHealth(false); }
    }

    private async Task HandleAsync(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        if (!Authenticated(context)) { await ErrorAsync(context, 401, "unauthorized"); return; }
        if (context.Request.Method != "GET") { context.Response.Headers.Allow = "GET"; await ErrorAsync(context, 405, "methodNotAllowed"); return; }
        if (context.Request.Headers.TryGetValue("X-AFKtor-Run-Id", out var expected) &&
            (!Guid.TryParse(expected, out var runId) || runId != collector.RunId))
        { await ErrorAsync(context, 409, "runMismatch"); return; }
        if (!requests.Wait(0)) { await ErrorAsync(context, 429, "busy"); return; }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(DiagnosticLimits.RequestSeconds));
        Task<(int Status, byte[] Body)>? work = null;
        try
        {
            // Copy bounded request metadata before handing work to an isolated reader.
            var path = context.Request.Path.Value;
            var query = context.Request.Query.ToDictionary(e => e.Key, e => e.Value.ToArray());
            var requestCancellation = deadline.Token;
            work = Task.Run(() => ReadResponse(path, query, requestCancellation));
            var result = await work.WaitAsync(deadline.Token).ConfigureAwait(false);
            context.Response.StatusCode = result.Status;
            context.Response.ContentType = "application/json";
            await context.Response.Body.WriteAsync(result.Body, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { context.Abort(); }
        catch (Exception) { if (!context.Response.HasStarted) await ErrorAsync(context, 503, "unavailable"); }
        finally
        {
            if (work is null || work.IsCompleted) requests.Release();
            else _ = work.ContinueWith(_ => requests.Release(), TaskScheduler.Default); // Hung readers keep their slot.
        }
    }

    private bool Authenticated(HttpContext context)
    {
        var header = context.Request.Headers.Authorization;
        if (header.Count != 1 || header[0] is not { } value || value.Length != 71 || !value.StartsWith("Bearer ", StringComparison.Ordinal)) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(value.AsSpan(7).ToString()), Encoding.ASCII.GetBytes(token));
    }

    private (int, byte[]) ReadResponse(string? path, Dictionary<string, string?[]> query, CancellationToken cancellationToken)
    {
        object? response;
        if (path == "/diagnostics/state" && query.Count == 0)
        {
            var state = collector.GetState();
            // The sealed merger exposes an immediate immutable health snapshot, including idle cleanup.
            if (history is RetainedDiagnosticHistory retained)
            {
                var health = retained.Status;
                state = state with { Health = state.Health with { Storage = state.Health.FailureCategory is "sinkUnavailable" or "shutdownTimeout" ? "unavailable" : health.Availability, Persistence = health,
                    Truncated = health.Truncated, FailureCategory = health.FailureCategory ?? state.Health.FailureCategory } };
            }
            response = state;
        }
        else if (path == "/diagnostics/runs" && query.Count == 0) response = new { schemaVersion = 1, runs = history.GetRuns(cancellationToken) };
        else if (path == "/diagnostics/events")
        {
            if (query.Any(e => e.Value.Length != 1 || e.Key is not ("runId" or "afterSequence" or "limit"))) return Error(400, "invalidQuery");
            var run = collector.RunId;
            long after = 0;
            var limit = 50;
            if (query.TryGetValue("runId", out var id) && !Guid.TryParse(id[0], out run) ||
                query.TryGetValue("afterSequence", out var cursor) && !long.TryParse(cursor[0], NumberStyles.None, CultureInfo.InvariantCulture, out after) ||
                query.TryGetValue("limit", out var count) && !int.TryParse(count[0], NumberStyles.None, CultureInfo.InvariantCulture, out limit) ||
                after < 0 || limit < 1 || limit > DiagnosticLimits.PageEvents) return Error(400, "invalidQuery");
            response = history.GetEvents(run, after, limit, cancellationToken);
            if (response is null) return history is IDiagnosticSinkHealth health && health.Status.Availability != "available"
                ? Error(503, "storageUnavailable") : Error(404, "runUnavailable");
        }
        else return Error(path is "/diagnostics/state" or "/diagnostics/runs" ? 400 : 404, "invalidRouteOrQuery");
        try
        {
            using var stream = new BoundedResponseStream();
            JsonSerializer.Serialize(stream, response, DiagnosticJson.Options);
            return (200, stream.ToArray());
        }
        catch (ResponseLimitException) { return Error(503, "responseTooLarge"); }
    }

    private static (int, byte[]) Error(int status, string code) => (status, JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, error = code }, DiagnosticJson.Options));
    private static async Task ErrorAsync(HttpContext context, int status, string code)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        timeout.CancelAfter(TimeSpan.FromSeconds(DiagnosticLimits.RequestSeconds));
        try { await context.Response.Body.WriteAsync(Error(status, code).Item2, timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { context.Abort(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        lifetime.Cancel();
        collector.SetApiHealth(false);
        var cleanup = Task.Run(async () =>
        {
            DiscoveryFile.Remove(DiscoveryPath);
            if (app is not null)
            {
                await app.StopAsync().ConfigureAwait(false);
                await app.DisposeAsync().ConfigureAwait(false);
            }
        });
        try { await cleanup.WaitAsync(TimeSpan.FromMilliseconds(DiagnosticLimits.ShutdownMilliseconds)).ConfigureAwait(false); }
        catch (Exception) { /* Diagnostic exit has a hard budget. */ }
    }

    private sealed class ResponseLimitException : Exception;
    private sealed class BoundedResponseStream : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count) { Check(count); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { Check(buffer.Length); base.Write(buffer); }
        private void Check(int count) { if (Length + count > DiagnosticLimits.ResponseBytes) throw new ResponseLimitException(); }
    }
}
