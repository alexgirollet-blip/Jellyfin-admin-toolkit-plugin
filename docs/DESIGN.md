# Admin Toolkit: design

## Target Jellyfin version

* Built against **Jellyfin 10.11.10** (`Jellyfin.Controller` / `Jellyfin.Model` 10.11.10 from NuGet), `net9.0`,
  `targetAbi 10.11.10.0`. Media Vote 1.0.0.0 broke because it was built against 10.11.0 and 10.11.10 removed
  `IUserManager.Users`; this plugin uses `IUserManager.GetUsers()`.
* How it is verified:
  1. Every Jellyfin signature used was read from the 10.11.10 packages by reflection (not from memory).
  2. `.github/scripts/check_versions.py` fails CI if the csproj, test project, ApiCompat tool, `build.yaml` targetAbi
     and `ErrorText.BuiltForJellyfin` disagree.
  3. `tools/ApiCompat` loads the built DLL with the real 10.11.10 runtime assemblies and JIT-compiles every method; any
     missing member fails CI (blocking) exactly as it would fail on the server.
* SkiaSharp: the 10.11.10 server ships SkiaSharp **3.116.1** (`Directory.Packages.props` at tag `v10.11.10`). The plugin
  compiles against that version with `ExcludeAssets=runtime;native` and does not ship it. Jellyfin's `PluginLoadContext`
  returns null for assemblies a plugin does not ship, so the server's loaded copy is used. A managed PNG decoder is the
  fallback if SkiaSharp cannot be used.

## Adapter layer

Core logic (duplicate detection, scoring, health rules, storage maths) only sees plain DTOs (`ItemSnapshot`,
`PlayState`, `ImageSlot`). Jellyfin is reached through four small interfaces:

| Interface | Implementation | Jellyfin APIs |
| --- | --- | --- |
| `ILibraryAdapter` | `JellyfinLibraryAdapter` | `ILibraryManager.GetVirtualFolders/GetItemList/GetItemById/DeleteItem`, `IProviderManager.QueueRefresh`, `BaseItem.GetMediaStreams/GetDeletePaths` |
| `IUserAdapter` | `JellyfinUserAdapter` | `IUserManager.GetUsers/GetUserById`, `IUserDataManager.GetUserData/SaveUserData` |
| `IImageAdapter` | `JellyfinImageAdapter` | `IProviderManager.SaveImage/GetAvailableRemoteImages/GetRemoteImageProviderInfo`, `BaseItem.DeleteImageAsync/SwapImagesAsync/UpdateToRepositoryAsync` |
| `IDriveSpaceProvider` | `SystemDriveSpace` | `System.IO.DriveInfo` (statvfs) |

