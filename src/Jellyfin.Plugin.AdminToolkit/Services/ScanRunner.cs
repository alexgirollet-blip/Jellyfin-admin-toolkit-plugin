using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AdminToolkit.Core;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AdminToolkit.Services;

/// <summary>State of one background scan, polled by the UI.</summary>
public sealed class ScanState
{
    public string Kind { get; set; } = string.Empty;

    public bool Running { get; set; }

    public double Progress { get; set; }

    public DateTime? StartedUtc { get; set; }

    public DateTime? FinishedUtc { get; set; }

    public string? Error { get; set; }

    public string? StartedBy { get; set; }
}

/// <summary>
/// Runs scans in the background only when an admin clicks Scan (or the opt-in scheduled task runs), so long scans do
/// not hit HTTP timeouts. Nothing is started at server start-up.
/// </summary>
public sealed class ScanRunner : IDisposable
{
    private readonly ConcurrentDictionary<string, ScanState> _states = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _cts = new(StringComparer.Ordinal);
    private readonly ILogger<ScanRunner>? _logger;

    public ScanRunner(ILogger<ScanRunner>? logger = null)
    {
        _logger = logger;
    }

    public ScanState Get(string kind) => _states.GetOrAdd(kind, k => new ScanState { Kind = k });

    public ScanState[] All() => new[] { Get("duplicates"), Get("health"), Get("storage") };

    /// <summary>Starts <paramref name="run"/> in the background unless that kind of scan is already running.</summary>
    public ScanState Start(string kind, string user, Func<IProgress<double>, CancellationToken, Task> run)
    {
        var state = Get(kind);
        lock (state)
        {
            if (state.Running)
            {
                throw new ToolkitException("busy", $"The {kind} scan is already running ({state.Progress:0}%).", 409);
            }

            state.Running = true;
            state.Progress = 0;
            state.Error = null;
            state.StartedUtc = DateTime.UtcNow;
            state.FinishedUtc = null;
            state.StartedBy = user;
        }

        var cts = new CancellationTokenSource(TimeSpan.FromHours(6));
        _cts[kind] = cts;
        var progress = new Progress<double>(p => state.Progress = Math.Round(Math.Clamp(p, 0, 100), 1));
        _ = Task.Run(async () =>
        {
            try
            {
                await run(progress, cts.Token).ConfigureAwait(false);
                state.Progress = 100;
            }
            catch (Exception ex)
            {
                state.Error = ex is OperationCanceledException ? "Cancelled." : ErrorText.Describe(ex);
                _logger?.LogError(ex, "Admin Toolkit {Kind} scan failed", kind);
            }
            finally
            {
                state.Running = false;
                state.FinishedUtc = DateTime.UtcNow;
                if (_cts.TryRemove(kind, out var c))
                {
                    c.Dispose();
                }
            }
        });
        return state;
    }

    public bool Cancel(string kind)
    {
        if (_cts.TryGetValue(kind, out var c))
        {
            c.Cancel();
            return true;
        }

        return false;
    }

    public void Dispose()
    {
        foreach (var c in _cts.Values)
        {
            c.Cancel();
            c.Dispose();
        }

        _cts.Clear();
    }
}
