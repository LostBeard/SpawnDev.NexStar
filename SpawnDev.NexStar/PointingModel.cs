using System.Text.Json;

namespace SpawnDev.NexStar
{
    /// <summary>
    /// Maps between the sky's true azimuth and altitude and what the mount reports. An aligned hand control reports true values and the model is the
    /// identity. A mount that was powered on without alignment reports azimuth relative to the direction it was facing at power-on, and an altitude that is
    /// right only if the tube was level then; one object centred in the eyepiece ("align here") gives the two offsets between the two frames, and every goto
    /// afterwards is translated through them. The offsets hold until the mount is powered off or its alignment changes.
    /// </summary>
    public sealed class PointingModel
    {
        /// <summary>mount azimuth = true azimuth + this, degrees</summary>
        public double AzimuthOffset { get; private set; }

        /// <summary>mount altitude = true altitude + this, degrees</summary>
        public double AltitudeOffset { get; private set; }

        /// <summary>True once an object has been centred (or the model was loaded).</summary>
        public bool IsSet { get; private set; }

        /// <summary>Works out the offsets from one object that is centred in the eyepiece: where it truly is, and where the mount says it is pointing.</summary>
        public void AlignOn(double trueAzimuth, double trueAltitude, double mountAzimuth, double mountAltitude)
        {
            if (!double.IsFinite(trueAzimuth) || !double.IsFinite(trueAltitude) || !double.IsFinite(mountAzimuth) || !double.IsFinite(mountAltitude))
                throw new ArgumentException("an alignment needs real positions");
            AzimuthOffset = Wrap180(mountAzimuth - trueAzimuth);
            AltitudeOffset = mountAltitude - trueAltitude;
            IsSet = true;
        }

        /// <summary>Back to the identity: the mount's own values are the truth.</summary>
        public void Clear()
        {
            AzimuthOffset = 0;
            AltitudeOffset = 0;
            IsSet = false;
        }

        /// <summary>The mount position that points at a true azimuth and altitude. The altitude is clamped to what the mount can reach.</summary>
        public (double Azimuth, double Altitude) ToMount(double trueAzimuth, double trueAltitude) =>
            (Wrap360(trueAzimuth + AzimuthOffset), Math.Clamp(trueAltitude + AltitudeOffset, -90, 90));

        /// <summary>The true azimuth and altitude the mount is pointing at when it reports a position.</summary>
        public (double Azimuth, double Altitude) FromMount(double mountAzimuth, double mountAltitude) =>
            (Wrap360(mountAzimuth - AzimuthOffset), Math.Clamp(mountAltitude - AltitudeOffset, -90, 90));

        public string ToJson() => JsonSerializer.Serialize(new Saved(AzimuthOffset, AltitudeOffset, IsSet));

        /// <summary>Restores a model from <see cref="ToJson"/>; anything unreadable gives the identity model.</summary>
        public static PointingModel FromJson(string? json)
        {
            var model = new PointingModel();
            if (string.IsNullOrWhiteSpace(json)) return model;
            try
            {
                var saved = JsonSerializer.Deserialize<Saved>(json);
                if (saved != null && saved.IsSet && double.IsFinite(saved.Az) && double.IsFinite(saved.Alt))
                {
                    model.AzimuthOffset = saved.Az;
                    model.AltitudeOffset = saved.Alt;
                    model.IsSet = true;
                }
            }
            catch (JsonException) { }
            return model;
        }

        private sealed record Saved(double Az, double Alt, bool IsSet);

        private static double Wrap360(double degrees)
        {
            var d = degrees % 360.0;
            return d < 0 ? d + 360.0 : d;
        }

        private static double Wrap180(double degrees)
        {
            var d = Wrap360(degrees);
            return d > 180 ? d - 360 : d;
        }
    }
}
