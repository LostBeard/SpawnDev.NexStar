namespace SpawnDev.NexStar;

/// <summary>
/// A byte link to a NexStar hand control, with no browser types in it so the same <see cref="NexStarService"/>
/// runs over WebSerial, WebUSB or a desktop serial port. Implementations feed received bytes to a
/// <see cref="NexStarCommandChannel"/>, which does the framing.
/// </summary>
public interface INexStarTransport : IAsyncDisposable
{
    /// <summary>True while the link is open and usable.</summary>
    bool Connected { get; }

    /// <summary>Fired when the link goes away (unplugged, closed by the other side, or <see cref="CloseAsync"/>).</summary>
    event Action<INexStarTransport> OnDisconnect;

    /// <summary>
    /// Sends one command and returns its payload (the response without the '#'; an empty array is a plain
    /// acknowledgement), or null on timeout, device error or a malformed response.
    /// </summary>
    Task<byte[]?> SendCommandAsync(byte[] command, int timeoutMs = NexStarCommandChannel.DefaultTimeoutMs);

    /// <summary>Closes the link.</summary>
    Task CloseAsync();

    /// <summary>Revokes the permission the user granted to this device, where the platform has such a thing.</summary>
    Task Forget();
}
