using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

namespace AFKtor.Diagnostics;

internal static class DiscoveryFile
{
    public static string Publish(string root, DiscoveryDescriptor descriptor, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(root);
        var directory = Path.Combine(root, descriptor.RunId.ToString("N"));
        if (OperatingSystem.IsWindows())
        {
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(true, false);
            var user = WindowsIdentity.GetCurrent().User ?? throw new UnauthorizedAccessException();
            security.SetOwner(user);
            security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(directory).Create(security);
        }
        else Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var path = Path.Combine(directory, "instance.json");
        var temporary = path + ".tmp";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var file = new FileStream(temporary, options)) JsonSerializer.Serialize(file, descriptor, DiagnosticJson.Options);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path);
            return path;
        }
        catch { Remove(temporary); throw; }
    }

    public static void Remove(string? path)
    {
        if (path is null) return;
        try { File.Delete(path); Directory.Delete(Path.GetDirectoryName(path)!); }
        catch (Exception) { /* Failure must not affect exit; stale descriptors are rejected by run identity. */ }
    }
}
