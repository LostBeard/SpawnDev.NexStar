using SpawnDev.SpawnJS;

namespace SpawnDev.NexStar
{
    /// <summary>
    /// Service for communicating with and controlling Celestron NexStar telescopes
    /// using the Web Serial API.<br/>
    /// Reference: https://developer.chrome.com/docs/capabilities/serial
    /// </summary>
    public class NexStarService : IAsyncBackgroundService
    {
        #region Private Fields
        private SpawnJSRuntime JS;
        private Task? _Ready = null;

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
        /// Currently selected serial port
        /// </summary>
        public ProlificSerial? SerialPort { get; private set; }

        /// <summary>
        /// Whether a serial port is connected and available
        /// </summary>
        public bool SerialPortAvailable => SerialPort?.Connected == true;

        /// <summary>
        /// Whether a serial port has been selected
        /// </summary>
        public bool SerialPortSelected => SerialPort != null;

        /// <summary>
        /// Whether communications are currently enabled
        /// </summary>
        public bool ComsEnabled => SerialPort?.Connected == true;

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
        /// Whether the telescope is aligned
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

        #endregion

        #region Events

        /// <summary>
        /// Fired when a serial port is connected
        /// </summary>
        public event Action<ProlificSerial> OnConnected = default!;

        /// <summary>
        /// Fired when a serial port is disconnected
        /// </summary>
        public event Action<ProlificSerial> OnDisconnected = default!;

        /// <summary>
        /// Fired when raw data is received
        /// </summary>
        public event Action<byte[]> OnData = default!;

        /// <summary>
        /// Fired when telescope status is updated
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
        /// Creates a new NexStarService instance
        /// </summary>
        public NexStarService(SpawnJSRuntime js)
        {
            JS = js;
            WebSerialSupported = JS.IsBrowser && JS.Has("navigator.serial");
            WebUSBSupported = JS.IsBrowser && JS.Has("navigator.usb");
        }

        #endregion

        #region Initialization

        /// <summary>
        /// Initializes the service, loading polyfills if necessary (e.g. for Android)
        /// </summary>
        private async Task InitAsync()
        {
            if (!JS.IsBrowser) return;
            // Nothing at this time - reserved for future use
        }

        #endregion

        #region Port Selection & Connection

        /// <summary>
        /// Opens device picker and selects a serial port
        /// </summary>
        /// <returns>True if a valid NexStar port was selected</returns>
        public async Task<bool> SelectPortAsync(bool useWebUSB = false)
        {
            if (SerialPort?.Connected == true)
            {
                return true;
            }
            ProlificSerial? serialPort = null;
            try
            {
                if (useWebUSB)
                {
                    serialPort = await ProlificSerial.OpenWithWebUSB();
                }
                else
                {
                    serialPort = await ProlificSerial.OpenWithWebSerial();
                }
            }
            catch
            {
                return false;
            }
            if (serialPort != null)
            {
                if (SerialPort != null)
                {
                    await SerialPort.CloseAsync();
                    SerialPort!.OnDisconnect -= SerialPort_OnDisconnect;
                    OnDisconnected?.Invoke(SerialPort);
                    SerialPort = null;
                }
                SerialPort = serialPort;
                SerialPort.OnDisconnect += SerialPort_OnDisconnect;
                OnConnected?.Invoke(SerialPort);
                await RefreshTelescopeInfoAsync();
                await GetRaDecAsync();
                await GetAzAltAsync();
                await GetTimeAsync();
                await GetLocationAsync();
                return true;
            }
            return false;
        }

        /// <summary>
        /// Deselects the current port
        /// </summary>
        public async Task DeselectPortAsync()
        {
            if (SerialPort != null)
            {
                var serialPort = SerialPort;
                await serialPort.CloseAsync();
                serialPort.OnDisconnect -= SerialPort_OnDisconnect;
                OnDisconnected?.Invoke(serialPort);
                SerialPort = null;
                await serialPort.Forget();
                ResetTelescopeState();
            }
        }

