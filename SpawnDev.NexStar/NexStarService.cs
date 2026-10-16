using SpawnDev.SpawnJS;
using System.Text;

namespace SpawnDev.NexStar
{
    /// <summary>
    /// Service for communicating with and controlling Celestron NexStar telescopes
    /// over WebSerial, WebUSB (browser) or any other <see cref="INexStarTransport"/>.<br/>
    /// Every query returns null (or leaves the cached value alone) when the hand control does not answer:
    /// a failed read never becomes a position, a time or a state.
    /// </summary>
    public class NexStarService : IAsyncBackgroundService
    {
        #region Private Fields
        private SpawnJSRuntime? JS;
        private Task? _Ready = null;
        private bool _disconnecting;

        #endregion

        #region Public Properties

        /// <summary>
        /// Gets a value indicating whether a polyfill implementation is being used.
        /// </summary>
        public bool UsingPolyfill { get; private set; }

        /// <summary>
        /// Ready task for async initialization
        /// </summary>
        public Task Ready => _Ready ??= InitAsync();

        /// <summary>
        /// The link to the hand control, or null when not connected
        /// </summary>
        public INexStarTransport? Transport { get; private set; }

        /// <summary>
        /// Whether a port is connected and available
        /// </summary>
        public bool SerialPortAvailable => Transport?.Connected == true;

        /// <summary>
        /// Whether a port has been selected
        /// </summary>
        public bool SerialPortSelected => Transport != null;

        /// <summary>
        /// Whether communications are currently enabled
        /// </summary>
        public bool ComsEnabled => Transport?.Connected == true;

        /// <summary>
        /// Why the last connection attempt failed, or null
        /// </summary>
        public string? LastError { get; private set; }

        /// <summary>
        /// Telescope model (retrieved after connection)
        /// </summary>
        public TelescopeModel Model { get; private set; } = TelescopeModel.Unknown;

        /// <summary>
        /// Hand controller version string
        /// </summary>
        public string Version { get; private set; } = "";

        /// <summary>
        /// Major version number
        /// </summary>
        public int VersionMajor { get; private set; }

        /// <summary>
        /// Minor version number
        /// </summary>
        public int VersionMinor { get; private set; }

        /// <summary>
        /// Whether the telescope is aligned. An unaligned mount reports RA/Dec that mean nothing and
        /// Az/Alt relative to where it was powered on; RA/Dec GoTo does not work until it is aligned.
        /// </summary>
        public bool IsAligned { get; private set; }

        /// <summary>
        /// Current tracking mode
        /// </summary>
        public TrackingMode CurrentTrackingMode { get; private set; } = TrackingMode.Off;

        /// <summary>
        /// Current RA/Dec position
        /// </summary>
        public RaDecCoordinates? CurrentRaDec { get; private set; }

        /// <summary>
        /// Current Az/Alt position
        /// </summary>
        public AzAltCoordinates? CurrentAzAlt { get; private set; }

        /// <summary>
        /// Current telescope location
        /// </summary>
        public GeoLocation? Location { get; private set; }

        /// <summary>
        /// The hand control's clock as last read
        /// </summary>
        public TelescopeTime? CurrentTime { get; private set; }

        #endregion

        #region Events

        /// <summary>
        /// Fired when a port is connected
        /// </summary>
        public event Action<INexStarTransport> OnConnected = default!;

        /// <summary>
        /// Fired when a port is disconnected
        /// </summary>
        public event Action<INexStarTransport> OnDisconnected = default!;

        /// <summary>
        /// Fired when telescope status changes: a connection opens or closes, or any state is refreshed
        /// </summary>
        public event Action OnStatusChanged = default!;

        /// <summary>
        /// True when web serial is supported in the current browser
        /// </summary>
        public bool WebSerialSupported { get; }

        /// <summary>
        /// True when web USB is supported in the current browser
        /// </summary>
        public bool WebUSBSupported { get; }

        #endregion

        #region Constructor

        /// <summary>
        /// Creates a NexStarService that can open browser ports (WebSerial, WebUSB)
        /// </summary>
        public NexStarService(SpawnJSRuntime js)
        {
            JS = js;
            WebSerialSupported = JS.IsBrowser && JS.Has("navigator.serial");
            WebUSBSupported = JS.IsBrowser && JS.Has("navigator.usb");
        }

