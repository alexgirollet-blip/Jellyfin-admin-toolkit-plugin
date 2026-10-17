using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Jellyfin.Plugin.AdminToolkit.Adapters;
using Jellyfin.Plugin.AdminToolkit.Configuration;
using Jellyfin.Plugin.AdminToolkit.Core;
using Jellyfin.Plugin.AdminToolkit.Services;
using Jellyfin.Plugin.AdminToolkit.Storage;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.AdminToolkit.Api;

/// <summary>Plugin status, self-check, libraries and background scan states.</summary>
[Route("AdminToolkit")]
public sealed class StatusController : ToolkitControllerBase
{
    private readonly SelfCheckService _selfCheck;
    private readonly ScanRunner _scans;
    private readonly ILibraryAdapter _library;
    private readonly JsonStore _store;
    private readonly SafetyGuard _guard;

    public StatusController(SelfCheckService selfCheck, ScanRunner scans, ILibraryAdapter library, JsonStore store, SafetyGuard guard)
    {
        _selfCheck = selfCheck;
        _scans = scans;
        _library = library;
        _store = store;
        _guard = guard;
    }

    [HttpGet("Status")]
    public ActionResult Status()
    {
        var cfg = PluginState.Config;
        return Json(new
        {
            pluginVersion = typeof(Plugin).Assembly.GetName().Version?.ToString(),
            builtForJellyfin = ErrorText.BuiltForJellyfin,
            serverAssemblies = SelfCheckService.ServerVersion,
            safeMode = _guard.SafeMode,
            dryRunDefault = cfg.DryRunDefault,
            confirmationLevel = cfg.ConfirmationLevel.ToString(),
            processUser = PermissionProbe.DescribeProcessUser(),
            dataDirectory = _store.Directory,
            scans = _scans.All(),
            cache = new
            {
                duplicates = _store.LastWrite(DuplicateService.CacheFile),
                health = _store.LastWrite(HealthService.CacheFile),
                storage = _store.LastWrite(StorageService.CacheFile)
            }
        });
    }

    [HttpGet("SelfCheck")]
    public ActionResult SelfCheck() => Json(_selfCheck.Run());

    [HttpGet("Libraries")]
    public ActionResult Libraries() => Json(_library.GetLibraries());

    [HttpGet("Scans")]
    public ActionResult Scans() => Json(_scans.All());

    [HttpPost("Scans/{kind}/Cancel")]
    public ActionResult Cancel([FromRoute] string kind) => Json(new { cancelled = _scans.Cancel(kind) });
}

/// <summary>Duplicates tab.</summary>
[Route("AdminToolkit/Duplicates")]
public sealed class DuplicatesController : ToolkitControllerBase
{
    private readonly DuplicateService _svc;
    private readonly ScanRunner _scans;

    public DuplicatesController(DuplicateService svc, ScanRunner scans)
    {
        _svc = svc;
        _scans = scans;
    }

    [HttpGet]
    public ActionResult Get()
    {
        var cache = _svc.GetCached();
        var choices = _svc.GetChoices();
        return Json(new { scanned = cache is not null, result = cache, marked = choices.Marked, scan = _scans.Get("duplicates") });
    }

    [HttpPost("Scan")]
    public ActionResult Scan()
    {
        var user = CurrentUserName;
        return Json(_scans.Start("duplicates", user, (p, ct) => _svc.ScanAsync(user, p, ct)), 202);
    }

    [HttpPost("Groups/{groupId}/Keep")]
    public async Task<ActionResult> Keep([FromRoute] string groupId)
    {
        var body = await ReadBodyAsync<KeepRequest>().ConfigureAwait(false);
        return Json(_svc.SetKeep(groupId, body.KeepItemId, CurrentUserName));
    }

    [HttpPost("Mark")]
    public async Task<ActionResult> Mark()
    {
        var body = await ReadBodyAsync<MarkRequest>().ConfigureAwait(false);
        return Json(_svc.Mark(body.GroupIds, body.Marked, CurrentUserName));
    }

    [HttpGet("Export")]
    public ActionResult Export([FromQuery] string format = "csv", [FromQuery] string scope = "marked")
    {
        bool recommended = string.Equals(scope, "recommended", StringComparison.OrdinalIgnoreCase);
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture);
        if (string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
        {
            return File(Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(_svc.ExportJson(recommended), Options)), "application/json", $"admin-toolkit-removal-list-{stamp}.json");
        }

