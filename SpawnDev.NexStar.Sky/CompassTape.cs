namespace SpawnDev.NexStar.Sky;

/// <summary>One mark on the compass tape.</summary>
/// <param name="X">Pixels from the tape's centre (negative = to the left)</param>
/// <param name="Azimuth">Degrees, 0-360</param>
/// <param name="Label">"N", "NE", "E"... on the eight named points, else null</param>
/// <param name="Major">A mark every 15 degrees is longer than the 5-degree ones</param>
public readonly record struct CompassMark(double X, double Azimuth, string? Label, bool Major);

/// <summary>
/// The marks of a heading tape: which way the chart is looking, with the compass points on it wherever they fall. Always visible, unlike the points on the
/// horizon, which are only on screen when that part of the horizon is.
/// </summary>
public static class CompassTape
{
    private static readonly string[] Names = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };

    /// <summary>The marks within <paramref name="halfWidthPixels"/> of the centre for a view looking at <paramref name="centerAzimuth"/>.</summary>
    public static List<CompassMark> Marks(double centerAzimuth, double pixelsPerDegree, double halfWidthPixels)
    {
        var marks = new List<CompassMark>();
        if (!double.IsFinite(centerAzimuth) || !(pixelsPerDegree > 0) || !(halfWidthPixels > 0)) return marks;
        double c = ((centerAzimuth % 360) + 360) % 360;
        double halfSpan = halfWidthPixels / pixelsPerDegree;
        int first = (int)Math.Floor((c - halfSpan) / 5.0), last = (int)Math.Ceiling((c + halfSpan) / 5.0);
        for (int i = first; i <= last; i++)
        {
            double a = i * 5.0;
            double x = (a - c) * pixelsPerDegree;
            if (Math.Abs(x) > halfWidthPixels) continue;
            double az = ((a % 360) + 360) % 360;
            int whole = (int)Math.Round(az);
            string? label = whole % 45 == 0 ? Names[whole / 45 % 8] : null;
            marks.Add(new CompassMark(x, az, label, whole % 15 == 0));
        }
        return marks;
    }

    /// <summary>The name of the nearest of the eight points ("SSW" is "S" or "SW" by whichever is nearer) with the azimuth, for the readout.</summary>
    public static string Readout(double azimuth)
    {
        double a = ((azimuth % 360) + 360) % 360;
        return $"{Names[(int)Math.Round(a / 45.0) % 8]} {Math.Round(a) % 360:F0}\u00b0";
    }
}
