using System.ComponentModel;
using System.Diagnostics;

namespace AFKtor.Tray;

internal static class LeagueClientDiscovery
{
    public static Task<string?> ReadLockfileAsync(CancellationToken cancellationToken)
        => Task.Run(() => ReadLockfile(cancellationToken), cancellationToken);

    private static string? ReadLockfile(CancellationToken cancellationToken)
    {
        var processes = Process.GetProcessesByName("LeagueClient");
        try
        {
            foreach (var process in processes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var executable = process.MainModule?.FileName;
                    if (executable is null)
                    {
                        continue;
                    }

                    var path = Path.Combine(Path.GetDirectoryName(executable)!, "lockfile");
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete);
                    using var reader = new StreamReader(stream);
                    var contents = reader.ReadToEnd();
                    var fields = contents.Trim().Split(':');
                    // Reject a stale lockfile left by a previous client process.
                    if (fields.Length == 5 && int.TryParse(fields[1], out var processId) &&
                        processId == process.Id && !process.HasExited)
                    {
                        return contents;
                    }
                }
                catch (Exception error) when (error is Win32Exception or InvalidOperationException or
                    IOException or UnauthorizedAccessException)
                {
                    // Startup, exit and file replacement races are retried on the next poll.
                }
            }

            return null;
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }
}