        return File(Encoding.UTF8.GetBytes(_svc.ExportCsv(recommended)), "text/csv", $"admin-toolkit-removal-list-{stamp}.csv");
    }

    [HttpPost("Plan")]
    public async Task<ActionResult> Plan()
    {
        var req = await ReadBodyAsync<RemovalRequest>().ConfigureAwait(false);
        return Json(await _svc.PlanAsync(req, CurrentUserId, HttpContext.RequestAborted).ConfigureAwait(false));
    }

    [HttpPost("Execute")]
    public async Task<ActionResult> Execute()
    {
        var req = await ReadBodyAsync<RemovalRequest>().ConfigureAwait(false);
        return Json(await _svc.ExecuteAsync(req, CurrentUserId, CurrentUserName, HttpContext.RequestAborted).ConfigureAwait(false));
    }

    public sealed class KeepRequest
    {
        public Guid KeepItemId { get; set; }
    }

    public sealed class MarkRequest
    {
        public List<string> GroupIds { get; set; } = new();

        public bool Marked { get; set; } = true;
    }
}

/// <summary>Images tab.</summary>
[Route("AdminToolkit/Images")]
public sealed class ImagesController : ToolkitControllerBase
{
    private readonly ImageService _svc;

    public ImagesController(ImageService svc)
    {
        _svc = svc;
    }

    [HttpGet("Search")]
    public ActionResult Search([FromQuery] string q) => Json(_svc.Search(q));

    [HttpGet("Browse")]
    public ActionResult Browse([FromQuery] Guid libraryId) => Json(_svc.Browse(libraryId));

    [HttpGet("Missing")]
    public ActionResult Missing([FromQuery] string type, [FromQuery] string? kinds, [FromQuery] Guid? libraryId)
    {
        var k = (kinds ?? "Movie,Series").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => Enum.TryParse<MediaKind>(x, true, out var v) ? v : (MediaKind?)null).Where(v => v is not null).Select(v => v!.Value).ToList();
        return Json(_svc.Missing(type, k, libraryId));
    }

    [HttpGet("Item/{id}")]
    public ActionResult Item([FromRoute] Guid id) => Json(_svc.GetItem(id));

    [HttpGet("Item/{id}/Remote")]
    public async Task<ActionResult> Remote([FromRoute] Guid id, [FromQuery] string? type, [FromQuery] bool allLanguages = false)
        => Json(await _svc.GetRemoteAsync(id, type, allLanguages, HttpContext.RequestAborted).ConfigureAwait(false));

    [HttpGet("Item/{id}/Web")]
    public async Task<ActionResult> Web([FromRoute] Guid id, [FromQuery] string type, [FromQuery] string? q)
        => Json(await _svc.SearchWebAsync(id, type, q, HttpContext.RequestAborted).ConfigureAwait(false));

    [HttpPost("Analyze")]
    public async Task<ActionResult> Analyze()
    {
        var req = await ReadBodyAsync<AnalyzeRequest>().ConfigureAwait(false);
        return Json(await _svc.AnalyzeAsync(req.Urls, req.Type, req.Kind, HttpContext.RequestAborted).ConfigureAwait(false));
    }

    /// <summary>Returns a candidate image's bytes (fetched SSRF-safely) so the browser can preview it over dark/light/checkerboard.</summary>
    [HttpGet("Proxy")]
    public async Task<ActionResult> Proxy([FromQuery] string url)
    {
        var img = await _svc.FetchAsync(url, HttpContext.RequestAborted).ConfigureAwait(false);
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers["Cache-Control"] = "private, max-age=600";
        return File(img.Data, img.ContentType);
    }

    [HttpPost("Item/{id}/Apply")]
    public async Task<ActionResult> Apply([FromRoute] Guid id)
    {
        var req = await ReadBodyAsync<ApplyImageRequest>().ConfigureAwait(false);
        return Json(await _svc.ApplyAsync(id, req, CurrentUserName, HttpContext.RequestAborted).ConfigureAwait(false));
    }

    [HttpPost("Item/{id}/Delete")]
    public async Task<ActionResult> Delete([FromRoute] Guid id)
    {
        var req = await ReadBodyAsync<SlotRequest>().ConfigureAwait(false);
        return Json(await _svc.DeleteAsync(id, req.Type, req.Index, CurrentUserName, HttpContext.RequestAborted).ConfigureAwait(false));
    }

    [HttpPost("Item/{id}/Reorder")]
    public async Task<ActionResult> Reorder([FromRoute] Guid id)
    {
        var req = await ReadBodyAsync<SlotRequest>().ConfigureAwait(false);
        return Json(await _svc.ReorderAsync(id, req.Type, req.Index, req.To, CurrentUserName, HttpContext.RequestAborted).ConfigureAwait(false));
    }

    [HttpPost("Item/{id}/Lock")]
    public async Task<ActionResult> Lock([FromRoute] Guid id)
    {
        var req = await ReadBodyAsync<LockRequest>().ConfigureAwait(false);
        return Json(await _svc.LockAsync(id, req.Locked, CurrentUserName, HttpContext.RequestAborted).ConfigureAwait(false));
    }

    public sealed class AnalyzeRequest
    {
        public List<string> Urls { get; set; } = new();

        public string Type { get; set; } = "Primary";

        public MediaKind Kind { get; set; } = MediaKind.Movie;
    }

    public sealed class SlotRequest
    {
        public string Type { get; set; } = "Backdrop";

        public int Index { get; set; }

        public int To { get; set; }
    }

    public sealed class LockRequest
    {
        public bool Locked { get; set; }
    }
}

