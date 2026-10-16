using SpawnDev.SpawnJS;
using SpawnDev.SpawnJS.JSObjects;
using System.Numerics;

namespace SpawnDev.NexStar
{
    public class PhoneSensorService : IAsyncBackgroundService, IDisposable
    {
        private readonly SpawnJSRuntime JS;
        private Window Window;
        private bool _isListening = false;
        private bool _requiresRequestPermission = false;
        private Task? _Ready = null;

        // Smoothing
        private const int SmoothingWindowSize = 10;
        private readonly Queue<Vector3> _history = new Queue<Vector3>();

        public event Action? OnSensorUpdated;

        /// <summary>
        /// Ready task for async initialization
        /// </summary>
        public Task Ready => _Ready ??= InitAsync();

        /// <summary>
        /// Current phone orientation (Alpha, Beta, Gamma) in degrees.
        /// </summary>
        public Vector3 CurrentOrientation { get; private set; }

        /// <summary>
        /// Smoothed phone orientation (Alpha, Beta, Gamma) in degrees.
        /// </summary>
        public Vector3 SmoothedOrientation { get; private set; }

        /// <summary>
        /// True if we have successfully requested permissions (if required) and attached listeners.
        /// </summary>
        public bool IsActive => _isListening;

        /// <summary>
        /// True if the device appears to require special permission handling (likely iOS.)
        /// </summary>
        public bool IsIOS => _requiresRequestPermission;

        public PhoneSensorService(SpawnJSRuntime js)
        {
            JS = js;
            Window = JS.WindowThis; // Access the global window object
        }

        public Task InitAsync()
        {
            // Detect iOS specifically for the DeviceOrientationEvent.requestPermission
            // We can check if `DeviceOrientationEvent` exists and has `requestPermission` property
            try
            {
                if (DeviceOrientationEvent.RequestPermissionSupported)
                {
                    _requiresRequestPermission = true;
                }
            }
            catch
            {
                // Fallback or not a device that needs this
                _requiresRequestPermission = false;
            }

            return Task.CompletedTask;
        }

        /// <summary>
        /// Requests permission to access device orientation sensors.
        /// On iOS 13+, this MUST be called from a user gesture (button click).
        /// </summary>
        public async Task<bool> RequestSensorPermission()
        {
            if (_isListening) return true;

            if (_requiresRequestPermission)
            {
                try
                {
                    // DeviceOrientationEvent.requestPermission() returns a promise resolving to 'granted' or 'denied'
                    var state = await DeviceOrientationEvent.RequestPermission();


                    if (state == "granted")
                    {
                        StartListening();
                        return true;
                    }
                    return false;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Permission request failed: {ex.Message}");
                    return false;
                }
            }
            else
            {
                // Android/Other: Permissions usually not required or are implicit for this API,
                // OR handled via Permissions API (which is less common for orientation).
                // We'll just try to start listening.
                StartListening();
                return true;
            }
        }

        private void StartListening()
        {
            if (_isListening) return;

            // Prefer deviceorientationabsolute for Android/absolute reference
            // But also fallback or listen to standard deviceorientation for iOS

            // Note: On some devices both might fire. We should pick one.
            // A common strategy is to try absolute first, if available.

            // However, hooking both and differentiating via event properties is safer.
            // Simplified approach: Hook 'deviceorientationabsolute'. If it fires, use it. 
            // Also hook 'deviceorientation'. Use it if 'absolute' isn't updating or for iOS.

            // Taking a simpler route: Hook both, but prefer Absolute if available data comes through.

            Window.OnDeviceOrientationAbsolute += OnDeviceOrientationAbsolute;
            Window.OnDeviceOrientation += OnDeviceOrientation;

            _isListening = true;
        }

        public void StopListening()
        {
            if (!_isListening) return;
            Window.OnDeviceOrientationAbsolute -= OnDeviceOrientationAbsolute;
            Window.OnDeviceOrientation -= OnDeviceOrientation;
            _isListening = false;
        }

        // Timestamp of last absolute update to prevent "standard" event from overwriting better data
        private double _lastAbsoluteTimestamp = 0;

        private void OnDeviceOrientationAbsolute(DeviceOrientationEvent e)
        {
            if (e.Alpha == null || e.Beta == null || e.Gamma == null) return;

            UpdateOrientation((double)e.Alpha, (double)e.Beta, (double)e.Gamma, true);
        }

