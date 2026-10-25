using System.Security.Cryptography;
using System.Text;
using Flowd.Application.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace Flowd.Api.Security;

/// <summary>
/// Replays the original response for a repeated <c>Idempotency-Key</c> with the same request within 24 hours (API-R1).
/// The same key with a different request is refused with <c>IDEMPOTENCY_KEY_REUSED</c>. The key is reserved before the command runs, so the
/// same request sent twice at the same time runs once: the second one is told <c>IDEMPOTENCY_IN_PROGRESS</c> (409) and should retry shortly.
/// A reservation whose request died is taken over after <c>Flowd:Idempotency:InProgressSeconds</c> (default 120, which must stay above the longest request, 60 s for a start that waits for its result).
/// </summary>
public sealed class IdempotencyMiddleware(RequestDelegate next, TimeProvider time, IConfiguration configuration)
{
    public const string HeaderName = "Idempotency-Key";
    public static readonly TimeSpan Retention = TimeSpan.FromHours(24);
    private readonly TimeSpan _inProgressTimeout = TimeSpan.FromSeconds(Math.Max(configuration.GetValue("Flowd:Idempotency:InProgressSeconds", 120), 1));
    private const int MaxKeyLength = 255;

    public async Task InvokeAsync(HttpContext context, IIdempotencyStore store)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(store);
        if (!ShouldApply(context.Request, out var key))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var scopedKey = $"{context.User.TenantId()}|{key}";
        var hash = await HashRequestAsync(context.Request).ConfigureAwait(false);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var now = time.GetUtcNow();
            var existing = await store.FindAsync(scopedKey, context.RequestAborted).ConfigureAwait(false);
            if (existing is not null && !IsExpired(existing, now))
            {
                await ReplayAsync(context, existing, hash).ConfigureAwait(false);
                return;
            }

            if (await store.TryReserveAsync(scopedKey, hash, now, now - Retention, now - _inProgressTimeout, context.RequestAborted).ConfigureAwait(false))
            {
                await RunAsync(context, store, scopedKey, hash).ConfigureAwait(false);
                return;
            }
        }

        await WriteInProgressAsync(context).ConfigureAwait(false); // two lost races in a row: someone else is running it
    }

    private bool IsExpired(IdempotentResponse stored, DateTimeOffset now) =>
        now - stored.CreatedAt >= (stored.IsInProgress ? _inProgressTimeout : Retention);

    private async Task RunAsync(HttpContext context, IIdempotencyStore store, string scopedKey, byte[] hash)
    {
        var original = context.Response.Body;
        await using var buffer = new MemoryStream();
        context.Response.Body = buffer;
        var completed = false;
        try
        {
            await next(context).ConfigureAwait(false);
            completed = true;
        }
        finally
        {
            context.Response.Body = original;
            if (!completed || context.Response.StatusCode >= 500)
            {
                await store.ReleaseAsync(scopedKey, CancellationToken.None).ConfigureAwait(false); // failures are not remembered: a retry may run
            }
        }

        if (context.Response.StatusCode < 500)
        {
            var response = new IdempotentResponse(hash, context.Response.StatusCode, context.Response.ContentType, buffer.ToArray(), time.GetUtcNow());
            await store.SaveAsync(scopedKey, response, CancellationToken.None).ConfigureAwait(false);
        }

        buffer.Position = 0;
        await buffer.CopyToAsync(original, context.RequestAborted).ConfigureAwait(false);
    }

    private static bool ShouldApply(HttpRequest request, out string key)
    {
        key = request.Headers[HeaderName].ToString();
        return !HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method) && key.Length is > 0 and <= MaxKeyLength;
    }

    private static async Task<byte[]> HashRequestAsync(HttpRequest request)
    {
        request.EnableBuffering();
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData(Encoding.UTF8.GetBytes($"{request.Method}|{request.Path}{request.QueryString}|"));
        var chunk = new byte[8192];
        int read;
        while ((read = await request.Body.ReadAsync(chunk).ConfigureAwait(false)) > 0)
        {
            sha.AppendData(chunk.AsSpan(0, read));
        }

        request.Body.Position = 0;
        return sha.GetHashAndReset();
    }

    private static async Task WriteInProgressAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status409Conflict;
        context.Response.Headers.RetryAfter = "1";
        context.Response.ContentType = "application/problem+json";
        await context.Response.WriteAsJsonAsync(
            new { title = "Request in progress", status = 409, code = "IDEMPOTENCY_IN_PROGRESS", detail = "A request with this key is still running. Retry in a moment to get its result." },
            context.RequestAborted).ConfigureAwait(false);
    }

    private static async Task ReplayAsync(HttpContext context, IdempotentResponse stored, byte[] hash)
    {
        if (!stored.RequestHash.AsSpan().SequenceEqual(hash))
        {
            context.Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
            context.Response.ContentType = "application/problem+json";
            await context.Response.WriteAsJsonAsync(
                new { title = "Idempotency key reused", status = 422, code = "IDEMPOTENCY_KEY_REUSED", detail = "The key was already used with a different request." },
                context.RequestAborted).ConfigureAwait(false);
            return;
        }

        if (stored.IsInProgress)
        {
            await WriteInProgressAsync(context).ConfigureAwait(false);
            return;
        }

        context.Response.StatusCode = stored.Status;
        context.Response.ContentType = stored.ContentType;
        context.Response.Headers["Idempotent-Replayed"] = "true";
        await context.Response.Body.WriteAsync(stored.Body, context.RequestAborted).ConfigureAwait(false);
    }
}