/// <summary>Library Health tab.</summary>
[Route("AdminToolkit/Health")]
public sealed class HealthController : ToolkitControllerBase
{
    private readonly HealthService _svc;
    private readonly ScanRunner _scans;

    public HealthController(HealthService svc, ScanRunner scans)
    {
        _svc = svc;
        _scans = scans;
    }

    [HttpGet]
    public ActionResult Get() => Json(new { scanned = _svc.GetCached() is not null, report = _svc.GetCached(), scan = _scans.Get("health") });

    [HttpPost("Scan")]
    public ActionResult Scan()
    {
        var user = CurrentUserName;
        return Json(_scans.Start("health", user, (p, ct) => _svc.ScanAsync(user, p, ct)), 202);
    }

    [HttpPost("Refresh")]
    public async Task<ActionResult> Refresh()
    {
        var req = await ReadBodyAsync<RefreshRequest>().ConfigureAwait(false);
        return Json(new { queued = _svc.Refresh(req.ItemIds, req.ReplaceAll, CurrentUserName) });
    }

    [HttpPost("RescanLibrary")]
    public async Task<ActionResult> RescanLibrary()
    {
        var req = await ReadBodyAsync<RescanRequest>().ConfigureAwait(false);
        _svc.RescanLibrary(req.LibraryId, CurrentUserName);
        return Json(new { queued = true });
    }

    public sealed class RefreshRequest
    {
        public List<Guid> ItemIds { get; set; } = new();

        public bool ReplaceAll { get; set; }
    }

    public sealed class RescanRequest
    {
        public Guid LibraryId { get; set; }
    }
}

/// <summary>Storage tab.</summary>
[Route("AdminToolkit/Storage")]
public sealed class StorageController : ToolkitControllerBase
{
    private readonly StorageService _svc;
    private readonly ScanRunner _scans;

    public StorageController(StorageService svc, ScanRunner scans)
    {
        _svc = svc;
        _scans = scans;
    }

    [HttpGet("Drives")]
    public ActionResult Drives() => Json(_svc.Drives());

    [HttpGet]
    public ActionResult Get([FromQuery] int? notWatchedMonths)
    {
        var report = _svc.GetCached();
        var months = notWatchedMonths ?? PluginState.Config.NotWatchedMonths;
        return Json(new { scanned = report is not null, report, notWatchedMonths = months, notWatched = report is null ? null : _svc.NotWatched(months), scan = _scans.Get("storage") });
    }

    [HttpPost("Scan")]
    public ActionResult Scan()
    {
        var user = CurrentUserName;
        return Json(_scans.Start("storage", user, (p, ct) => _svc.ScanAsync(user, p, ct)), 202);
    }

    [HttpGet("Export")]
    public ActionResult Export([FromQuery] string scope = "notwatched", [FromQuery] int? months = null)
    {
        var items = string.Equals(scope, "all", StringComparison.OrdinalIgnoreCase)
            ? _svc.GetCached()?.Items ?? new List<StorageItem>()
            : _svc.NotWatched(months ?? PluginState.Config.NotWatchedMonths);
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture);
        return File(Encoding.UTF8.GetBytes(_svc.ExportCsv(items)), "text/csv", $"admin-toolkit-storage-{scope}-{stamp}.csv");
    }
}