        /// <summary>
        /// Creates a NexStarService with no browser: connect with <see cref="AttachTransportAsync"/>
        /// </summary>
        public NexStarService()
        {
        }

        #endregion

        #region Initialization

        /// <summary>
        /// Initializes the service
        /// </summary>
        private Task InitAsync() => Task.CompletedTask;

        #endregion

        #region Port Selection & Connection

        /// <summary>
        /// Opens the browser's device picker and connects to the chosen port
        /// </summary>
        /// <returns>True if a NexStar hand control answered</returns>
        public async Task<bool> SelectPortAsync(bool useWebUSB = false)
        {
            if (Transport?.Connected == true)
            {
                return true;
            }
            if (JS == null)
            {
                LastError = "No browser runtime: use AttachTransportAsync";
                return false;
            }
            LastError = null;
            ProlificSerial? serialPort = null;
            try
            {
                serialPort = useWebUSB
                    ? await ProlificSerial.OpenWithWebUSB()
                    : await ProlificSerial.OpenWithWebSerial();
            }
            catch (Exception ex)
            {
                LastError = $"{ex.GetType().Name}: {ex.Message}";
                return false;
            }
            if (serialPort == null)
            {
                LastError = "No device was chosen, or the port could not be opened";
                return false;
            }
            return await AttachTransportAsync(serialPort);
        }

        /// <summary>
        /// Connects over an already open transport and reads the hand control's state.
        /// </summary>
        /// <returns>True if the hand control answered; otherwise the transport is closed and <see cref="LastError"/> says why</returns>
        public async Task<bool> AttachTransportAsync(INexStarTransport transport)
        {
            if (Transport != null)
            {
                await DeselectPortAsync();
            }
            LastError = null;
            Transport = transport;
            transport.OnDisconnect += Transport_OnDisconnect;
            OnConnected?.Invoke(transport);
            OnStatusChanged?.Invoke();
            if (await GetVersionAsync() == null)
            {
                LastError = "The port opened but the hand control did not answer";
                await DeselectPortAsync();
                return false;
            }
            await RefreshTelescopeInfoAsync();
            await GetRaDecAsync();
            await GetAzAltAsync();
            await GetTimeAsync();
            await GetLocationAsync();
            return true;
        }

        /// <summary>
        /// Disconnects the current port
        /// </summary>
        public async Task DeselectPortAsync()
        {
            var transport = Transport;
            if (transport == null || _disconnecting) return;
            _disconnecting = true;
            try
            {
                transport.OnDisconnect -= Transport_OnDisconnect;
                Transport = null;
                ResetTelescopeState();
                try { await transport.CloseAsync(); } catch (Exception ex) { LastError = $"{ex.GetType().Name}: {ex.Message}"; }
                try { await transport.Forget(); } catch { }
                OnDisconnected?.Invoke(transport);
                OnStatusChanged?.Invoke();
            }
            finally
            {
                _disconnecting = false;
            }
        }

        private void ResetTelescopeState()
        {
            Model = TelescopeModel.Unknown;
            Version = "";
            VersionMajor = 0;
            VersionMinor = 0;
            IsAligned = false;
            CurrentTrackingMode = TrackingMode.Off;
            CurrentRaDec = null;
            CurrentAzAlt = null;
            Location = null;
            CurrentTime = null;
        }

        #endregion

        #region Command plumbing

        private Task<byte[]?> SendAsync(byte[] command) => Transport?.SendCommandAsync(command) ?? Task.FromResult<byte[]?>(null);

        private Task<byte[]?> SendAsync(string asciiCommand) => SendAsync(Encoding.ASCII.GetBytes(asciiCommand));

        /// <summary>True for a plain acknowledgement: the response was just the '#'.</summary>
        private static bool Acknowledged(byte[]? payload) => payload != null && payload.Length == 0;

        #endregion

        #region Telescope Commands - Basic

        /// <summary>
        /// Refreshes telescope info (version, model, alignment, tracking)
        /// </summary>
        public async Task RefreshTelescopeInfoAsync()
        {
            await GetVersionAsync();
            await GetModelAsync();
            await GetAlignmentStatusAsync();
            await GetTrackingModeAsync();
        }

