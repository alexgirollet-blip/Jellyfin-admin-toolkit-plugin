using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Jellyfin.Plugin.AdminToolkit.Core;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AdminToolkit.Api;

/// <summary>Builds the JSON error body every Admin Toolkit endpoint answers with.</summary>
public static class ErrorResponse
{
    public static ContentResult Build(Exception ex, string method, string path)
    {
        int status = ex is ToolkitException tk ? tk.Status : 500;
        string code = ex switch
        {
            ToolkitException t => t.Code,
            JellyfinApiException j when j.IsVersionMismatch => "jellyfin_version_mismatch",
            _ when ErrorText.IsVersionMismatch(ex) => "jellyfin_version_mismatch",
            JellyfinApiException => "jellyfin_api_error",
            _ => "server_error"
        };
        var body = JsonSerializer.Serialize(
            new { error = code, message = ErrorText.Describe(ex), endpoint = method + " " + path, status },
            ToolkitControllerBase.Options);
        return new ContentResult { Content = body, ContentType = "application/json; charset=utf-8", StatusCode = status };
    }
}

/// <summary>
/// Catches every exception from an Admin Toolkit controller: logs it in full on the server and answers with
/// <c>{ error, message, endpoint, status }</c> so the UI never shows a bare "HTTP 500". No stack trace reaches the browser.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class ToolkitErrorFilterAttribute : Attribute, IExceptionFilter
{
    public void OnException(ExceptionContext context)
    {
        if (context.ExceptionHandled || context.Exception is OperationCanceledException)
        {
            return;
        }

        var req = context.HttpContext.Request;
        if (context.Exception is not ToolkitException)
        {
            var logger = context.HttpContext.RequestServices.GetService<ILoggerFactory>()?.CreateLogger("AdminToolkit.Api");
            logger?.LogError(context.Exception, "Admin Toolkit: {Method} {Path} failed", req.Method, req.Path.Value);
        }

        context.Result = ErrorResponse.Build(context.Exception, req.Method, req.Path.Value ?? string.Empty);
        context.ExceptionHandled = true;
    }
}

/// <summary>Base for all Admin Toolkit controllers: admin-only, own JSON options, readable errors.</summary>
[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[ToolkitErrorFilter]
[Produces("application/json")]
public abstract class ToolkitControllerBase : ControllerBase
{
    internal static readonly JsonSerializerOptions Options = CreateOptions();

    /// <summary>Gets the authenticated admin's id (from the token claims only).</summary>
    protected Guid CurrentUserId
    {
        get
        {
            var value = User.FindFirst("Jellyfin-UserId")?.Value;
            return Guid.TryParse(value, out var id) ? id : Guid.Empty;
        }
    }

    /// <summary>Gets the authenticated admin's name for the activity log.</summary>
    protected string CurrentUserName => User.Identity?.Name ?? User.FindFirst("Jellyfin-User")?.Value ?? CurrentUserId.ToString("N");

    protected ActionResult Json(object? value, int status = 200)
        => new ContentResult { Content = JsonSerializer.Serialize(value, Options), ContentType = "application/json; charset=utf-8", StatusCode = status };

    protected async Task<T> ReadBodyAsync<T>()
        where T : class
    {
        try
        {
            var body = await JsonSerializer.DeserializeAsync<T>(Request.Body, Options, HttpContext.RequestAborted).ConfigureAwait(false);
            return body ?? throw new ToolkitException("bad_request", "The request body is empty.");
        }
        catch (JsonException ex)
        {
            throw new ToolkitException("bad_request", "The request body is not valid JSON: " + ex.Message);
        }
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var o = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never
        };
        o.Converters.Add(new JsonStringEnumConverter());
        return o;
    }
}
