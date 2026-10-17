using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.AdminToolkit.Adapters;
using Jellyfin.Plugin.AdminToolkit.Core;
using Jellyfin.Plugin.AdminToolkit.Images;

namespace Jellyfin.Plugin.AdminToolkit.Services;

/// <summary>Exercises every adapter once (read-only) and reports which Jellyfin APIs work on this server.</summary>
public sealed class SelfCheckService
{
    // 1x1 PNG with a fully transparent pixel, for checking that the server's SkiaSharp can be used.
    private static readonly byte[] TinyPng = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGNgYGBgAAAABQABpfZFQAAAAABJRU5ErkJggg==");

    private readonly ILibraryAdapter _library;
    private readonly IUserAdapter _users;
    private readonly IImageAdapter _images;

    public SelfCheckService(ILibraryAdapter library, IUserAdapter users, IImageAdapter images)
    {
        _library = library;
        _users = users;
        _images = images;
    }

    public static string ServerVersion
    {
        get
        {
            try
            {
                return typeof(MediaBrowser.Controller.Library.ILibraryManager).Assembly.GetName().Version?.ToString() ?? "?";
            }
            catch (Exception ex) when (ErrorText.IsVersionMismatch(ex))
            {
                return "?";
            }
        }
    }

    public List<SelfCheckResult> Run()
    {
        var results = new List<SelfCheckResult>();
        void Check(string name, Func<string> run)
        {
            try
            {
                results.Add(new SelfCheckResult { Name = name, Ok = true, Detail = run() });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                results.Add(new SelfCheckResult { Name = name, Ok = false, Detail = ErrorText.Describe(ex) });
            }
        }

        Check("Jellyfin version", () => $"Server assemblies {ServerVersion}; this build was made for {ErrorText.BuiltForJellyfin}.");
        IReadOnlyList<LibraryInfo> libs = Array.Empty<LibraryInfo>();
        Check("Libraries", () =>
        {
            libs = _library.GetLibraries();
            return string.Create(CultureInfo.InvariantCulture, $"{libs.Count} libraries, {libs.Sum(l => l.Locations.Count)} folders.");
        });
        ItemSnapshot? sample = null;
        Check("Library items and media streams", () =>
        {
            var items = _library.GetItems(new ItemQuery { Kinds = new[] { MediaKind.Movie, MediaKind.Series }, Limit = 1, WithStreams = true });
            sample = items.FirstOrDefault();
            return sample is null ? "No movies or series found." : $"Read \"{sample.Name}\".";
        });
        IReadOnlyList<UserRef> users = Array.Empty<UserRef>();
        Check("Users", () =>
        {
            users = _users.GetUsers();
            return string.Create(CultureInfo.InvariantCulture, $"{users.Count} users ({users.Count(u => u.IsAdministrator)} admin, {users.Count(u => u.IsDisabled)} disabled).");
        });
        Check("Play data", () =>
        {
            if (sample is null || users.Count == 0)
            {
                return "Skipped (no item or user).";
            }

            var st = _users.GetPlayState(users[0].Id, sample.Id);
            return st is null ? "No record (OK)." : "Read OK.";
        });
        Check("Images", () => sample is null ? "Skipped (no item)." : string.Create(CultureInfo.InvariantCulture, $"{_images.GetImages(sample.Id).Count} image(s) on \"{sample.Name}\"."));
        Check("Remote image providers", () => sample is null ? "Skipped (no item)." : string.Join(", ", _images.GetRemoteProviders(sample.Id)));
        Check("Episodes", () =>
        {
            var series = _library.GetItems(new ItemQuery { Kinds = new[] { MediaKind.Series }, Limit = 1, WithStreams = false }).FirstOrDefault();
            return series is null ? "No series found." : string.Create(CultureInfo.InvariantCulture, $"{_library.GetEpisodes(series.Id, true).Count} episode records in \"{series.Name}\".");
        });
        Check("SkiaSharp (transparency detection)", () =>
        {
            var f = ImageInspector.Inspect(TinyPng);
            if (f.Decoder != "skia")
            {
                throw new ToolkitException("skia", "SkiaSharp could not be used; the built-in PNG decoder is used instead (WebP/GIF transparency cannot be checked).");
            }

            return $"OK ({f.Transparency}).";
        });
        Check("Jellyfin process user", PermissionProbe.DescribeProcessUser);
        return results;
    }
}
