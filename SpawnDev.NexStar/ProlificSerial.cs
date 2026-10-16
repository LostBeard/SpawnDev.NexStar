using SpawnDev.SpawnJS;
using SpawnDev.SpawnJS.JSObjects;

namespace SpawnDev.NexStar;

/// <summary>
/// Prolific PL2303 USB-to-Serial adapter support base class.
/// </summary>
public abstract class ProlificSerial : INexStarTransport
{
    /// <summary>
    /// Frames and serialises commands; the subclass writes bytes to its device and feeds every received byte to <see cref="NexStarCommandChannel.OnBytes"/>.
    /// </summary>
    protected readonly NexStarCommandChannel Channel = new();

    /// <summary>
    /// Fired when a serial port is connected
    /// </summary>
    public event Action<ProlificSerial> OnConnect = default!;
    protected void ConnectedEvent() => OnConnect?.Invoke(this);

    /// <summary>
    /// Fired when a serial port is disconnected
    /// </summary>
    public event Action<INexStarTransport> OnDisconnect = default!;
    protected void DisconnectedEvent() => OnDisconnect?.Invoke(this);
    /// <summary>
    /// Fired when raw data is received
    /// </summary>
    public event Action<ProlificSerial, byte[]> OnData = default!;
    protected void DataReceived(byte[] data) => OnData?.Invoke(this, data);
    /// <summary>
    /// Fired when status changes
    /// </summary>
    public event Action<ProlificSerial> OnStatusChange = default!;
    protected void StatusChanged() => OnStatusChange?.Invoke(this);
    /// <summary>
    /// True if connected to a Prolific PL2303 device
    /// </summary>
    public abstract bool Connected { get; }
    #region Serial Configuration

    /// <summary>
    /// USB Vendor ID filter for Prolific USB-to-Serial adapters
    /// </summary>
    public const int ProlificVendorId = 0x067B;

    /// <summary>
    /// Celestron standard serial settings: 9600 baud, 8N1
    /// </summary>
    public readonly SerialOptions DefaultSerialOptions = new SerialOptions
    {
        BaudRate = 9600,
        DataBits = 8,
        StopBits = 1,
        Parity = "none"
    };

    /// <summary>
    /// Command timeout in milliseconds. The hand control can take up to 3.5 s to answer a command it has to
    /// relay to a motor board (Celestron protocol, "Timeouts"), so this must stay above that.
    /// </summary>
    public const int CommandTimeoutMs = NexStarCommandChannel.DefaultTimeoutMs;

    #endregion
    public abstract Task<bool> OpenAsync(SerialOptions? serialOptions = null);
    public abstract Task CloseAsync();
    public abstract Task<byte[]?> SendCommandAsync(byte[] command, int timeoutMs = CommandTimeoutMs);
    /// <summary>
    /// JS interop runtime
    /// </summary>
    protected static SpawnJSRuntime JS => SpawnJSRuntime.Instance;

