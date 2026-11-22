using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace ScTelemetryGateway.Core;

public sealed partial class MfdServer
{
    private void MapProfileApi(WebApplication application)
    {
        application.MapGet("/api/mfd/fields", ProfileApiAsync);
        application.MapGet("/api/mfd/events", ProfileApiAsync);
        application.MapGet("/api/mfd/profiles", ProfileApiAsync);
        application.MapPost("/api/mfd/profiles", ProfileApiAsync);
        application.MapMethods("/api/mfd/profiles/{id:guid}",
            [HttpMethods.Get, HttpMethods.Put, HttpMethods.Delete], ProfileApiAsync);
    }

    private async Task ProfileApiAsync(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!OriginAllowed(context) ||
            !HttpMethods.IsGet(context.Request.Method) && context.Request.Headers.Origin.Count == 0)
        {
            await ProfileError(context, 403, "origin_rejected", "Requests must originate from this gateway.");
            return;
        }
        var authorization = context.Request.Headers.Authorization.ToString();
        var approvedToken = false;
        var unavailable = false;
        lock (gate)
        {
            unavailable = stopping;
            if (!stopping && authorization.StartsWith("Bearer ", StringComparison.Ordinal) && authorization.Length == 71)
            {
                var tokenBytes = Encoding.UTF8.GetBytes(authorization[7..]);
                approvedToken = approved.Values.Any(device =>
                    CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(device.Token), tokenBytes));
            }
        }
        if (unavailable)
        {
            await ProfileError(context, 503, "listener_stopping", "The gateway is shutting down.");
            return;
        }
        if (!approvedToken)
        {
            await ProfileError(context, 401, "not_approved", "Pair this display and approve it on the PC.");
            return;
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token, context.RequestAborted);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            var routeId = context.Request.RouteValues["id"]?.ToString();
            var id = routeId is null ? Guid.Empty : Guid.Parse(routeId);
            if (routeId is not null && id == Guid.Empty)
            {
                await ProfileError(context, 400, "invalid_profile_id", "A nonempty profile identifier is required.");
                return;
            }
            if (HttpMethods.IsGet(context.Request.Method))
            {
                if (context.Request.Path == "/api/mfd/fields")
                {
                    var catalog = snapshot.SelectedFields.Select(path => MfdCatalogField.From(FieldCatalog.All[path]));
                    await context.Response.WriteAsJsonAsync(catalog, WireJson.Options, deadline.Token);
                }
                else if (context.Request.Path == "/api/mfd/events")
                    await context.Response.WriteAsJsonAsync(MfdProfile.TriggerEvents, WireJson.Options, deadline.Token);
                else if (id == Guid.Empty)
                {
                    var summaries = profiles.Summaries();
                    var bytes = JsonSerializer.SerializeToUtf8Bytes(summaries, WireJson.Options);
                    var etag = "\"" + Convert.ToHexString(SHA256.HashData(bytes)) + "\"";
                    context.Response.Headers.ETag = etag;
                    if (context.Request.Headers.IfNoneMatch.ToString() == etag)
                    {
                        context.Response.StatusCode = 304;
                        return;
                    }
                    context.Response.ContentType = "application/json";
                    await context.Response.Body.WriteAsync(bytes, deadline.Token);
                }
                else
                {
                    var profile = profiles.Get(id);
                    if (profile is null)
                        await ProfileError(context, 404, "profile_missing", "This profile no longer exists.");
                    else
                        await WriteProfile(context, profile, deadline.Token);
                }
                return;
            }

            long revision = 0;
            if (!HttpMethods.IsPost(context.Request.Method))
            {
                var match = context.Request.Headers.IfMatch.ToString();
                if (match.Length == 0)
                {
                    await ProfileError(context, 428, "revision_required", "An If-Match revision is required.");
                    return;
                }
                if (match.Length < 3 || match[0] != '"' || match[^1] != '"' ||
                    !long.TryParse(match.AsSpan(1, match.Length - 2), NumberStyles.None, CultureInfo.InvariantCulture, out revision) ||
                    revision < 1)
                {
                    await ProfileError(context, 400, "invalid_revision", "Invalid If-Match revision.");
                    return;
                }
            }
            if (HttpMethods.IsDelete(context.Request.Method))
            {
                profiles.Delete(id, revision);
                context.Response.StatusCode = 204;
                return;
            }

            var draft = await ReadProfile(context, deadline.Token);
            if (draft is null) return;
            var saved = HttpMethods.IsPost(context.Request.Method)
                ? profiles.Create(draft) : profiles.Update(id, revision, draft);
            context.Response.StatusCode = HttpMethods.IsPost(context.Request.Method) ? 201 : 200;
            await WriteProfile(context, saved, deadline.Token);
        }
        catch (MfdProfileConflictException ex)
        {
            await ProfileError(context, 412, "revision_conflict", ex.Message);
        }
        catch (MfdProfileLimitException ex)
        {
            await ProfileError(context, 422, "profile_limit", ex.Message);
        }
        catch (ArgumentException ex)
        {
            await ProfileError(context, 400, "invalid_profile", ex.Message);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        {
            logger?.LogError(ex, "MFD profile storage failed");
            await ProfileError(context, 503, "storage_failed", "Profiles could not be read or saved on the PC. Check the gateway log and storage.");
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            if (!context.RequestAborted.IsCancellationRequested && !shutdown.IsCancellationRequested)
                await ProfileError(context, 408, "request_timeout", "The profile request timed out.");
        }
    }

    private static async Task<MfdProfile?> ReadProfile(HttpContext context, CancellationToken cancellationToken)
    {
        if (context.Request.ContentLength is > MfdProfile.MaxRequestBytes)
        {
            await ProfileError(context, 413, "profile_too_large", "Profiles must not exceed 512 KiB.");
            return null;
        }
        var buffer = new byte[MfdProfile.MaxRequestBytes + 1];
        var count = 0;
        while (count < buffer.Length)
        {
            var read = await context.Request.Body.ReadAsync(buffer.AsMemory(count), cancellationToken);
            if (read == 0) break;
            count += read;
        }
        if (count > MfdProfile.MaxRequestBytes)
        {
            await ProfileError(context, 413, "profile_too_large", "Profiles must not exceed 512 KiB.");
            return null;
        }
        try
        {
            var profile = JsonSerializer.Deserialize<MfdProfile>(buffer.AsSpan(0, count), WireJson.Options)
                ?? throw new JsonException("Profile cannot be null.");
            profile.Validate();
            return profile;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            await ProfileError(context, 400, "invalid_profile", ex.Message);
            return null;
        }
    }

    private static Task WriteProfile(HttpContext context, MfdProfile profile, CancellationToken cancellationToken)
    {
        context.Response.Headers.ETag = "\"" + profile.Revision.ToString(CultureInfo.InvariantCulture) + "\"";
        return context.Response.WriteAsJsonAsync(profile, WireJson.Options, cancellationToken);
    }

    private static Task ProfileError(HttpContext context, int status, string code, string message)
    {
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(new { error = code, message }, WireJson.Options, context.RequestAborted);
    }
}
