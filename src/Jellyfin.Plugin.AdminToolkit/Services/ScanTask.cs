using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AdminToolkit.Configuration;
using Jellyfin.Plugin.AdminToolkit.Storage;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AdminToolkit.Services;

/// <summary>
/// "Admin Toolkit: scan duplicates and library health". Off by default twice over: it has no default trigger (add one in
/// Dashboard > Scheduled Tasks), and it does nothing unless "Allow scheduled scans" is ticked in the plugin's Settings.
/// It only scans and caches results; it never changes or deletes anything.
/// </summary>
public sealed class ScanTask : IScheduledTask
{
    private readonly DuplicateService _duplicates;
    private readonly HealthService _health;
    private readonly ActivityLog _log;
    private readonly Func<PluginConfiguration> _config;
    private readonly ILogger<ScanTask> _logger;

    public ScanTask(DuplicateService duplicates, HealthService health, ActivityLog log, ILogger<ScanTask> logger)
        : this(duplicates, health, log, () => Plugin.Instance?.Configuration ?? new PluginConfiguration(), logger)
    {
    }

    internal ScanTask(DuplicateService duplicates, HealthService health, ActivityLog log, Func<PluginConfiguration> config, ILogger<ScanTask> logger)
    {
        _duplicates = duplicates;
        _health = health;
        _log = log;
        _config = config;
        _logger = logger;
    }

    public string Name => "Admin Toolkit: scan duplicates and library health";

    public string Key => "AdminToolkitScan";

    public string Description => "Runs the Admin Toolkit duplicate and health scans and caches the results so the tabs open instantly. Read-only. Does nothing unless \"Allow scheduled scans\" is ticked in Admin Toolkit settings.";

    public string Category => "Admin Toolkit";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (!_config().ScheduledScansEnabled)
        {
            _logger.LogInformation("Admin Toolkit scheduled scan skipped: \"Allow scheduled scans\" is off in the plugin settings.");
            _log.Add("scheduled task", "Scan", "Scheduled scan", "Library", "Refused", "Skipped: \"Allow scheduled scans\" is off in Settings.");
            progress.Report(100);
            return;
        }

        await _duplicates.ScanAsync("scheduled task", new Progress<double>(p => progress.Report(p / 2)), cancellationToken).ConfigureAwait(false);
        await _health.ScanAsync("scheduled task", new Progress<double>(p => progress.Report(50 + (p / 2))), cancellationToken).ConfigureAwait(false);
        progress.Report(100);
    }

    /// <summary>No default trigger: nothing runs until the admin adds one.</summary>
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();
}
