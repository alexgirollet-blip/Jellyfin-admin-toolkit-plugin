using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.AdminToolkit.Core;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.AdminToolkit.Adapters;

/// <summary>
/// <see cref="IUserAdapter"/> over IUserManager / IUserDataManager. Uses IUserManager.GetUsers(): Jellyfin 10.11.10 has no
/// <c>Users</c> property (its removal is what broke Media Vote 1.0.0.0 with MissingMethodException).
/// </summary>
public sealed class JellyfinUserAdapter : IUserAdapter
{
    private readonly IUserManager _userManager;
    private readonly IUserDataManager _userDataManager;
    private readonly ILibraryManager _libraryManager;

    public JellyfinUserAdapter(IUserManager userManager, IUserDataManager userDataManager, ILibraryManager libraryManager)
    {
        _userManager = userManager;
        _userDataManager = userDataManager;
        _libraryManager = libraryManager;
    }

    public IReadOnlyList<UserRef> GetUsers()
        => JellyfinCall.Run("list users", () => _userManager.GetUsers()
            .Select(u => new UserRef(u.Id, u.Username, u.HasPermission(PermissionKind.IsAdministrator), u.HasPermission(PermissionKind.IsDisabled)))
            .OrderBy(u => u.Name, StringComparer.OrdinalIgnoreCase)
            .ToList());

    public PlayState? GetPlayState(Guid userId, Guid itemId)
        => JellyfinCall.Run("read play data", () =>
        {
            var user = _userManager.GetUserById(userId);
            var item = _libraryManager.GetItemById(itemId);
            if (user is null || item is null)
            {
                return null;
            }

            var d = _userDataManager.GetUserData(user, item);
            return d is null ? null : new PlayState
            {
                Played = d.Played,
                PlayCount = d.PlayCount,
                IsFavorite = d.IsFavorite,
                PlaybackPositionTicks = d.PlaybackPositionTicks,
                LastPlayedDate = d.LastPlayedDate
            };
        });

    public void SavePlayState(Guid userId, Guid itemId, PlayState state)
        => JellyfinCall.Run("save play data", () =>
        {
            var user = _userManager.GetUserById(userId) ?? throw new ToolkitException("not_found", "User not found.", 404);
            var item = _libraryManager.GetItemById(itemId) ?? throw new ToolkitException("not_found", "Item not found.", 404);
            var d = _userDataManager.GetUserData(user, item) ?? throw new ToolkitException("no_userdata", "Jellyfin returned no user data record for this item.", 500);
            d.Played = state.Played;
            d.PlayCount = state.PlayCount;
            d.IsFavorite = state.IsFavorite;
            d.PlaybackPositionTicks = state.PlaybackPositionTicks;
            d.LastPlayedDate = state.LastPlayedDate;
            _userDataManager.SaveUserData(user, item, d, UserDataSaveReason.Import, CancellationToken.None);
        });
}