    public static async Task<ProlificSerialWebSerial?> OpenWithWebSerial()
    {
        SerialPort? serialPort = null;
        using var Serial = JS.Get<Serial>("navigator.serial");
        if (Serial == null) return null;
        try
        {
            // Filters uses a collection expression into IEnumerable<> - SpawnJS <=2.1.18 threw
            // InvalidCastException marshalling <>z__ReadOnlySingleElementList (needs 2.1.19-local.1+).
            serialPort = await Serial.RequestPort(new() { Filters = [new SerialPortFilter { UsbVendorId = ProlificVendorId }] });
        }
        catch (Exception ex)
        {
            // User cancel / SecurityError (no user gesture) / marshalling bugs - do NOT swallow silently
            Console.WriteLine($"[NexStar] RequestPort failed: {ex.GetType().Name}: {ex.Message}");
        }
        if (serialPort == null) return null;
        var prolificSerialPort = new ProlificSerialWebSerial(serialPort);
        var succ = await prolificSerialPort.OpenAsync();
        if (!succ)
        {
            await prolificSerialPort.DisposeAsync();
            return null;
        }
        return prolificSerialPort;
    }
    public static async Task<ProlificSerialWebUSB?> OpenWithWebUSB()
    {
        USBDevice? usbDevice = null;
        using var USB = JS.Get<USB>("navigator.usb");
        if (USB == null) return null;
        try
        {
            // Same collection-expression → IEnumerable<> marshalling path as WebSerial (needs SpawnJS 2.1.19-local.1+)
            usbDevice = await USB.RequestDevice(new() { Filters = [new USBDeviceFilter { VendorId = ProlificVendorId }] });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[NexStar] RequestDevice failed: {ex.GetType().Name}: {ex.Message}");
        }
        if (usbDevice == null) return null;
        var prolificSerialPort = new ProlificSerialWebUSB(usbDevice);
        var succ = await prolificSerialPort.OpenAsync();
        if (!succ)
        {
            await prolificSerialPort.DisposeAsync();
            return null;
        }
        return prolificSerialPort;
    }
    /// <summary>
    /// Opens a Prolific serial port the user has already granted this page, without showing the picker. Null if none was granted or
    /// the browser has no Web Serial. With more than one granted, the first that answers is used by the caller (the service verifies it).
    /// </summary>
    public static async Task<ProlificSerialWebSerial?> OpenGrantedWithWebSerial()
    {
        using var Serial = JS.Get<Serial>("navigator.serial");
        if (Serial == null) return null;
        try
        {
            var ports = await Serial.GetPorts();
            foreach (var port in ports.ToArray())
            {
                var info = port.GetInfo();
                if (info.UsbVendorId != ProlificVendorId) continue;
                var prolific = new ProlificSerialWebSerial(port);
                if (await prolific.OpenAsync()) return prolific;
                await prolific.DisposeAsync();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[NexStar] GetPorts failed: {ex.GetType().Name}: {ex.Message}");
        }
        return null;
    }

    /// <summary>
    /// Opens a Prolific USB device the user has already granted this page, without showing the picker. Null if none was granted.
    /// </summary>
    public static async Task<ProlificSerialWebUSB?> OpenGrantedWithWebUSB()
    {
        using var USB = JS.Get<USB>("navigator.usb");
        if (USB == null) return null;
        try
        {
            var devices = await USB.GetDevices();
            foreach (var device in devices.ToArray())
            {
                if (device.VendorId != ProlificVendorId) continue;
                var prolific = new ProlificSerialWebUSB(device);
                if (await prolific.OpenAsync()) return prolific;
                await prolific.DisposeAsync();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[NexStar] GetDevices failed: {ex.GetType().Name}: {ex.Message}");
        }
        return null;
    }

    public virtual Task Forget() => Task.CompletedTask;
    public virtual async ValueTask DisposeAsync()
    {
        await CloseAsync();
    }


    ///// <summary>
    ///// Validates that a port is connected to a Celestron mount using echo command
    ///// </summary>
    //protected async Task<bool> ValidateCelestronMountAsync()
    //{
    //    try
    //    {
    //        await OpenAsync(SerialOptions);

    //        //using var writable = port.Writable;
    //        //using var writer = writable.GetWriter();
    //        //using var readable = port.Readable;
    //        //using var reader = readable.GetReader();

    //        // Send echo command: K + test char
    //        byte[] command = new byte[] { 0x4B, 0x41 }; // 'K', 'A'
    //        await WriteAsync(command);

    //        var readBuffer = new List<byte>();
    //        var startTime = DateTime.Now;
    //        bool hashFound = false;

    //        while ((DateTime.Now - startTime).TotalMilliseconds < 1000)
    //        {
    //            var result = await ReadAsync();
    //            if (result.Done) break;
    //            if (result.Value != null)
    //            {
    //                readBuffer.AddRange(result.Value.ToArray());
    //                if (readBuffer.Contains(0x23)) // '#'
    //                {
    //                    hashFound = true;
    //                    break;
    //                }
    //            }
    //        }

    //        reader.ReleaseLock();
    //        writer.ReleaseLock();

    //        // Valid if we got 'A' followed by '#'
    //        return hashFound && readBuffer.Contains(0x41);
    //    }
    //    catch (Exception ex)
    //    {
    //        JS.Log($"Validation failed: {ex.Message}");
    //    }
    //    finally
    //    {
    //        try { await port.Close(); } catch { }
    //    }
    //    return false;
    //}
}
