using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.AdminToolkit.Configuration;

public enum ImageSearchProviderKind
{
    None,
    Brave,
    SerpApi,
    GoogleCse
}

/// <summary>How much confirmation destructive actions need.</summary>
public enum ConfirmationLevel
{
    /// <summary>A confirmation dialog with the exact list of what will happen.</summary>
    Dialog,

    /// <summary>The dialog plus typing DELETE (default).</summary>
    TypeDelete
}

/// <summary>Maps a path prefix (as Jellyfin sees it) to a drive label such as "8TB 1".</summary>
public sealed class DriveMapping
{
    public string Prefix { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;
}

/// <summary>Maps a path as Sonarr/Radarr sees it to the same folder as Jellyfin sees it (their containers mount differently).</summary>
public sealed class PathMapping
{
    public string ArrPrefix { get; set; } = string.Empty;

    public string JellyfinPrefix { get; set; } = string.Empty;
}

/// <summary>Weights of the duplicate keep/remove score. Every signal is visible in the reasons list.</summary>
public sealed class ScoreWeights
{
    public int ManagedPath { get; set; } = 100;

    public int Completeness { get; set; } = 40;

    public int OnDisk { get; set; } = 30;

    public int Quality { get; set; } = 20;

    public int Metadata { get; set; } = 10;

    public int WatchHistory { get; set; } = 15;

    public int DateAdded { get; set; } = 3;

    public int PreferredDrive { get; set; } = 5;

    /// <summary>Gets or sets a value indicating whether the older copy gets the date-added points (true) or the newer one.</summary>
    public bool PreferOlder { get; set; } = true;

    /// <summary>Gets or sets the minimum score gap for High confidence.</summary>
    public int HighConfidenceGap { get; set; } = 30;

    /// <summary>Gets or sets the minimum score gap for Medium confidence (below is Low and never auto-selected).</summary>
    public int MediumConfidenceGap { get; set; } = 10;

