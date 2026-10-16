using SpawnDev.SpawnJS;
using SpawnDev.SpawnJS.JSObjects;

namespace SpawnDev.NexStar.App.Services
{
    public class LocationService : IAsyncBackgroundService
    {
        private readonly NexStarService NexStar;
        private readonly SpawnJSRuntime JS;
        private Task? _Ready = null;

        /// <summary>
        /// Ready task for async initialization
        /// </summary>
        public Task Ready => _Ready ??= InitAsync();

        /// <summary>
        /// The effective location to use in the app (Telescope > Browser).
        /// </summary>
        public GeoLocation? Location { get; private set; }

        /// <summary>
        /// True if the current Location is coming from the Browser (GPS), false if from Telescope or manual/none.
        /// </summary>
        public bool IsBrowserLocation { get; private set; }

        /// <summary>
        /// Fired when the effective Location changes.
        /// </summary>
        public event Action? OnLocationChanged;

        // Cache browser location to avoid repeated prompts if we fall back to it
        public GeoLocation? CachedBrowserLocation { get; private set; }

        public LocationService(NexStarService nexStar, SpawnJSRuntime js)
        {
            NexStar = nexStar;
            JS = js;
            NexStar.OnStatusChanged += NexStar_OnStatusChanged;
        }

        private async Task InitAsync()
        {
            await RequestBrowserLocationAsync();
            await UpdateLocationAsync();
        }

        private void NexStar_OnStatusChanged()
        {
            // If telescope location changes (or disconnects), re-eval our effective location
            _ = UpdateLocationAsync();
        }

        /// <summary>
        /// Forces an update of the location logic.
        /// </summary>
        public async Task UpdateLocationAsync()
        {
            var oldLat = Location?.Latitude;
            var oldLon = Location?.Longitude;
            var oldIsBrowser = IsBrowserLocation;

            // 1. Priority: Telescope Location
            if (NexStar.SerialPortAvailable && NexStar.Location != null && NexStar.Location.Latitude != 0 && NexStar.Location.Longitude != 0)
            {
                Location = NexStar.Location;
                IsBrowserLocation = false;
            }
            // 2. Fallback: Browser Location
            else if (CachedBrowserLocation != null)
            {
                Location = CachedBrowserLocation;
                IsBrowserLocation = true;
            }
            else
            {
                // Try to get browser location silently if possible, or leave as null?
                // We shouldn't force prompt on Init, only on explicit request or if we can do it silently?
                // The Permissions API isn't always reliable for "Prompt" status across browsers.
                // For now, if we don't have it, we don't have it.
                // BUT, if we want to "fallback" we might want to try fetching it if we haven't yet.
                // Let's rely on explicit "RequestBrowserLocation" or prior cache. 
                // However, user asked for "fallback to browser's location". 
                // We'll check if we have permission? 

                // For now, assume null if not cached or scope.
                Location = null;
                IsBrowserLocation = false;
            }

            // Detect change
            if (Location?.Latitude != oldLat || Location?.Longitude != oldLon || IsBrowserLocation != oldIsBrowser)
            {
                OnLocationChanged?.Invoke();
            }
        }

        /// <summary>
        /// Explicitly requests browser location permission and updates the service state.
        /// </summary>
        public async Task<bool> RequestBrowserLocationAsync()
        {
            try
            {
                using var navigator = JS.Get<Navigator>("navigator");
                using var geolocation = navigator.Geolocation;
                if (geolocation == null) return false;

                var pos = await geolocation.GetCurrentPositionAsync();
                if (pos?.Coords != null)
                {
                    CachedBrowserLocation = new GeoLocation
                    {
                        Latitude = pos.Coords.Latitude,
                        Longitude = pos.Coords.Longitude
                    };
                    await UpdateLocationAsync();
                    return true;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"LocationService: GPS request failed: {ex.Message}");
            }
            return false;
        }
    }
}
