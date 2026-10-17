using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Security.Cryptography;
using Jellyfin.Plugin.AdminToolkit.Configuration;

namespace Jellyfin.Plugin.AdminToolkit.Core;

/// <summary>
/// Server-side safety rules for every change. Safe mode refuses all changes (the UI hides the buttons too, but the
/// server is what enforces it). Destructive actions need a one-time token from a preview ("plan") call.
/// </summary>
public sealed class SafetyGuard
{
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(10);
    private readonly Func<PluginConfiguration> _config;
    private readonly Func<DateTime> _now;
    private readonly ConcurrentDictionary<string, (string Fingerprint, Guid UserId, DateTime Expires)> _tokens = new(StringComparer.Ordinal);

    public SafetyGuard(Func<PluginConfiguration> config, Func<DateTime>? now = null)
    {
        _config = config;
        _now = now ?? (() => DateTime.UtcNow);
    }

    public bool SafeMode => _config().SafeMode;

    /// <summary>Throws a 403 refusal when safe mode is on.</summary>
    public void EnsureChangesAllowed(string what)
    {
        if (_config().SafeMode)
        {
            throw new ToolkitException("safe_mode", $"Safe mode is on, so \"{what}\" is not allowed. Turn safe mode off in Settings to make changes.", 403);
        }
    }

    /// <summary>Issues a one-time token bound to a plan fingerprint and the admin who previewed it.</summary>
    public string IssueToken(string planFingerprint, Guid userId)
    {
        foreach (var (k, v) in _tokens.ToArray())
        {
            if (v.Expires < _now())
            {
                _tokens.TryRemove(k, out _);
            }
        }

        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        _tokens[token] = (planFingerprint, userId, _now().Add(TokenLifetime));
        return token;
    }

    /// <summary>
    /// Validates and consumes a token. Destructive actions also need the typed word DELETE when the confirmation level
    /// is <see cref="ConfirmationLevel.TypeDelete"/>.
    /// </summary>
    public void ConsumeToken(string? token, string planFingerprint, Guid userId, bool destructive, string? typedConfirmation)
    {
        if (string.IsNullOrEmpty(token) || !_tokens.TryRemove(token, out var entry))
        {
            throw new ToolkitException("confirmation_required", "This action needs a fresh preview and confirmation. Preview it again.", 409);
        }

        if (entry.Expires < _now())
        {
            throw new ToolkitException("confirmation_expired", "The confirmation expired (10 minutes). Preview the action again.", 409);
        }

        if (!string.Equals(entry.Fingerprint, planFingerprint, StringComparison.Ordinal))
        {
            throw new ToolkitException("plan_changed", "What would happen has changed since the preview (files or library changed). Preview the action again.", 409);
        }

        if (entry.UserId != userId)
        {
            throw new ToolkitException("confirmation_user", "The preview was made by another admin. Preview the action yourself.", 409);
        }

        if (destructive && _config().ConfirmationLevel == ConfirmationLevel.TypeDelete
            && !string.Equals(typedConfirmation?.Trim(), "DELETE", StringComparison.Ordinal))
        {
            throw new ToolkitException("type_delete", "Type DELETE (in capitals) to confirm.", 400);
        }
    }
}