        private void OnDeviceOrientation(DeviceOrientationEvent e)
        {
            // If we recently got absolute data (within 100ms), ignore this standard event
            // to avoid jittering between two sources.
            // Iif absolute is firing, we prefer it.

            // Checking if we have valid data
            double alpha = 0;

            // iOS check: webkitCompassHeading
            // SpawnDev.SpawnJS DeviceOrientationEvent might not expose webkitCompassHeading directly if it's standard binding.
            // But we can check dynamic properties.
            var heading = e.WebkitCompassHeading;
            if (heading != null)
            {
                // iOS: Alpha is 0 when device is initialized, not North.
                // webkitCompassHeading is the heading relative to magnetic north.
                // We should use that as "Alpha" for our purposes if available.
                alpha = heading.Value;
                // Note: orientation conventions might differ. 
                // Alpha typically: 0=North, 90=East (counter-clockwise?) No, standard is 0=North, 90=East?
                // Web API: 0=North, increasing counter-clockwise usually.
                // webkitCompassHeading: 0=North, increasing clockwise (degrees).
                // We need to normalize this.
                // Standard alpha: 0=North, East=90? MDN says: z-axis is 0 when pointing North. 
                // Actually MDN says: "alpha... is a value in degrees... motion of the device around the z axis... 0 is north... counter-clockwise"
                // CompasHeading: "degrees... clockwise"
                // So Alpha_standard ~= 360 - CompassHeading

                alpha = (360 - alpha) % 360;
            }
            else
            {
                if (e.Alpha == null) return;
                alpha = (double)e.Alpha;
            }

            if (e.Beta == null || e.Gamma == null) return;

            // If we haven't received absolute data recently (or ever), use this.
            // We'll use a crude "timestamp" check if we were using the event timestamp, 
            // but for now let's just say: if we are on Android (not iOS), and absolute is working, this handler shouldn't overwrite.
            // Since we know `_isIOS` is set in Init:
            if (!_requiresRequestPermission && _lastAbsoluteTimestamp > 0 && (DateTime.UtcNow - _lastAbsoluteTime).TotalSeconds < 0.5)
            {
                return;
            }

            UpdateOrientation(alpha, (double)e.Beta, (double)e.Gamma, false);
        }

        private DateTime _lastAbsoluteTime = DateTime.MinValue;

        private void UpdateOrientation(double alpha, double beta, double gamma, bool isAbsolute)
        {
            if (isAbsolute)
            {
                _lastAbsoluteTime = DateTime.UtcNow;
                _lastAbsoluteTimestamp = 1; // Flag that we are getting absolute data
            }

            CurrentOrientation = new Vector3((float)alpha, (float)beta, (float)gamma);

            // Smoothing
            _history.Enqueue(CurrentOrientation);
            if (_history.Count > SmoothingWindowSize)
            {
                _history.Dequeue();
            }

            SmoothedOrientation = ComputeAverage(_history);
            OnSensorUpdated?.Invoke();
        }

        private Vector3 ComputeAverage(IEnumerable<Vector3> vectors)
        {
            // Simple averaging
            // Note: Angular averaging for Alpha (0-360) has a wrap-around issue (359 vs 1).
            // For a simple implementation initially, we will ignore wrap-around or handle it simply.
            // Given it's a "Push-To" aid, we might not be crossing North constantly, but we should handle it.
            // Proper way: Sum of sines and cosines.

            float sumSinAlpha = 0;
            float sumCosAlpha = 0;
            float sumBeta = 0;
            float sumGamma = 0;
            int count = 0;

            foreach (var v in vectors)
            {
                // Convert Alpha to radians for averaging
                var alphaRad = v.X * MathF.PI / 180f;
                sumSinAlpha += MathF.Sin(alphaRad);
                sumCosAlpha += MathF.Cos(alphaRad);

                sumBeta += v.Y;
                sumGamma += v.Z;
                count++;
            }

            if (count == 0) return Vector3.Zero;

            var avgAlphaRad = MathF.Atan2(sumSinAlpha, sumCosAlpha);
            var avgAlpha = avgAlphaRad * 180f / MathF.PI;
            if (avgAlpha < 0) avgAlpha += 360f;

            var avgBeta = sumBeta / count;
            var avgGamma = sumGamma / count;

            return new Vector3(avgAlpha, avgBeta, avgGamma);
        }

        // ==========================================
        // Calibration & Push-To Logic
        // ==========================================