    public ScoreWeights Clone() => (ScoreWeights)MemberwiseClone();
}

/// <summary>Admin Toolkit settings, stored by Jellyfin in plugins/configurations/Jellyfin.Plugin.AdminToolkit.xml.</summary>
public sealed class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Gets or sets a value indicating whether safe mode is on: only viewing/scanning/exporting is allowed. ON for a new install.</summary>
    public bool SafeMode { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether the dry-run box is ticked by default in every action dialog.</summary>
    public bool DryRunDefault { get; set; } = true;

    public ConfirmationLevel ConfirmationLevel { get; set; } = ConfirmationLevel.TypeDelete;

    public string SonarrUrl { get; set; } = string.Empty;

    public string SonarrApiKey { get; set; } = string.Empty;

    public string RadarrUrl { get; set; } = string.Empty;

    public string RadarrApiKey { get; set; } = string.Empty;

    public string JellyseerrUrl { get; set; } = string.Empty;

    public string JellyseerrApiKey { get; set; } = string.Empty;

    public List<PathMapping> ArrPathMappings { get; set; } = new();

    public ImageSearchProviderKind ImageSearchProvider { get; set; } = ImageSearchProviderKind.None;

    public string ImageSearchApiKey { get; set; } = string.Empty;

    /// <summary>Gets or sets the Programmable Search Engine id (cx), only for the legacy Google provider.</summary>
    public string GoogleSearchEngineId { get; set; } = string.Empty;

    public List<DriveMapping> DriveMappings { get; set; } = new();

    /// <summary>Gets or sets the label of a drive whose copies get the "preferred drive" points (empty = off).</summary>
    public string PreferredDrive { get; set; } = string.Empty;

    public ScoreWeights Weights { get; set; } = new();

    /// <summary>Gets or sets a value indicating whether the duplicate scan also looks for duplicate episodes inside one series.</summary>
    public bool ScanEpisodeDuplicates { get; set; }

    /// <summary>Gets or sets a value indicating whether the scheduled task may run scans (it also has no trigger until you add one).</summary>
    public bool ScheduledScansEnabled { get; set; }

    public int ActivityRetentionDays { get; set; } = 180;

    /// <summary>Gets or sets the language (ISO 639-2, e.g. "eng") the subtitle audit looks for.</summary>
    public string AuditSubtitleLanguage { get; set; } = "eng";

    /// <summary>Gets or sets the language the audio audit looks for.</summary>
    public string AuditAudioLanguage { get; set; } = "eng";

    public int HighBitrateMbps { get; set; } = 60;

    public int NotWatchedMonths { get; set; } = 12;

    /// <summary>Gets or sets the Docker container name used in the copy-ready commands shown when Jellyfin cannot delete files.</summary>
    public string ContainerName { get; set; } = "jellyfin";

    /// <summary>Clamps values to safe ranges and removes empty rows. Returns true when something changed.</summary>
    public bool Normalize()
    {
        bool changed = false;
        int Clamp(int v, int min, int max)
        {
            var c = Math.Clamp(v, min, max);
            changed |= c != v;
            return c;
        }

        Weights ??= new ScoreWeights();
        Weights.ManagedPath = Clamp(Weights.ManagedPath, 0, 1000);
        Weights.Completeness = Clamp(Weights.Completeness, 0, 1000);
        Weights.OnDisk = Clamp(Weights.OnDisk, 0, 1000);
        Weights.Quality = Clamp(Weights.Quality, 0, 1000);
        Weights.Metadata = Clamp(Weights.Metadata, 0, 1000);
        Weights.WatchHistory = Clamp(Weights.WatchHistory, 0, 1000);
        Weights.DateAdded = Clamp(Weights.DateAdded, 0, 1000);
        Weights.PreferredDrive = Clamp(Weights.PreferredDrive, 0, 1000);
        Weights.HighConfidenceGap = Clamp(Weights.HighConfidenceGap, 1, 2000);
        Weights.MediumConfidenceGap = Clamp(Weights.MediumConfidenceGap, 0, Weights.HighConfidenceGap);
        ActivityRetentionDays = Clamp(ActivityRetentionDays, 1, 3650);
        HighBitrateMbps = Clamp(HighBitrateMbps, 5, 1000);
        NotWatchedMonths = Clamp(NotWatchedMonths, 1, 240);

        var drives = (DriveMappings ?? new()).Where(d => !string.IsNullOrWhiteSpace(d.Prefix) && !string.IsNullOrWhiteSpace(d.Label))
            .Select(d => new DriveMapping { Prefix = d.Prefix.Trim(), Label = d.Label.Trim() }).ToList();
        changed |= drives.Count != (DriveMappings?.Count ?? 0);
        DriveMappings = drives;

        var maps = (ArrPathMappings ?? new()).Where(m => !string.IsNullOrWhiteSpace(m.ArrPrefix) && !string.IsNullOrWhiteSpace(m.JellyfinPrefix))
            .Select(m => new PathMapping { ArrPrefix = m.ArrPrefix.Trim(), JellyfinPrefix = m.JellyfinPrefix.Trim() }).ToList();
        changed |= maps.Count != (ArrPathMappings?.Count ?? 0);
        ArrPathMappings = maps;

        SonarrUrl = (SonarrUrl ?? string.Empty).Trim();
        RadarrUrl = (RadarrUrl ?? string.Empty).Trim();
        JellyseerrUrl = (JellyseerrUrl ?? string.Empty).Trim();
        SonarrApiKey = (SonarrApiKey ?? string.Empty).Trim();
        RadarrApiKey = (RadarrApiKey ?? string.Empty).Trim();
        JellyseerrApiKey = (JellyseerrApiKey ?? string.Empty).Trim();
        ImageSearchApiKey = (ImageSearchApiKey ?? string.Empty).Trim();
        GoogleSearchEngineId = (GoogleSearchEngineId ?? string.Empty).Trim();
        PreferredDrive = (PreferredDrive ?? string.Empty).Trim();
        ContainerName = string.IsNullOrWhiteSpace(ContainerName) ? "jellyfin" : ContainerName.Trim();
        AuditSubtitleLanguage = string.IsNullOrWhiteSpace(AuditSubtitleLanguage) ? "eng" : AuditSubtitleLanguage.Trim().ToLowerInvariant();
        AuditAudioLanguage = string.IsNullOrWhiteSpace(AuditAudioLanguage) ? "eng" : AuditAudioLanguage.Trim().ToLowerInvariant();
        return changed;
    }
}
