using System.Globalization;
using System.Net.Http.Headers;
using System.Text;

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
        var fields = contents?.Trim().Split(':');
        if (fields is not { Length: 5 } || fields[0] != "LeagueClient" ||
            !int.TryParse(fields[1], out var processId) || processId <= 0 ||
            !int.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var port) ||
            port is < 1 or > 65535 || string.IsNullOrWhiteSpace(fields[3]) || fields[4] != "https")
        {
            return false;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://127.0.0.1:{port}/lol-gameflow/v1/gameflow-phase");
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"riot:{fields[3]}")));
        using var response = await client.SendAsync(request, cancellationToken);
        return response.IsSuccessStatusCode;
    }
}
