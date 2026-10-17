using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Jellyfin.Plugin.AdminToolkit.Core;

namespace Jellyfin.Plugin.AdminToolkit.Services;

/// <summary>Result of a permission check on one path.</summary>
public sealed record PermissionCheck(bool Ok, string Message, string? Path = null, string? Reason = null)
{
    public static PermissionCheck Allowed { get; } = new(true, "Writable.");
}

/// <summary>
/// Checks whether the Jellyfin process (user "abc" in the linuxserver image) can delete or write a path.
/// The default checks write nothing: on Linux they use access(2) with W_OK|X_OK, which honours the effective uid,
/// group membership and read-only mounts. Deleting a folder needs write+execute on its parent and on every folder
/// inside it. Messages state the facts only; they never suggest changing PUID/PGID, mounts or ownership.
/// </summary>
public static class PermissionProbe
{
    private const int WOk = 2;
    private const int XOk = 1;
    private const int ROk = 4;
    private const int MaxDirectories = 20000;

    /// <summary>Checks that every path can be deleted (file: parent writable; folder: parent and whole tree writable).</summary>
    public static PermissionCheck CheckDeletable(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            var r = CheckOne(path);
            if (!r.Ok)
            {
                return r;
            }
        }

        return PermissionCheck.Allowed;
    }

    /// <summary>Checks that a folder exists and a file could be created in it (no write is made).</summary>
    public static PermissionCheck CheckWritableDirectory(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return new PermissionCheck(false, $"Folder not found: {directory}. Is the drive mounted inside the Jellyfin container?", directory, "not found");
        }

        return Access(directory, WOk | XOk);
    }

    /// <summary>Checks that a file or folder can be read.</summary>
    public static PermissionCheck CheckReadable(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return new PermissionCheck(false, $"Not found on disk: {path}", path, "not found");
        }

        return Access(path, Directory.Exists(path) ? ROk | XOk : ROk);
    }

    /// <summary>
    /// The explicit "Test write" button: creates and immediately deletes an empty hidden file in the folder.
    /// Only runs when the admin asks for it.
    /// </summary>
    public static PermissionCheck ProbeWrite(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return new PermissionCheck(false, $"Folder not found: {directory}. Is the drive mounted inside the Jellyfin container?", directory, "not found");
        }

        var probe = Path.Combine(directory, ".admintoolkit-write-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (File.Create(probe, 1, FileOptions.DeleteOnClose))
            {
            }

            return new PermissionCheck(true, $"Jellyfin ({DescribeProcessUser()}) created and deleted a test file in {directory}.", directory);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            var reason = ex.Message.Contains("Read-only", StringComparison.OrdinalIgnoreCase) ? "read-only file system" : "permission denied";
            return Denied(directory, reason + " (" + ex.Message + ")");
        }
        finally
        {
            try
            {
                if (File.Exists(probe))
                {
                    File.Delete(probe);
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
            }
        }
    }

    /// <summary>Copy-ready commands an admin can run themselves when Jellyfin cannot delete the files.</summary>
    public static List<string> ManualDeleteCommands(IEnumerable<string> paths, string containerName)
    {
        var list = paths.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct().ToList();
        var container = string.IsNullOrWhiteSpace(containerName) ? "jellyfin" : containerName.Trim();
        var cmds = new List<string>();
        foreach (var p in list)
        {
            cmds.Add($"docker exec -u 0 {PathText.ShellQuote(container)} rm -r -- {PathText.ShellQuote(p)}");
        }

        return cmds;
    }

    public static string DescribeProcessUser()
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD())
        {
            try
            {
                return $"uid {getuid()}, gid {getgid()}";
            }
            catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
            {
            }
        }

        return Environment.UserName;
    }

    private static PermissionCheck CheckOne(string path)
    {
        bool isDir = Directory.Exists(path);
        bool isFile = !isDir && File.Exists(path);
        if (!isDir && !isFile)
        {
            return new PermissionCheck(false, $"Not found on disk: {path}. Is the drive mounted inside the Jellyfin container?", path, "not found");
        }

        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path));
        if (parent is not null)
        {
            var p = Access(parent, WOk | XOk);
            if (!p.Ok)
            {
                return p;
            }
        }

        if (!isDir)
        {
            return PermissionCheck.Allowed;
        }

        int count = 0;
        var stack = new Stack<string>();
        stack.Push(path);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            var w = Access(dir, WOk | XOk);
            if (!w.Ok)
            {
                return w;
            }

            if (++count > MaxDirectories)
            {
                break;
            }

            IEnumerable<string> subdirs;
            try
            {
                subdirs = Directory.EnumerateDirectories(dir).ToList();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                return Denied(dir, "cannot list the folder's contents");
            }

            foreach (var sub in subdirs)
            {
                if ((File.GetAttributes(sub) & FileAttributes.ReparsePoint) == 0)
                {
                    stack.Push(sub);
                }
            }
        }

        return PermissionCheck.Allowed;
    }

    private static PermissionCheck Access(string path, int mode)
    {
        if (!(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD()))
        {
            try
            {
                if ((mode & WOk) != 0 && Directory.Exists(path) && (new DirectoryInfo(path).Attributes & FileAttributes.ReadOnly) != 0)
                {
                    return Denied(path, "read-only");
                }

                return PermissionCheck.Allowed;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Denied(path, ex.Message);
            }
        }

        try
        {
            if (access(path, mode) == 0)
            {
                return PermissionCheck.Allowed;
            }

            int errno = Marshal.GetLastPInvokeError();
            string reason = errno switch
            {
                13 => "permission denied",
                30 => "read-only file system (this folder is mounted read-only into the Jellyfin container)",
                2 => "not found",
                1 => "operation not permitted",
                _ => "error " + errno
            };
            return Denied(path, reason);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // Cannot check on this platform; Jellyfin's own delete still reports failures.
            return PermissionCheck.Allowed;
        }
    }

    private static PermissionCheck Denied(string path, string reason)
        => new(false, $"Jellyfin ({DescribeProcessUser()}) cannot write to \"{path}\": {reason}.", path, reason);

    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    private static extern int access([MarshalAs(UnmanagedType.LPUTF8Str)] string pathname, int mode);

    [DllImport("libc")]
    private static extern uint getuid();

    [DllImport("libc")]
    private static extern uint getgid();
}

/// <summary>Disk space via DriveInfo (statvfs on Linux), which reports the file system containing the path.</summary>
public sealed class SystemDriveSpace : IDriveSpaceProvider
{
    public DriveSpace? GetSpace(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                return null;
            }

            var d = new DriveInfo(path);
            return d.TotalSize > 0 ? new DriveSpace(d.TotalSize, d.AvailableFreeSpace) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}
