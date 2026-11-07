namespace AFKtor.Core;

public sealed class LcuConnectionProbe(
    Func<CancellationToken, Task<string?>> readLockfile,
    HttpClient client) : ILeagueConnectionProbe
{
    public async Task<bool> IsConnectedAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await ProbeAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception error) when (error is HttpRequestException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private async Task<bool> ProbeAsync(CancellationToken cancellationToken)
    {
        var contents = await readLockfile(cancellationToken);
        var connection = LcuConnection.Parse(contents);
        if (connection is null) { return false; }
        using var request = new HttpRequestMessage(HttpMethod.Get,
            connection.Endpoint("/lol-gameflow/v1/gameflow-phase"));
        request.Headers.Authorization = connection.Authorization;
        using var response = await client.SendAsync(request, cancellationToken);
        return response.IsSuccessStatusCode;
    }
}
