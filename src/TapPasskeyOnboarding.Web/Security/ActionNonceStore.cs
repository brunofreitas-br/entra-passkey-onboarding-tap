using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace TapPasskeyOnboarding.Web.Security;

public interface IActionNonceStore
{
    string Issue(string objectId);
    bool TryConsume(string objectId, string nonce);
}

public sealed class MemoryActionNonceStore(TimeProvider timeProvider) : IActionNonceStore
{
    private readonly ConcurrentDictionary<string, (string ObjectId, DateTimeOffset Expires)> _nonces = new();

    public string Issue(string objectId)
    {
        Cleanup();
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        _nonces[nonce] = (objectId, timeProvider.GetUtcNow().AddMinutes(5));
        return nonce;
    }

    public bool TryConsume(string objectId, string nonce)
    {
        if (string.IsNullOrWhiteSpace(nonce) || !_nonces.TryRemove(nonce, out var entry)) return false;
        return entry.Expires >= timeProvider.GetUtcNow() &&
               string.Equals(entry.ObjectId, objectId, StringComparison.OrdinalIgnoreCase);
    }

    private void Cleanup()
    {
        var now = timeProvider.GetUtcNow();
        foreach (var item in _nonces.Where(x => x.Value.Expires < now))
            _nonces.TryRemove(item.Key, out _);
    }
}
