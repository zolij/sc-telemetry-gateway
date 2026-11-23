using System.Text.Json;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace VerseDataGateway.Core;

public sealed partial class MfdServer
{
    private sealed record TriggerRequest(string ActionName, uint? HoldMs);

    private void MapActionApi(WebApplication application)
    {
        application.MapGet("/api/mfd/actions", ActionApiAsync);
        application.MapGet("/api/mfd/action-library", ActionApiAsync);
        application.MapPost("/api/mfd/actions/trigger", ActionApiAsync);
    }

    private async Task ActionApiAsync(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!OriginAllowed(context) ||
            HttpMethods.IsPost(context.Request.Method) && context.Request.Headers.Origin.Count == 0)
        {
            await ProfileError(context, 403, "origin_rejected", "Requests must originate from this gateway.");
            return;
        }
        var authorization = context.Request.Headers.Authorization.ToString();
        Guid? deviceId;
        lock (gate)
            deviceId = !stopping && authorization.StartsWith("Bearer ", StringComparison.Ordinal) &&
                authorization.Length == 71
                ? approved.Values.FirstOrDefault(device => MfdDeviceStore.TokenMatches(authorization[7..], device.TokenHash))?.Id
                : null;
        if (deviceId is null)
        {
            await ProfileError(context, 401, "not_approved", "Pair this display with a code shown on the PC.");
            return;
        }
        if (actions is null && context.Request.Path != "/api/mfd/action-library")
        {
            await ProfileError(context, 503, "actions_unavailable", "Game controls are unavailable.");
            return;
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token, context.RequestAborted);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            if (HttpMethods.IsGet(context.Request.Method))
            {
                if (context.Request.Path == "/api/mfd/action-library")
                {
                    await context.Response.WriteAsJsonAsync(actionLibrary.List(snapshot), WireJson.Options, deadline.Token);
                    return;
                }
                var result = await actions!.ListAsync(deadline.Token);
                actionLibrary.Remember(result);
                await context.Response.WriteAsJsonAsync(result, WireJson.Options, deadline.Token);
                return;
            }
            if (context.Request.ContentLength is > MaxRequestBytes)
            {
                await ProfileError(context, 413, "request_too_large", "Control requests must not exceed 4096 bytes.");
                return;
            }
            var buffer = new byte[MaxRequestBytes + 1];
            var count = 0;
            while (count < buffer.Length)
            {
                var read = await context.Request.Body.ReadAsync(buffer.AsMemory(count), deadline.Token);
                if (read == 0) break;
                count += read;
            }
            if (count > MaxRequestBytes)
            {
                await ProfileError(context, 413, "request_too_large", "Control requests must not exceed 4096 bytes.");
                return;
            }
            var request = JsonSerializer.Deserialize<TriggerRequest>(buffer.AsSpan(0, count), WireJson.Options)
                ?? throw new JsonException("A control request is required.");
            bool DispatchIfAuthorized(Action dispatch)
            {
                lock (gate)
                {
                    if (stopping || !approved.ContainsKey(deviceId.Value)) return false;
                    dispatch();
                    return true;
                }
            }
            var response = await actions!.TriggerAsync(request.ActionName, request.HoldMs, DispatchIfAuthorized, deadline.Token);
            await context.Response.WriteAsJsonAsync(new { status = response.Status.ToString(), response.Message },
                WireJson.Options, deadline.Token);
        }
        catch (JsonException)
        {
            await ProfileError(context, 400, "invalid_request", "Invalid control request.");
        }
        catch (ArgumentException ex)
        {
            await ProfileError(context, 400, "invalid_action", ex.Message);
        }
        catch (MfdActionForbiddenException)
        {
            await ProfileError(context, 403, "action_forbidden", "The action is not authorized or the game is disconnected.");
        }
        catch (MfdActionBusyException)
        {
            await ProfileError(context, 429, "action_busy", "Game controls are busy. Try again shortly.");
        }
        catch (RpcException ex)
        {
            logger?.LogWarning("Game action RPC failed: {StatusCode}", ex.StatusCode);
            await ProfileError(context, 503, "game_unavailable", $"Game control unavailable ({ex.StatusCode}).");
        }
        catch (InvalidDataException ex)
        {
            logger?.LogWarning(ex, "Invalid game action catalog");
            await ProfileError(context, 503, "invalid_catalog", "The game or stored action catalog is invalid.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger?.LogWarning(ex, "Failed to access MFD action library");
            await ProfileError(context, 503, "action_library_error", "The action library could not be read or saved.");
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            if (!context.RequestAborted.IsCancellationRequested && !shutdown.IsCancellationRequested)
                await ProfileError(context, 408, "request_timeout", "The control request timed out.");
        }
    }
}
