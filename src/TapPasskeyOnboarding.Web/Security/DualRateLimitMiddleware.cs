using System.Collections.Concurrent;

namespace TapPasskeyOnboarding.Web.Security;

public interface IRateLimitStore
{
    bool TryAcquire(string key, int limit, TimeSpan window);
}

public sealed class MemoryRateLimitStore(TimeProvider timeProvider) : IRateLimitStore
{
    private readonly ConcurrentDictionary<string, Counter> _counters = new();

    public bool TryAcquire(string key, int limit, TimeSpan window)
    {
        var now = timeProvider.GetUtcNow();
        var counter = _counters.AddOrUpdate(
            key,
            _ => new Counter(now.Add(window), 1),
            (_, existing) => existing.Expires <= now ? new Counter(now.Add(window), 1) : existing with { Count = existing.Count + 1 });
        return counter.Count <= limit;
    }

    private sealed record Counter(DateTimeOffset Expires, int Count);
}

public sealed class DualRateLimitMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, IRateLimitStore store)
    {
        if (!IsProtectedMutation(context.Request))
        {
            await next(context);
            return;
        }

        var source = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var oid = context.User.FindFirst("oid")?.Value ?? "anonymous";
        if (!store.TryAcquire($"source:{source}", 60, TimeSpan.FromMinutes(1)) ||
            !store.TryAcquire($"user:{oid}", 20, TimeSpan.FromMinutes(1)))
        {
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            context.Response.Headers.RetryAfter = "60";
            await context.Response.WriteAsJsonAsync(new { error = new { code = "rate_limited", message = "Too many requests." } });
            return;
        }

        await next(context);
    }

    private static bool IsProtectedMutation(HttpRequest request)
    {
        if (!HttpMethods.IsPost(request.Method))
            return false;

        var handler = request.Query["handler"].ToString();
        return (request.Path == "/" && string.Equals(handler, "IssueTap", StringComparison.OrdinalIgnoreCase)) ||
            (request.Path.StartsWithSegments("/confirm") &&
             string.Equals(handler, "Verify", StringComparison.OrdinalIgnoreCase));
    }
}