/// <summary>Activity Log tab.</summary>
[Route("AdminToolkit/Activity")]
public sealed class ActivityController : ToolkitControllerBase
{
    private readonly ActivityLog _log;

    public ActivityController(ActivityLog log)
    {
        _log = log;
    }

    [HttpGet]
    public ActionResult Get([FromQuery] string? area, [FromQuery] string? result, [FromQuery] string? text, [FromQuery] int limit = 500)
        => Json(_log.Read(new ActivityFilter { Area = area, Result = result, Text = text, Limit = limit }));

    [HttpGet("Export")]
    public ActionResult Export([FromQuery] string? area, [FromQuery] string? result, [FromQuery] string? text)
    {
        var rows = _log.Read(new ActivityFilter { Area = area, Result = result, Text = text, Limit = 100000 });
        var csv = Csv.Write(new[] { "timeUtc", "user", "area", "action", "target", "result", "detail" }, rows.Select(e => new object?[] { e.TimeUtc, e.User, e.Area, e.Action, e.Target, e.Result, e.Detail }));
        return File(Encoding.UTF8.GetBytes(csv), "text/csv", "admin-toolkit-activity-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture) + ".csv");
    }
}

/// <summary>Settings tab.</summary>
[Route("AdminToolkit/Settings")]
public sealed class SettingsController : ToolkitControllerBase
{
    private readonly SettingsService _svc;
    private readonly ILibraryAdapter _library;
    private readonly ActivityLog _log;

    public SettingsController(SettingsService svc, ILibraryAdapter library, ActivityLog log)
    {
        _svc = svc;
        _library = library;
        _log = log;
    }

    [HttpGet]
    public ActionResult Get() => Json(new { settings = _svc.GetMasked(), defaults = new ScoreWeights() });

    [HttpPost]
    public async Task<ActionResult> Save()
    {
        var body = await ReadBodyAsync<PluginConfiguration>().ConfigureAwait(false);
        return Json(new { ok = true, settings = _svc.Save(body, CurrentUserName) });
    }

    [HttpPost("ResetWeights")]
    public ActionResult ResetWeights() => Json(new { ok = true, settings = _svc.ResetWeights(CurrentUserName) });

    [HttpPost("Test/{service}")]
    public async Task<ActionResult> Test([FromRoute] string service)
    {
        var req = await ReadBodyAsync<ConnectionTestRequest>().ConfigureAwait(false);
        try
        {
            return Json(new { ok = true, message = "Connected: " + await _svc.TestArrAsync(service, req, HttpContext.RequestAborted).ConfigureAwait(false) });
        }
        catch (Integrations.Arr.ArrException ex)
        {
            return Json(new { ok = false, message = ex.Message });
        }
    }

    [HttpPost("TestImageSearch")]
    public async Task<ActionResult> TestImageSearch()
    {
        var req = await ReadBodyAsync<ConnectionTestRequest>().ConfigureAwait(false);
        return Json(await _svc.TestImageSearchAsync(req, HttpContext.RequestAborted).ConfigureAwait(false));
    }

    /// <summary>
    /// Checks write access to a configured drive prefix or library folder. Without <c>probe</c> nothing is written
    /// (access(2)); with <c>probe=true</c> an empty hidden file is created and deleted immediately.
    /// </summary>
    [HttpPost("TestWrite")]
    public async Task<ActionResult> TestWrite()
    {
        var req = await ReadBodyAsync<WriteTestRequest>().ConfigureAwait(false);
        var locations = _library.GetLibraries().SelectMany(l => l.Locations);
        if (!SettingsService.IsTestablePath(req.Path, PluginState.Config, locations))
        {
            throw new ToolkitException("bad_path", "Only library folders and configured drive prefixes can be tested.");
        }

        var r = req.Probe ? PermissionProbe.ProbeWrite(req.Path) : PermissionProbe.CheckWritableDirectory(req.Path);
        _log.Add(CurrentUserName, "Settings", req.Probe ? "Write test (temporary file)" : "Write check", req.Path, r.Ok ? "OK" : "Failed", r.Message);
        return Json(new { ok = r.Ok, message = r.Ok ? (req.Probe ? r.Message : $"Jellyfin ({PermissionProbe.DescribeProcessUser()}) can write to {req.Path}.") : r.Message, reason = r.Reason });
    }

    public sealed class WriteTestRequest
    {
        public string Path { get; set; } = string.Empty;

        public bool Probe { get; set; }
    }
}
