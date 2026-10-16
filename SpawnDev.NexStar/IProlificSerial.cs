
using SpawnDev.SpawnJS.JSObjects;

namespace SpawnDev.NexStar
{
    public interface IProlificSerial : IAsyncDisposable
    {
        bool Connected { get; }
        Task<bool> OpenAsync(SerialOptions? serialOptions = null);
        Task CloseAsync();
        Task Forget();
        Task<string> SendStringCommandAsync(string data, int timeoutMs);
        Task<byte[]?> SendCommandAsync(byte[] command, int timeoutMs);
    }
}