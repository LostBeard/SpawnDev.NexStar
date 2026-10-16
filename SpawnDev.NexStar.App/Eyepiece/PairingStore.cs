using SpawnDev.NexStar.Eyepiece;
using SpawnDev.SpawnJS.JSObjects;

namespace SpawnDev.NexStar.App.Eyepiece;

/// <summary>
/// The pairing, kept in this browser's local storage so a paired phone and controlling instance find each other again without scanning anything.
/// Anyone with access to this browser profile can read it: it is as private as the browser.
/// </summary>
public sealed class PairingStore
{
    private readonly Storage? _storage;
    private readonly string _key;

    public PairingStore(Storage? storage, string key)
    {
        _storage = storage;
        _key = key;
    }

    /// <summary>The saved pairing, or null when there is none or it is not readable.</summary>
    public PairingInfo? Load()
    {
        try
        {
            var token = _storage?.GetItem(_key);
            return PairingInfo.TryParseToken(token, out var info) ? info : null;
        }
        catch { return null; }
    }

    public void Save(PairingInfo info)
    {
        try { _storage?.SetItem(_key, info.ToToken()); } catch { }
    }

    public void Clear()
    {
        try { _storage?.RemoveItem(_key); } catch { }
    }
}