        /// <summary>
        /// Gets the hand controller version
        /// </summary>
        public async Task<string?> GetVersionAsync()
        {
            var payload = await SendAsync("V");
            if (payload == null || payload.Length != 2) return null;
            VersionMajor = payload[0];
            VersionMinor = payload[1];
            Version = $"{VersionMajor}.{VersionMinor}";
            OnStatusChanged?.Invoke();
            return Version;
        }

        /// <summary>
        /// Gets the telescope model; <see cref="TelescopeModel.Unknown"/> if it did not answer or the id is not a known model
        /// </summary>
        public async Task<TelescopeModel> GetModelAsync()
        {
            var payload = await SendAsync("m");
            if (payload == null || payload.Length != 1) return TelescopeModel.Unknown;
            Model = Enum.IsDefined(typeof(TelescopeModel), (int)payload[0])
                ? (TelescopeModel)payload[0]
                : TelescopeModel.Unknown;
            OnStatusChanged?.Invoke();
            return Model;
        }

        /// <summary>
        /// Sends an echo command for testing; returns the character the hand control sent back
        /// </summary>
        public async Task<char?> EchoAsync(char testChar)
        {
            var payload = await SendAsync(new byte[] { (byte)'K', (byte)testChar });
            if (payload == null || payload.Length != 1) return null;
            return (char)payload[0];
        }

        /// <summary>
        /// Gets alignment status; null if the hand control did not answer
        /// </summary>
        public async Task<bool?> GetAlignmentStatusAsync()
        {
            var payload = await SendAsync("J");
            if (payload == null || payload.Length != 1) return null;
            IsAligned = payload[0] == 1;
            OnStatusChanged?.Invoke();
            return IsAligned;
        }

        /// <summary>
        /// Checks if a GoTo operation is in progress; null if the hand control did not answer
        /// (a failed read must not look like "finished")
        /// </summary>
        public async Task<bool?> IsGotoInProgressAsync()
        {
            var payload = await SendAsync("L");
            if (payload == null || payload.Length != 1) return null;
            return payload[0] == (byte)'1';
        }

        /// <summary>
        /// Cancels any GoTo operation in progress
        /// </summary>
        public async Task<bool> CancelGotoAsync() => Acknowledged(await SendAsync("M"));

        #endregion

        #region Telescope Commands - Position

        /// <summary>
        /// Gets current RA/Dec position (meaningless until the mount is aligned)
        /// </summary>
        /// <param name="precise">Use precise (32-bit) format for sub-arcsecond accuracy</param>
        public async Task<RaDecCoordinates?> GetRaDecAsync(bool precise = false)
        {
            var payload = await SendAsync(precise ? "e" : "E");
            if (!NexStarProtocol.TryParsePositionResponse(payload, out var ra, out var dec)) return null;
            CurrentRaDec = new RaDecCoordinates(ra, dec);
            OnStatusChanged?.Invoke();
            return CurrentRaDec;
        }

        /// <summary>
        /// Gets current Az/Alt position (relative to the power-on direction until the mount is aligned)
        /// </summary>
        /// <param name="precise">Use precise (32-bit) format for sub-arcsecond accuracy</param>
        public async Task<AzAltCoordinates?> GetAzAltAsync(bool precise = false)
        {
            var payload = await SendAsync(precise ? "z" : "Z");
            if (!NexStarProtocol.TryParsePositionResponse(payload, out var az, out var alt)) return null;
            CurrentAzAlt = new AzAltCoordinates(az, alt);
            OnStatusChanged?.Invoke();
            return CurrentAzAlt;
        }

        /// <summary>
        /// Commands telescope to slew to RA/Dec position. The hand control ignores this until it is aligned.
        /// </summary>
        public async Task<bool> GotoRaDecAsync(double ra, double dec, bool precise = false)
        {
            if (!(ra >= 0 && ra <= 360 && dec >= -90 && dec <= 90)) return false;
            return Acknowledged(await SendAsync(NexStarProtocol.FormatGotoRaDecCommand(ra, dec, precise)));
        }

