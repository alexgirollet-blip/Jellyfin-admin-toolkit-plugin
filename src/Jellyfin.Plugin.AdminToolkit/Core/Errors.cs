using System;
using System.IO;

namespace Jellyfin.Plugin.AdminToolkit.Core;

/// <summary>A Jellyfin API call failed. The message is readable and says which call failed.</summary>
public sealed class JellyfinApiException : Exception
{
    public JellyfinApiException(string message, Exception inner)
        : base(message, inner)
    {
    }

    public JellyfinApiException(string message)
        : base(message)
    {
    }

    public JellyfinApiException()
    {
    }

    /// <summary>Gets a value indicating whether the failure is a binary mismatch between this build and the server.</summary>
    public bool IsVersionMismatch => InnerException is not null && ErrorText.IsVersionMismatch(InnerException);
}

/// <summary>
/// A refusal or validation error that should reach the admin as-is (e.g. "safe mode is on").
/// <see cref="Status"/> is the HTTP status the API answers with.
/// </summary>
public sealed class ToolkitException : Exception
{
    public ToolkitException(string code, string message, int status = 400)
        : base(message)
    {
        Code = code;
        Status = status;
    }

    public ToolkitException()
        : this("error", "Admin Toolkit error")
    {
    }

    public ToolkitException(string message)
        : this("error", message)
    {
    }

    public ToolkitException(string message, Exception inner)
        : base(message, inner)
    {
        Code = "error";
        Status = 500;
    }

    public string Code { get; }

    public int Status { get; }
}

/// <summary>Turns exceptions into text an admin can act on.</summary>
public static class ErrorText
{
    /// <summary>The exact Jellyfin server version this build is compiled against (checked by CI against build.yaml).</summary>
    public const string BuiltForJellyfin = "10.11.10";

    /// <summary>True when the exception (or an inner one) means this DLL does not bind against the running Jellyfin.</summary>
    public static bool IsVersionMismatch(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is MissingMemberException or TypeLoadException or FileNotFoundException or FileLoadException or BadImageFormatException)
            {
                return true;
            }
        }

        return false;
    }

    public static string Describe(Exception ex)
    {
        if (ex is JellyfinApiException or ToolkitException)
        {
            return ex.Message;
        }

        if (IsVersionMismatch(ex))
        {
            return $"This Admin Toolkit build (made for Jellyfin {BuiltForJellyfin}) does not match the Jellyfin version running on this server: "
                   + $"{Root(ex).GetType().Name}: {Root(ex).Message} Install the Admin Toolkit release built for your Jellyfin version.";
        }

        return Root(ex).Message;
    }

    private static Exception Root(Exception ex)
    {
        var e = ex;
        while (e.InnerException is not null && e is not JellyfinApiException)
        {
            e = e.InnerException;
        }

        return e;
    }
}

/// <summary>
/// Adapter boundary. Every call into Jellyfin goes through <see cref="Run{T}"/> as a lambda. Because the Jellyfin member
/// is referenced only inside the lambda, a member that does not exist on the running server makes the JIT throw
/// <see cref="MissingMethodException"/> / <see cref="TypeLoadException"/> when the lambda is invoked, i.e. inside this
/// try block, and it is reported as a readable <see cref="JellyfinApiException"/> instead of crashing the request.
/// </summary>
public static class JellyfinCall
{
    public static T Run<T>(string what, Func<T> call)
    {
        try
        {
            return call();
        }
        catch (Exception ex) when (ex is not JellyfinApiException and not ToolkitException and not OperationCanceledException)
        {
            throw Wrap(what, ex);
        }
    }

    public static void Run(string what, Action call)
    {
        try
        {
            call();
        }
        catch (Exception ex) when (ex is not JellyfinApiException and not ToolkitException and not OperationCanceledException)
        {
            throw Wrap(what, ex);
        }
    }

    public static async System.Threading.Tasks.Task<T> RunAsync<T>(string what, Func<System.Threading.Tasks.Task<T>> call)
    {
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not JellyfinApiException and not ToolkitException and not OperationCanceledException)
        {
            throw Wrap(what, ex);
        }
    }

    public static async System.Threading.Tasks.Task RunAsync(string what, Func<System.Threading.Tasks.Task> call)
    {
        try
        {
            await call().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not JellyfinApiException and not ToolkitException and not OperationCanceledException)
        {
            throw Wrap(what, ex);
        }
    }

    private static JellyfinApiException Wrap(string what, Exception ex)
    {
        var detail = ErrorText.IsVersionMismatch(ex) ? ErrorText.Describe(ex) : ex.GetType().Name + ": " + ex.Message;
        return new JellyfinApiException($"Jellyfin could not {what}: {detail}", ex);
    }
}
