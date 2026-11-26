using System.ComponentModel;
using System.Diagnostics;

namespace AFKtor.Tray;

internal static class LeagueClientDiscovery
{
    public static Task<string?> ReadLockfileAsync(CancellationToken cancellationToken)
        => Task.Run(() => ReadLockfile(cancellationToken), cancellationToken);

    private static string? ReadLockfile(CancellationToken cancellationToken)
    {
        if (TestOverrides.LockfilePath is { } overridePath) { return ReadOverrideLockfile(overridePath); }
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

                    var contents = ReadShared(Path.Combine(Path.GetDirectoryName(executable)!, "lockfile"));
                    // Reject a stale lockfile left by a previous client process.
                    if (ProcessIdOf(contents) == process.Id && !process.HasExited)
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

    /// <summary>
    /// A fake League client writes its own lockfile. It is used only while the process it
    /// names is alive, the same staleness rule the real lockfile gets.
    /// </summary>
    private static string? ReadOverrideLockfile(string path)
    {
        try
        {
            var contents = ReadShared(path);
            if (ProcessIdOf(contents) is not { } processId) { return null; }
            using var process = Process.GetProcessById(processId);
            return process.HasExited ? null : contents;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or
            IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static int? ProcessIdOf(string contents)
    {
        var fields = contents.Trim().Split(':');
        return fields.Length == 5 && int.TryParse(fields[1], out var processId) ? processId : null;
    }
}