        /// <summary>
        /// Commands telescope to slew to Az/Alt position
        /// </summary>
        public async Task<bool> GotoAzAltAsync(double az, double alt, bool precise = false)
        {
            if (!(az >= 0 && az <= 360 && alt >= -90 && alt <= 90)) return false;
            return Acknowledged(await SendAsync(NexStarProtocol.FormatGotoAzAltCommand(az, alt, precise)));
        }

        /// <summary>
        /// Syncs telescope position to provided RA/Dec coordinates
        /// </summary>
        public async Task<bool> SyncRaDecAsync(double ra, double dec, bool precise = false)
        {
            if (!(ra >= 0 && ra <= 360 && dec >= -90 && dec <= 90)) return false;
            return Acknowledged(await SendAsync(NexStarProtocol.FormatSyncRaDecCommand(ra, dec, precise)));
        }

        #endregion

        #region Telescope Commands - Slewing

        /// <summary>
        /// Starts slewing at a fixed rate
        /// </summary>
        public async Task<bool> SlewFixedAsync(SlewAxis axis, SlewDirection direction, SlewRate rate)
            => Acknowledged(await SendAsync(NexStarProtocol.FormatFixedSlewCommand(axis, direction, rate)));

        /// <summary>
        /// Starts slewing at a variable rate
        /// </summary>
        /// <param name="axis">Axis to slew</param>
        /// <param name="direction">Direction to slew</param>
        /// <param name="rateArcsecPerSec">Rate in arcseconds per second, clamped to 0 .. <see cref="NexStarProtocol.MaxVariableSlewArcsecPerSec"/></param>
        public async Task<bool> SlewVariableAsync(SlewAxis axis, SlewDirection direction, double rateArcsecPerSec)
            => Acknowledged(await SendAsync(NexStarProtocol.FormatVariableSlewCommand(axis, direction, rateArcsecPerSec)));

        /// <summary>
        /// Stops slewing on a specific axis
        /// </summary>
        public Task<bool> StopSlewAsync(SlewAxis axis) => SlewFixedAsync(axis, SlewDirection.Positive, SlewRate.Stop);

        /// <summary>
        /// Stops slewing on both axes; true only if both stops were acknowledged
        /// </summary>
        public async Task<bool> StopAllSlewAsync()
        {
            var a = await StopSlewAsync(SlewAxis.RaAzm);
            var b = await StopSlewAsync(SlewAxis.DecAlt);
            return a && b;
        }

        #endregion

        #region Telescope Commands - Tracking

        /// <summary>
        /// Gets current tracking mode; null if the hand control did not answer or reported a mode this library does not know
        /// </summary>
        public async Task<TrackingMode?> GetTrackingModeAsync()
        {
            var payload = await SendAsync("t");
            if (payload == null || payload.Length != 1 || payload[0] > 3) return null;
            CurrentTrackingMode = (TrackingMode)payload[0];
            OnStatusChanged?.Invoke();
            return CurrentTrackingMode;
        }

        /// <summary>
        /// Sets tracking mode
        /// </summary>
        public async Task<bool> SetTrackingModeAsync(TrackingMode mode)
        {
            var ok = Acknowledged(await SendAsync(new byte[] { (byte)'T', (byte)mode }));
            if (ok)
            {
                CurrentTrackingMode = mode;
                OnStatusChanged?.Invoke();
            }
            return ok;
        }

        #endregion

        #region Telescope Commands - Time/Location

        /// <summary>
        /// Gets telescope time
        /// </summary>
        public async Task<TelescopeTime?> GetTimeAsync()
        {
            var payload = await SendAsync("h");
            if (!NexStarProtocol.TryParseTimeResponse(payload, out var time)) return null;
            CurrentTime = time;
            OnStatusChanged?.Invoke();
            return time;
        }

        /// <summary>
        /// Sets telescope time: the local clock time, the zone's STANDARD UTC offset in whole hours, and whether
        /// daylight saving applies (the hand control adds the hour itself)
        /// </summary>
        public async Task<bool> SetTimeAsync(DateTime localTime, int standardOffsetHours, bool dst)
            => Acknowledged(await SendAsync(NexStarProtocol.FormatSetTimeCommand(localTime, standardOffsetHours, dst)));