Every Jellyfin member is referenced only inside a lambda passed to `JellyfinCall.Run(...)`. The JIT resolves a member
when the method that uses it is compiled; because the lambda is its own method, a member missing on the running server
throws `MissingMethodException`/`TypeLoadException` inside `JellyfinCall`'s try block and becomes a readable
`JellyfinApiException` ("Jellyfin could not list users: This Admin Toolkit build (made for Jellyfin 10.11.10) does not
match ..."). `/AdminToolkit/SelfCheck` exercises every adapter and the Settings tab shows the result.

## Endpoints

All under `/AdminToolkit/...`, all `[Authorize(Policy = Policies.RequiresElevation)]` (administrators only). Errors are
JSON `{ error, message, endpoint, status }`; the UI shows the message, endpoint and HTTP status with "Copy diagnostics".

| Area | Endpoints |
| --- | --- |
| Status | `GET Status`, `GET SelfCheck` |
| Duplicates | `GET Duplicates`, `POST Duplicates/Scan`, `POST Duplicates/Groups/{id}/Keep`, `POST Duplicates/Mark`, `GET Duplicates/Export`, `POST Duplicates/Plan`, `POST Duplicates/Execute` |
| Images | `GET Images/Search`, `GET Images/Browse`, `GET Images/Item/{id}`, `GET Images/Item/{id}/Remote`, `GET Images/Item/{id}/Web`, `POST Images/Analyze`, `GET Images/Proxy`, `POST Images/Item/{id}/Apply`, `POST Images/Item/{id}/Delete`, `POST Images/Item/{id}/Reorder`, `POST Images/Item/{id}/Lock`, `GET Images/Missing` |
| Health | `GET Health`, `POST Health/Scan`, `POST Health/Refresh`, `POST Health/RescanLibrary` |
| Storage | `GET Storage`, `POST Storage/Scan`, `GET Storage/Export` |
| Activity | `GET Activity`, `GET Activity/Export` |
| Settings | `GET Settings`, `POST Settings`, `POST Settings/ResetWeights`, `POST Settings/Test/{service}`, `POST Settings/TestImageSearch`, `POST Settings/TestWrite` |

Destructive actions are two-step: a `Plan` call returns exactly what would happen plus a one-time confirmation token
(10 minutes, bound to the plan); `Execute` needs that token (plus the typed word `DELETE` at the default confirmation
level). Safe mode refuses every change on the server, not only in the UI.

## Storage

* Settings: the plugin's own XML (`/config/plugins/configurations/Jellyfin.Plugin.AdminToolkit.xml`).
  API keys are stored there, returned masked (`••••1234`), and never logged.
* Cache and activity log: `<data>/admintoolkit/` (`/config/data/admintoolkit` in the linuxserver image):
  `duplicates.json`, `health.json`, `storage.json`, `choices.json` (keep overrides and the removal list),
  `activity.jsonl` (pruned by the retention setting).
* Uninstall removes `<data>/admintoolkit`. Nothing is ever written into other plugins' configuration, into
  `index.html`/jellyfin-web, or the home screen.
* Nothing runs at start-up. The scheduled task "Admin Toolkit: scan duplicates and library health" has no default
  trigger and also refuses to run until "Allow scheduled scans" is ticked in Settings.

## Drive and path mapping

* Settings has rows "path prefix (as Jellyfin sees it) -> label", e.g. `/data/media1 -> 8TB 1`. The longest matching
  prefix wins and prefixes match on whole path segments. Space comes from `DriveInfo` (statvfs) on the prefix; a
  prefix that is missing or empty is reported as "not mounted?".
* Sonarr/Radarr usually mount media at different paths. "Sonarr/Radarr path mappings" (`/tv -> /data/tvshows`)
  translate their paths to Jellyfin's before comparing.

## Sonarr/Radarr removal

* A copy is **managed** when its folder equals (after path mapping) the `path` of the Sonarr series / Radarr movie
  matched by TVDb/TMDb/IMDb id (`GET /api/v3/series`, `GET /api/v3/movie`, `X-Api-Key` header).
* Removing a managed copy: `DELETE /api/v3/series/{id}?deleteFiles=true&addImportListExclusion=false` (Radarr:
  `DELETE /api/v3/movie/{id}?deleteFiles=true&addImportExclusion=false`), then Jellyfin removes the item from the
  library. The plan warns that Sonarr/Radarr will no longer track the title.
* Removing an unmanaged copy (the usual stale duplicate): the exact paths are checked with `access(2)` (W_OK|X_OK on
  the folder and its parent; nothing is written). If writable: "remove from Jellyfin library" and, with a second
  confirmation, "delete files" (`ILibraryManager.DeleteItem(..., DeleteFileLocation = true)`, the same call as
  Jellyfin's "Delete media"). If not writable: the plan shows the path, the reason (permission denied / read-only
  file system / not found) and a copy-ready shell command to run yourself. It never suggests changing PUID/PGID,
  mounts or ownership.
* "Remove from library only (keep files)" can also write an empty `.ignore` file into the folder (Jellyfin 10.11's
  `DotIgnoreIgnoreRule`) so the next scan does not add it back.
* "Copy watch data first": played state, play count, favourite and resume position are copied per user from the
  removed copy to the kept copy wherever the kept copy has no data (matched by season/episode number for series).

## Images

* Current images: `BaseItem.ImageInfos` (type, index, size, dimensions).
* Sources: Jellyfin's remote providers (`IProviderManager.GetAvailableRemoteImages`, shown first), a web image search
  provider (Brave Search API or SerpApi; Google Custom Search JSON API only for existing keys, it is closed to new
  customers and ends 1 January 2027), a pasted URL, or an upload.
* Transparency: the server fetches each candidate through the SSRF-safe fetcher and decodes it with SkiaSharp; an
  image is "Transparent" only if pixels actually have alpha < 250 (a PNG/WebP with an unused alpha channel is
  "Opaque").
* Saving: bytes are downloaded server-side and passed to `IProviderManager.SaveImage(item, stream, mime, type, index)`
  then `UpdateToRepositoryAsync(ImageUpdate)`, the same calls as Jellyfin's own image upload endpoint, so the image is
  stored wherever Jellyfin is configured to store artwork.
* Lock: Jellyfin 10.11 has no per-image lock (`MetadataField` has no image entry). The "lock" option sets the item's
  `IsLocked` flag, which stops metadata refreshes from changing the item; the UI says so.
* SSRF: http/https only; every connection (including each redirect hop, handled manually, max 3) resolves DNS and
  refuses loopback, private (10/8, 172.16/12, 192.168/16, fc00::/7), link-local (169.254/16, fe80::/10), CGNAT
  (100.64/10), multicast, unspecified and IPv4-mapped forms of those; 15 MB and 15 s limits; image content types only;
  decode refused above 40 megapixels. API keys never reach the browser.

## Duplicate detection and scoring

Signals, strongest first: shared TMDb/TVDb/IMDb id (also ids in folder names like `[tmdbid-603]`); same normalised
title + year without conflicting ids; for series, episode overlap per copy; separate list of movies Jellyfin merged as
versions (informational). Copies that point at the same files are flagged and never offered for file deletion.

Score per copy = sum of signals, each from 0 to its weight (all adjustable, "reset to defaults"):

| Signal | Default weight | Points |
| --- | --- | --- |
| Path managed by Sonarr/Radarr | 100 | full if managed |
| Completeness (series) | 60 | weight × episodes / expected |
| On disk and readable | 30 | weight × files present / files |
| Quality | 20 | weight × resolution class / best (size not used) |
| Metadata | 10 | ids 40%, poster 30%, backdrop 15%, overview 15% |
| Watch history | 5 | weight × plays / most plays (low: watch data can be copied to the kept copy) |
| Date added | 3 | older copy (or newer, setting) |
| Preferred drive | 5 | if on the preferred drive |

Confidence: **Low** if ids conflict, two copies share files, the best copy is not on disk, or the top two are within
the Medium gap (10); **Medium** if matched by title only or the gap is below the High gap (30); otherwise **High**. Low
groups are never pre-selected for removal. Each copy lists its reasons; swapping keep/remove re-evaluates warnings.
