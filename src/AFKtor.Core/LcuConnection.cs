using System.Globalization;
using System.Net.Http.Headers;
using System.Text;

namespace AFKtor.Core;

public sealed class LcuConnection
{
    private readonly string password;
    public int ProcessId { get; }
    public int Port { get; }

    private LcuConnection(int processId, int port, string password)
        => (ProcessId, Port, this.password) = (processId, port, password);

    public static LcuConnection? Parse(string? contents)
    {
        var fields = contents?.Trim().Split(':');
        if (fields is not { Length: 5 } || fields[0] != "LeagueClient" ||
            !int.TryParse(fields[1], out var processId) || processId <= 0 ||
            !int.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var port) ||
            port is < 1 or > 65535 || string.IsNullOrWhiteSpace(fields[3]) || fields[4] != "https")
        {
            return null;
        }
        return new LcuConnection(processId, port, fields[3]);
    }

    public Uri Endpoint(string path) => new($"https://127.0.0.1:{Port}/{path.TrimStart('/')}");
    public AuthenticationHeaderValue Authorization => new("Basic",
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"riot:{password}")));
    public bool Matches(LcuConnection other)
        => ProcessId == other.ProcessId && Port == other.Port && password == other.password;
    public override string ToString() => $"LeagueClient ({ProcessId})";
}