        private Quaternion _calibrationOffset = Quaternion.Identity;
        public bool IsCalibrated { get; private set; } = false;

        /// <summary>
        /// Calibrates the phone alignment using the telescope's current known position.
        /// Calculates the rotational offset between the Phone's orientation and the Telescope's frame.
        /// </summary>
        public void Calibrate(AzAltCoordinates telescopePos)
        {
            if (telescopePos == null) return;

            // 1. Get current Phone Quaternion (Mean of recent history to be stable)
            var phoneQuat = GetQuaternionFromEuler(SmoothedOrientation);

            // 2. Construct Scope Quaternion from Az/Alt
            // Scope Frame: Identity = North, Level (Az=0, Alt=0)
            // Azimuth = Rotation around Z (Up)
            // Altitude = Rotation around X (Right/East)
            // Note: Azimuth 0=North, 90=East.
            // Standard Math Z-rotation: 0->X. 90->Y.
            // Our Frame: Y=North, X=East.
            // Z-Rot(0) = Y (North).
            // Z-Rot(-90) = X (East).
            // So AngleZ = -Azimuth.

            // Altitude: 0=Horizon, 90=Zenith.
            // Rotation around X (East): Y(North) -> Z(Up) is +90 deg.
            // So AngleX = +Altitude.

            var azRad = (float)(-telescopePos.Azimuth * Math.PI / 180.0);
            var altRad = (float)(telescopePos.Altitude * Math.PI / 180.0);

            var qAz = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, azRad);
            var qAlt = Quaternion.CreateFromAxisAngle(Vector3.UnitX, altRad);

            // Order: Azimuth first (global), then Altitude (local/elevated)
            var scopeQuat = qAz * qAlt;

            // 3. Calculate Offset
            // scope = phone * offset  =>  offset = Inverse(phone) * scope
            _calibrationOffset = Quaternion.Inverse(phoneQuat) * scopeQuat;

            IsCalibrated = true;
        }

        /// <summary>
        /// Gets the calculated Telescope position based on current phone sensors.
        /// Returns null if not calibrated or sensors not active.
        /// </summary>
        public AzAltCoordinates? GetCalculatedPosition()
        {
            if (!IsCalibrated || !_isListening) return null;

            var phoneQuat = GetQuaternionFromEuler(SmoothedOrientation);

            // Apply offset
            // scope = phone * offset
            var scopeQuat = phoneQuat * _calibrationOffset;

            // Extract Az/Alt from scopeQuat
            // Transform "North" vector (0,1,0) by scopeQuat to get pointing vector
            var pointing = Vector3.Transform(Vector3.UnitY, scopeQuat);

            // Alt = Asin(Z)
            var altRad = MathF.Asin(Math.Clamp(pointing.Z, -1f, 1f));
            var alt = altRad * 180f / MathF.PI;

            // Az = Atan2(X, Y)
            // Note: Our Azimuth definition: 0=North(Y), 90=East(X). A bit weird vs standard polar.
            // Standard Polar (from X): 0=X, 90=Y.
            // Here: Vector (X, Y). 
            // If Pointing North (0,1) -> Az 0.
            // If Pointing East (1,0) -> Az 90.
            // Atan2(X, Y) gives:
            // (0, 1) -> 0
            // (1, 0) -> PI/2 (90)
            // (0, -1) -> PI (180)
            // (-1, 0) -> -PI/2 (-90) -> 270.
            // So Atan2(X, Y) works perfectly for Azimuth 0=North, 90=East.

            var azRad = MathF.Atan2(pointing.X, pointing.Y);
            var az = azRad * 180f / MathF.PI;
            if (az < 0) az += 360f;

            return new AzAltCoordinates(az, alt);
        }

        private Quaternion GetQuaternionFromEuler(Vector3 euler)
        {
            // Euler: Alpha (Z), Beta (X), Gamma (Y)
            // Web Standard Order: Z -> X -> Y

            var alphaRad = euler.X * MathF.PI / 180f;
            var betaRad = euler.Y * MathF.PI / 180f;
            var gammaRad = euler.Z * MathF.PI / 180f;

            var qZ = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, alphaRad);
            var qX = Quaternion.CreateFromAxisAngle(Vector3.UnitX, betaRad);
            var qY = Quaternion.CreateFromAxisAngle(Vector3.UnitY, gammaRad);

            // Combined = Z * X * Y
            return qZ * qX * qY;
        }

        public void Dispose()
        {
            StopListening();
        }
    }
}