        /// <summary>
        /// Sets the telescope clock from this computer's clock and time zone
        /// </summary>
        public Task<bool> SyncTimeAsync() => SyncTimeAsync(DateTime.UtcNow, TimeZoneInfo.Local);

        /// <summary>
        /// Sets the telescope clock so its UTC is <paramref name="utc"/>, using <paramref name="zone"/> for the offset and daylight saving
        /// (see <see cref="NexStarProtocol.ComputeClock"/>)
        /// </summary>
        public Task<bool> SyncTimeAsync(DateTime utc, TimeZoneInfo zone)
        {
            var clock = NexStarProtocol.ComputeClock(utc, zone);
            return SetTimeAsync(clock.LocalTime, clock.TimezoneOffset, clock.DaylightSaving);
        }

        /// <summary>
        /// Gets telescope location
        /// </summary>
        public async Task<GeoLocation?> GetLocationAsync()
        {
            var payload = await SendAsync("w");
            if (!NexStarProtocol.TryParseLocationResponse(payload, out var location)) return null;
            Location = location;
            OnStatusChanged?.Invoke();
            return Location;
        }

        /// <summary>
        /// Sets telescope location
        /// </summary>
        public async Task<bool> SetLocationAsync(double lat, double lon)
        {
            if (!(lat >= -90 && lat <= 90 && lon >= -180 && lon <= 180)) return false;
            var ok = Acknowledged(await SendAsync(NexStarProtocol.FormatSetLocationCommand(lat, lon)));
            if (ok)
            {
                Location = new GeoLocation(lat, lon);
                OnStatusChanged?.Invoke();
            }
            return ok;
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Get a list of popular targets filtered by visibility from current location
        /// </summary>
        /// <param name="currentTime">Time to check visibility (defaults to UTC Now)</param>
        /// <returns>List of visible popular objects</returns>
        public IEnumerable<CelestialObject> GetVisibleQuickTargets(DateTime? currentTime = null)
        {
            var candidates = new List<CelestialObject>();

            // Popular Bright Stars
            var starNames = new[] { "Polaris", "Sirius", "Vega", "Rigel", "Betelgeuse", "Arcturus", "Capella", "Altair", "Aldebaran", "Antares", "Spica" };
            foreach (var name in starNames)
            {
                var s = CelestialCatalogs.AlignmentStars.FirstOrDefault(x => x.Name == name);
                if (s != null) candidates.Add(s);
            }

            // Popular Messier Objects (Nebulae, Clusters, Galaxies)
            // M31 (Andromeda), M42 (Orion), M45 (Pleiades), M13 (Hercules), M57 (Ring),
            // M8 (Lagoon), M27 (Dumbbell), M51 (Whirlpool), M44 (Beehive), M11 (Wild Duck)
            var messierNums = new[] { 31, 42, 45, 13, 57, 8, 27, 51, 44, 11, 16, 20, 81, 82 };
            foreach (var num in messierNums)
            {
                var m = CelestialCatalogs.Messier.FirstOrDefault(x => x.MessierNumber == num);
                if (m != null) candidates.Add(m);
            }

            if (Location == null)
            {
                // Return all candidates if location unknown
                return candidates;
            }

            var time = currentTime ?? DateTime.UtcNow;

            // Filter by visibility (> 0 degrees altitude) and return sorted by altitude
            return candidates
                .Select(obj => new
                {
                    Obj = obj,
                    AltAz = AstronomyMath.J2000ToHorizontal(
                        obj.RightAscension, obj.Declination,
                        Location.Latitude, Location.Longitude, time)
                })
                .Where(x => x.AltAz.Altitude > 10) // Only objects > 10° above horizon
                .OrderByDescending(x => x.AltAz.Altitude) // Highest objects first
                .Select(x => x.Obj)
                .ToList();
        }

        #endregion

        #region Event Handlers

        private void Transport_OnDisconnect(INexStarTransport transport)
        {
            if (Transport != transport) return;
            // Not async void: an exception here would be unobservable. DeselectPortAsync handles its own failures.
            _ = DeselectPortAsync();
        }

        #endregion
    }
}
