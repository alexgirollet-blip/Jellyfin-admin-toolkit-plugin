using System;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.AdminToolkit.Configuration;
using Jellyfin.Plugin.AdminToolkit.Core;
using Jellyfin.Plugin.AdminToolkit.Services;
using Jellyfin.Plugin.AdminToolkit.Storage;
using Xunit;

namespace Jellyfin.Plugin.AdminToolkit.Tests;

public sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "attest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            foreach (var f in Directory.EnumerateFileSystemEntries(Path, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(f, FileAttributes.Normal);
            }

            Directory.Delete(Path, true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

public class StorageTests
{
    [Fact]
    public void JsonStore_RoundTrips_AndReturnsNullForMissingOrDamaged()
    {
        using var t = new TempDir();
        var store = new JsonStore(System.IO.Path.Combine(t.Path, "admintoolkit"));
        Assert.Null(store.Read<ActivityEntry>("x.json"));
        Assert.False(Directory.Exists(store.Directory));

        store.Write("x.json", new ActivityEntry { Target = "hello" });
        Assert.Equal("hello", store.Read<ActivityEntry>("x.json")!.Target);

        File.WriteAllText(System.IO.Path.Combine(store.Directory, "bad.json"), "{not json");
        Assert.Null(store.Read<ActivityEntry>("bad.json"));
    }

    [Fact]
    public void ActivityLog_FiltersAndPrunesByRetention()
    {
        using var t = new TempDir();
        var now = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        var log = new ActivityLog(t.Path, () => 30, () => now);
        log.Add("alex", "Duplicates", "Remove", "Show (2015)", "OK", "deleted /x");
        log.Add("alex", "Images", "Apply", "Film", "Failed", "boom");

        Assert.Equal(2, log.Read(new ActivityFilter()).Count);
        Assert.Single(log.Read(new ActivityFilter { Area = "images" }));
        Assert.Single(log.Read(new ActivityFilter { Result = "OK" }));
        Assert.Single(log.Read(new ActivityFilter { Text = "/x" }));

        now = now.AddDays(45);
        log.Add("alex", "Settings", "Save", "settings", "OK");
        // Adding after the retention period prunes old entries automatically.
        Assert.Equal("Settings", Assert.Single(log.Read(new ActivityFilter())).Area);
        Assert.Equal(0, log.Prune());
    }

    [Fact]
    public void Csv_QuotesAndNeutralisesFormulas()
    {
        var csv = Csv.Write(new[] { "a", "b" }, new[] { new object?[] { "x,y", "=cmd()" }, new object?[] { -5, null } });
        var lines = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToArray();
        Assert.Equal("a,b", lines[0]);
        Assert.Equal("\"x,y\",'=cmd()", lines[1]);
        Assert.Equal("-5,", lines[2]);
    }
}

public class PermissionTests
{
    [Fact]
    public void WritableFolder_IsDeletable()
    {
        using var t = new TempDir();
        var show = Directory.CreateDirectory(System.IO.Path.Combine(t.Path, "Show", "Season 1")).Parent!.FullName;
        File.WriteAllText(System.IO.Path.Combine(show, "Season 1", "e1.mkv"), "x");
        Assert.True(PermissionProbe.CheckDeletable(new[] { show }).Ok);
        Assert.True(PermissionProbe.CheckWritableDirectory(show).Ok);
        Assert.True(PermissionProbe.CheckReadable(show).Ok);
    }

    [Fact]
    public void MissingPath_ReportsNotMounted_WithoutPuidAdvice()
    {
        var r = PermissionProbe.CheckDeletable(new[] { "/definitely/not/here/Show" });
        Assert.False(r.Ok);
        Assert.Equal("not found", r.Reason);
        Assert.Contains("mounted", r.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("PUID", r.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PGID", r.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("chown", r.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReadOnlySubfolder_BlocksDeletion_WhenNotRoot()
    {
        if (!OperatingSystem.IsLinux() || PermissionProbe.DescribeProcessUser().StartsWith("uid 0,", StringComparison.Ordinal))
        {
            // root bypasses permission bits, so this cannot be demonstrated as root (CI runs as a normal user).
            return;
        }

        using var t = new TempDir();
        var show = System.IO.Path.Combine(t.Path, "Show");
        var season = Directory.CreateDirectory(System.IO.Path.Combine(show, "Season 1")).FullName;
        File.SetUnixFileMode(season, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var r = PermissionProbe.CheckDeletable(new[] { show });
            Assert.False(r.Ok);
            Assert.Equal("permission denied", r.Reason);
            Assert.Equal(season, r.Path);
            Assert.DoesNotContain("PUID", r.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.SetUnixFileMode(season, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void ProbeWrite_CreatesAndRemovesItsTestFile()
    {
        using var t = new TempDir();
        var r = PermissionProbe.ProbeWrite(t.Path);
        Assert.True(r.Ok, r.Message);
        Assert.Empty(Directory.EnumerateFileSystemEntries(t.Path));
        Assert.False(PermissionProbe.ProbeWrite(System.IO.Path.Combine(t.Path, "nope")).Ok);
    }

    [Fact]
    public void ManualCommands_AreQuotedAndUseContainerPaths()
    {
        var cmds = PermissionProbe.ManualDeleteCommands(new[] { "/data/8tb1/TV/It's Always Sunny", "/data/8tb1/TV/It's Always Sunny" }, "jellyfin");
        var cmd = Assert.Single(cmds);
        Assert.Equal("docker exec -u 0 'jellyfin' rm -r -- '/data/8tb1/TV/It'\\''s Always Sunny'", cmd);
    }

    [Fact]
    public void DriveSpace_ReadsSpaceOfAnyFolder()
    {
        using var t = new TempDir();
        var s = new SystemDriveSpace().GetSpace(t.Path);
        Assert.NotNull(s);
        Assert.True(s!.TotalBytes > 0);
        Assert.True(s.FreeBytes <= s.TotalBytes);
        Assert.Null(new SystemDriveSpace().GetSpace("/definitely/not/here"));
    }
}

public class SafetyGuardTests
{
    [Fact]
    public void SafeMode_RefusesChanges_With403()
    {
        var cfg = new PluginConfiguration();
        var guard = new SafetyGuard(() => cfg);
        var ex = Assert.Throws<ToolkitException>(() => guard.EnsureChangesAllowed("remove a duplicate"));
        Assert.Equal(403, ex.Status);
        Assert.Equal("safe_mode", ex.Code);
        cfg.SafeMode = false;
        guard.EnsureChangesAllowed("remove a duplicate");
    }

    [Fact]
    public void Token_IsOneTime_BoundToPlanAndUser_AndExpires()
    {
        var cfg = new PluginConfiguration { SafeMode = false };
        var now = DateTime.UtcNow;
        var guard = new SafetyGuard(() => cfg, () => now);
        var admin = Guid.NewGuid();

        var t1 = guard.IssueToken("plan-a", admin);
        Assert.Equal("plan_changed", Assert.Throws<ToolkitException>(() => guard.ConsumeToken(t1, "plan-b", admin, false, null)).Code);

        var t2 = guard.IssueToken("plan-a", admin);
        Assert.Equal("confirmation_user", Assert.Throws<ToolkitException>(() => guard.ConsumeToken(t2, "plan-a", Guid.NewGuid(), false, null)).Code);

        var t3 = guard.IssueToken("plan-a", admin);
        guard.ConsumeToken(t3, "plan-a", admin, false, null);
        Assert.Equal("confirmation_required", Assert.Throws<ToolkitException>(() => guard.ConsumeToken(t3, "plan-a", admin, false, null)).Code);

        var t4 = guard.IssueToken("plan-a", admin);
        now = now.AddMinutes(11);
        Assert.Equal("confirmation_expired", Assert.Throws<ToolkitException>(() => guard.ConsumeToken(t4, "plan-a", admin, false, null)).Code);
    }

    [Fact]
    public void Destructive_NeedsTypedDelete_AtDefaultLevel()
    {
        var cfg = new PluginConfiguration { SafeMode = false };
        var guard = new SafetyGuard(() => cfg);
        var admin = Guid.NewGuid();
        var t = guard.IssueToken("p", admin);
        Assert.Equal("type_delete", Assert.Throws<ToolkitException>(() => guard.ConsumeToken(t, "p", admin, true, "delete")).Code);
        guard.ConsumeToken(guard.IssueToken("p", admin), "p", admin, true, "DELETE");

        cfg.ConfirmationLevel = ConfirmationLevel.Dialog;
        guard.ConsumeToken(guard.IssueToken("p", admin), "p", admin, true, null);
    }
}
