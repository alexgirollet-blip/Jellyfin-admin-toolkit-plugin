using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace Jellyfin.Plugin.AdminToolkit.Storage;

/// <summary>
/// Small JSON file cache in &lt;data&gt;/admintoolkit. Writes go to a temp file then replace the target, so a crash
/// never leaves a half-written cache. Nothing is created until the first scan or action.
/// </summary>
public sealed class JsonStore
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _dir;
    private readonly Lock _lock = new();

    public JsonStore(string directory)
    {
        _dir = directory;
    }

    public string Directory => _dir;

    public T? Read<T>(string name)
        where T : class
    {
        var path = Path.Combine(_dir, name);
        lock (_lock)
        {
            if (!File.Exists(path))
            {
                return null;
            }

            try
            {
                using var fs = File.OpenRead(path);
                return JsonSerializer.Deserialize<T>(fs, Options);
            }
            catch (JsonException)
            {
                // A cache from an older version or a damaged file: treat as "no cache, scan again".
                return null;
            }
        }
    }

    public void Write<T>(string name, T value)
    {
        var path = Path.Combine(_dir, name);
        lock (_lock)
        {
            System.IO.Directory.CreateDirectory(_dir);
            var tmp = path + ".tmp";
            using (var fs = File.Create(tmp))
            {
                JsonSerializer.Serialize(fs, value, Options);
            }

            File.Move(tmp, path, true);
        }
    }

    public DateTime? LastWrite(string name)
    {
        var path = Path.Combine(_dir, name);
        return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null;
    }
}

/// <summary>One line in the activity log.</summary>
public sealed class ActivityEntry
{
    public DateTime TimeUtc { get; set; }

    public string User { get; set; } = string.Empty;

    /// <summary>Gets or sets the area: Scan, Duplicates, Images, Health, Settings, Storage.</summary>
    public string Area { get; set; } = string.Empty;

    public string Action { get; set; } = string.Empty;

    public string Target { get; set; } = string.Empty;

    /// <summary>Gets or sets OK, Failed, DryRun, Refused or Partial.</summary>
    public string Result { get; set; } = string.Empty;

    public string Detail { get; set; } = string.Empty;
}

/// <summary>Filters for reading the log.</summary>
public sealed class ActivityFilter
{
    public string? Area { get; set; }

    public string? Result { get; set; }

    public string? Text { get; set; }

    public DateTime? SinceUtc { get; set; }

    public int Limit { get; set; } = 500;
}

/// <summary>Append-only JSON-lines activity log with a retention period. Never contains API keys.</summary>
public sealed class ActivityLog
{
    private readonly string _path;
    private readonly Func<int> _retentionDays;
    private readonly Func<DateTime> _now;
    private readonly Lock _lock = new();
    private DateTime _lastPrune = DateTime.MinValue;

    public ActivityLog(string directory, Func<int> retentionDays, Func<DateTime>? now = null)
    {
        _path = Path.Combine(directory, "activity.jsonl");
        _retentionDays = retentionDays;
        _now = now ?? (() => DateTime.UtcNow);
    }

    public void Add(string user, string area, string action, string target, string result, string detail = "")
    {
        var entry = new ActivityEntry
        {
            TimeUtc = _now(),
            User = user,
            Area = area,
            Action = action,
            Target = target,
            Result = result,
            Detail = detail.Length > 4000 ? detail[..4000] : detail
        };
        lock (_lock)
        {
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.AppendAllText(_path, JsonSerializer.Serialize(entry, JsonStore.Options) + "\n");
            if (_now() - _lastPrune > TimeSpan.FromHours(12))
            {
                PruneLocked();
            }
        }
    }

    public IReadOnlyList<ActivityEntry> Read(ActivityFilter filter)
    {
        lock (_lock)
        {
            return ReadAllLocked()
                .Where(e => string.IsNullOrEmpty(filter.Area) || string.Equals(e.Area, filter.Area, StringComparison.OrdinalIgnoreCase))
                .Where(e => string.IsNullOrEmpty(filter.Result) || string.Equals(e.Result, filter.Result, StringComparison.OrdinalIgnoreCase))
                .Where(e => filter.SinceUtc is null || e.TimeUtc >= filter.SinceUtc)
                .Where(e => string.IsNullOrWhiteSpace(filter.Text)
                    || (e.Target + " " + e.Action + " " + e.Detail + " " + e.User).Contains(filter.Text.Trim(), StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(e => e.TimeUtc)
                .Take(Math.Clamp(filter.Limit, 1, 100000))
                .ToList();
        }
    }

    /// <summary>Removes entries older than the retention period. Returns the number removed.</summary>
    public int Prune()
    {
        lock (_lock)
        {
            return PruneLocked();
        }
    }

    private int PruneLocked()
    {
        _lastPrune = _now();
        if (!File.Exists(_path))
        {
            return 0;
        }

        var cutoff = _now().AddDays(-Math.Max(1, _retentionDays()));
        var all = ReadAllLocked();
        var keep = all.Where(e => e.TimeUtc >= cutoff).ToList();
        if (keep.Count == all.Count)
        {
            return 0;
        }

        var tmp = _path + ".tmp";
        File.WriteAllLines(tmp, keep.Select(e => JsonSerializer.Serialize(e, JsonStore.Options)));
        File.Move(tmp, _path, true);
        return all.Count - keep.Count;
    }

    private List<ActivityEntry> ReadAllLocked()
    {
        var list = new List<ActivityEntry>();
        if (!File.Exists(_path))
        {
            return list;
        }

        foreach (var line in File.ReadLines(_path))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                var e = JsonSerializer.Deserialize<ActivityEntry>(line, JsonStore.Options);
                if (e is not null)
                {
                    list.Add(e);
                }
            }
            catch (JsonException)
            {
            }
        }

        return list;
    }
}

/// <summary>CSV writer (RFC 4180 quoting; formula-looking cells are prefixed so spreadsheets do not execute them).</summary>
public static class Csv
{
    private static readonly System.Buffers.SearchValues<char> NeedsQuotes = System.Buffers.SearchValues.Create(",\"\n\r");

    public static string Write(IEnumerable<string> header, IEnumerable<IEnumerable<object?>> rows)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(string.Join(",", header.Select(Cell)));
        foreach (var r in rows)
        {
            sb.AppendLine(string.Join(",", r.Select(v => Cell(Format(v)))));
        }

        return sb.ToString();
    }

    private static string Format(object? v) => v switch
    {
        null => string.Empty,
        DateTime d => d.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString() ?? string.Empty
    };

    private static string Cell(string s)
    {
        if (s.Length > 0 && "=+-@\t\r".Contains(s[0], StringComparison.Ordinal) && !double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
        {
            s = "'" + s;
        }

        return s.AsSpan().IndexOfAny(NeedsQuotes) >= 0 ? "\"" + s.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : s;
    }
}