        #endregion

        #region
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
        }

        #endregion


        #region Telescope Commands - Basic

        /// <summary>
        /// Refreshes telescope info (version, model, alignment)
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
            if (SerialPort == null) return null;
            var response = await SerialPort.SendCommandAsync(new byte[] { (byte)'V' });
            if (response == null || response.Length < 2) return null;

            VersionMajor = response[0];
            VersionMinor = response[1];
            Version = $"{VersionMajor}.{VersionMinor}";
            OnStatusChanged?.Invoke();
            return Version;
        }

        /// <summary>
        /// Gets the telescope model
        /// </summary>
        public async Task<TelescopeModel> GetModelAsync()
        {
            if (SerialPort == null) return TelescopeModel.Unknown;
            var response = await SerialPort.SendCommandAsync(new byte[] { (byte)'m' });
            if (response == null || response.Length < 2) return TelescopeModel.Unknown;

            var modelId = response[0];
            Model = Enum.IsDefined(typeof(TelescopeModel), (int)modelId)
                ? (TelescopeModel)modelId
                : TelescopeModel.Unknown;
            OnStatusChanged?.Invoke();
            return Model;
        }

        /// <summary>
        /// Sends an echo command for testing
        /// </summary>
        public async Task<char?> EchoAsync(char testChar)
        {
            if (SerialPort == null) return null;
            var response = await SerialPort.SendCommandAsync(new byte[] { (byte)'K', (byte)testChar });
            if (response == null || response.Length < 2) return null;
            return (char)response[0];
        }

        /// <summary>
        /// Gets alignment status
        /// </summary>
        public async Task<bool> GetAlignmentStatusAsync()
        {
            if (SerialPort == null) return false;
            var response = await SerialPort.SendCommandAsync(new byte[] { (byte)'J' });
            if (response == null || response.Length < 2) return false;
            IsAligned = response[0] == 1;
            OnStatusChanged?.Invoke();
            return IsAligned;
        }

        /// <summary>
        /// Checks if a GoTo operation is in progress
        /// </summary>
        public async Task<bool> IsGotoInProgressAsync()
        {
            if (SerialPort == null) return false;
            var response = await SerialPort.SendStringCommandAsync("L");
            return response == "1";
        }

        /// <summary>
        /// Cancels any GoTo operation in progress
        /// </summary>
        public async Task<bool> CancelGotoAsync()
        {
            if (SerialPort == null) return false;
            var response = await SerialPort.SendStringCommandAsync("M");
            return response != null;
        }

        #endregion

        #region Telescope Commands - Position

        /// <summary>
        /// Gets current RA/Dec position
        /// </summary>
        /// <param name="precise">Use precise (32-bit) format for sub-arcsecond accuracy</param>
        public async Task<RaDecCoordinates?> GetRaDecAsync(bool precise = false)
        {
            if (SerialPort == null) return null;
            var cmd = precise ? "e" : "E";
            var response = await SerialPort.SendStringCommandAsync(cmd);
            if (response == null) return null;

            var (ra, dec) = NexStarProtocol.ParsePositionResponse(response);
            CurrentRaDec = new RaDecCoordinates(ra, dec);
            OnStatusChanged?.Invoke();
            return CurrentRaDec;
        }

        /// <summary>
        /// Gets current Az/Alt position
        /// </summary>
        /// <param name="precise">Use precise (32-bit) format for sub-arcsecond accuracy</param>
        public async Task<AzAltCoordinates?> GetAzAltAsync(bool precise = false)
        {
            if (SerialPort == null) return null;
            var cmd = precise ? "z" : "Z";
            var response = await SerialPort.SendStringCommandAsync(cmd);
            if (response == null) return null;

            var (az, alt) = NexStarProtocol.ParsePositionResponse(response);
            CurrentAzAlt = new AzAltCoordinates(az, alt);
            OnStatusChanged?.Invoke();
            return CurrentAzAlt;
        }

        /// <summary>
        /// Commands telescope to slew to RA/Dec position
        /// </summary>
        public async Task<bool> GotoRaDecAsync(double ra, double dec, bool precise = false)
        {
            if (SerialPort == null) return false;
            if (ra < 0 || ra > 360 || dec < -90 || dec > 90) return false;
            var command = NexStarProtocol.FormatGotoRaDecCommand(ra, dec, precise);
            var response = await SerialPort.SendCommandAsync(command);
            return response != null;
        }

        /// <summary>
        /// Commands telescope to slew to Az/Alt position
        /// </summary>
        public async Task<bool> GotoAzAltAsync(double az, double alt, bool precise = false)
        {
            if (SerialPort == null) return false;
            if (az < 0 || az > 360 || alt < -90 || alt > 90) return false;
            var command = NexStarProtocol.FormatGotoAzAltCommand(az, alt, precise);
            var response = await SerialPort.SendCommandAsync(command);
            return response != null;
        }

        /// <summary>
        /// Syncs telescope position to provided RA/Dec coordinates
        /// </summary>
        public async Task<bool> SyncRaDecAsync(double ra, double dec, bool precise = false)
        {
            if (SerialPort == null) return false;
            if (ra < 0 || ra > 360 || dec < -90 || dec > 90) return false;
            var command = NexStarProtocol.FormatSyncRaDecCommand(ra, dec, precise);
            var response = await SerialPort.SendCommandAsync(command);
            return response != null;
        }

        #endregion

        #region Telescope Commands - Slewing

        /// <summary>
        /// Starts slewing at a fixed rate
        /// </summary>
        public async Task<bool> SlewFixedAsync(SlewAxis axis, SlewDirection direction, SlewRate rate)
        {
            if (SerialPort == null) return false;
            var command = NexStarProtocol.FormatFixedSlewCommand(axis, direction, rate);
            var response = await SerialPort.SendCommandAsync(command);
            return response != null;
        }

        /// <summary>
        /// Starts slewing at a variable rate
        /// </summary>
        /// <param name="axis">Axis to slew</param>
        /// <param name="direction">Direction to slew</param>
        /// <param name="rateArcsecPerSec">Rate in arcseconds per second (max ~16000)</param>
        public async Task<bool> SlewVariableAsync(SlewAxis axis, SlewDirection direction, double rateArcsecPerSec)
        {
            if (SerialPort == null) return false;
            var command = NexStarProtocol.FormatVariableSlewCommand(axis, direction, rateArcsecPerSec);
            var response = await SerialPort.SendCommandAsync(command);
            return response != null;
        }

        /// <summary>
        /// Stops slewing on a specific axis
        /// </summary>
        public async Task<bool> StopSlewAsync(SlewAxis axis)
        {
            return await SlewFixedAsync(axis, SlewDirection.Positive, SlewRate.Stop);
        }

        /// <summary>
        /// Stops slewing on both axes
        /// </summary>
        public async Task StopAllSlewAsync()
        {
            await StopSlewAsync(SlewAxis.RaAzm);
            await StopSlewAsync(SlewAxis.DecAlt);
        }

        #endregion

        #region Telescope Commands - Tracking

        /// <summary>
        /// Gets current tracking mode
        /// </summary>
        public async Task<TrackingMode> GetTrackingModeAsync()
        {
            if (SerialPort == null) return TrackingMode.Off;
            var response = await SerialPort.SendCommandAsync(new byte[] { (byte)'t' });
            if (response == null || response.Length < 2) return TrackingMode.Off;

            CurrentTrackingMode = response[0] switch
            {
                0 => TrackingMode.Off,
                1 => TrackingMode.AltAz,
                2 => TrackingMode.EQNorth,
                3 => TrackingMode.EQSouth,
                _ => TrackingMode.Off
            };
            OnStatusChanged?.Invoke();
            return CurrentTrackingMode;
        }

        /// <summary>
        /// Sets tracking mode
        /// </summary>
        public async Task<bool> SetTrackingModeAsync(TrackingMode mode)
        {
            if (SerialPort == null) return false;
            var command = new byte[] { (byte)'T', (byte)mode };
            var response = await SerialPort.SendCommandAsync(command);
            if (response != null)
            {
                CurrentTrackingMode = mode;
                OnStatusChanged?.Invoke();
            }
            return response != null;
        }

        #endregion

        #region Telescope Commands - Time/Location

        /// <summary>
        /// Gets telescope time
        /// </summary>
        public async Task<TelescopeTime?> GetTimeAsync()
        {
            if (SerialPort == null) return null;
            var response = await SerialPort.SendCommandAsync(new byte[] { (byte)'h' });
            if (response == null || response.Length < 9) return null;
            return NexStarProtocol.ParseTimeResponse(response);
        }

        /// <summary>
        /// Sets telescope time
        /// </summary>
        public async Task<bool> SetTimeAsync(DateTime time, int tzOffset, bool dst)
        {
            if (SerialPort == null) return false;
            var command = NexStarProtocol.FormatSetTimeCommand(time, tzOffset, dst);
            var response = await SerialPort.SendCommandAsync(command);
            return response != null;
        }

        /// <summary>
        /// Sets telescope time to current browser time
        /// </summary>
        public async Task<bool> SyncTimeAsync()
        {
            var now = DateTime.Now;
            var offset = TimeZoneInfo.Local.GetUtcOffset(now);
            var isDst = TimeZoneInfo.Local.IsDaylightSavingTime(now);
            return await SetTimeAsync(now, (int)offset.TotalHours, isDst);
        }

        /// <summary>
        /// Gets telescope location
        /// </summary>
        public async Task<GeoLocation?> GetLocationAsync()
        {
            if (SerialPort == null) return null;
            var response = await SerialPort.SendCommandAsync(new byte[] { (byte)'w' });
            if (response == null || response.Length < 9) return null;
            Location = NexStarProtocol.ParseLocationResponse(response);
            OnStatusChanged?.Invoke();
            return Location;
        }

        /// <summary>
        /// Sets telescope location
        /// </summary>
        public async Task<bool> SetLocationAsync(double lat, double lon)
        {
            if (SerialPort == null) return false;
            if (lat < -90 || lat > 90 || lon < -180 || lon > 180) return false;
            var command = NexStarProtocol.FormatSetLocationCommand(lat, lon);
            var response = await SerialPort.SendCommandAsync(command);
            if (response != null)
            {
                Location = new GeoLocation(lat, lon);
                OnStatusChanged?.Invoke();
            }
            return response != null;
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
                    AltAz = AstronomyMath.EquatorialToHorizontal(
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

        //private async Task UpdateAsync()
        //{
        //    if (Serial == null) return;

        //    var ports = (await Serial.GetPorts()).ToArray();
        //    var serialPort = ports.FirstOrDefault();
        //    if (serialPort != null && SerialPort == null)
        //    {
        //        SerialPort = serialPort;
        //        SerialPort.OnDisconnect += SerialPort_OnDisconnect;
        //        OnConnected?.Invoke(SerialPort);
        //    }
        //}

        //private void Serial_OnConnect(Event e)
        //{
        //    var serialPort = e.TargetAs<SerialPort>();
        //    if (SerialPort == null && serialPort != null)
        //    {
        //        SerialPort = serialPort;
        //        SerialPort.OnDisconnect += SerialPort_OnDisconnect;
        //        OnConnected?.Invoke(SerialPort);
        //    }
        //}

        private async void SerialPort_OnDisconnect(ProlificSerial e)
        {
            //await StopComsAsync();
            if (SerialPort == e)
            {
                _ = DeselectPortAsync();
            }
        }

        #endregion
    }
}
