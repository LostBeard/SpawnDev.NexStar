using System.Numerics;

namespace SpawnDev.NexStar.Sky;

/// <summary>
/// Where the observer is, when it is, and which way the chart looks: everything needed to turn a J2000 catalog position into a screen position.
/// <para>
/// The sky is held as unit vectors. A catalog position (J2000 equatorial) becomes a horizontal one (x = East, y = North, z = Up) through one
/// rotation, <see cref="EquatorialToHorizontal"/>: precession from J2000 to the date, then sidereal time and latitude. The chart looks along a
/// direction (<see cref="CenterAzimuth"/>, <see cref="CenterAltitude"/>) and draws with a stereographic projection (conformal: circles stay circles,
/// shapes stay true at every field of view, and it covers the whole sky). The same numbers go to the GPU, so the stars drawn by the shader and the
/// planets, labels and reticle drawn by the HUD land on each other.
/// </para>
/// All screen coordinates are CSS pixels, origin top-left, y down.
/// </summary>
public sealed class SkyView
{
    private readonly double[] _m = new double[9]; // J2000 -> horizontal, row-major: rows E, N, U
    private Vector3 _right, _up, _forward;
    private double _scale; // pixels per stereographic unit

    /// <summary>Latitude, degrees north.</summary>
    public double Latitude { get; set; } = 40.0;

    /// <summary>Longitude, degrees east.</summary>
    public double Longitude { get; set; } = -75.0;

    /// <summary>The instant being shown (UTC).</summary>
    public DateTime Utc { get; set; } = DateTime.UtcNow;

    /// <summary>The azimuth at the centre of the screen, degrees from north through east.</summary>
    public double CenterAzimuth { get; set; } = 180.0;

    /// <summary>The altitude at the centre of the screen, degrees.</summary>
    public double CenterAltitude { get; set; } = 40.0;

    /// <summary>The vertical field of view, degrees. Clamped to <see cref="MinFov"/>..<see cref="MaxFov"/> by <see cref="ZoomAt"/>.</summary>
    public double FovDegrees { get; set; } = 90.0;

    public double WidthPx { get; set; } = 1280;
    public double HeightPx { get; set; } = 800;

    public const double MinFov = 0.05;
    public const double MaxFov = 175.0;

    /// <summary>Camera basis in the horizontal frame: right, up, forward (valid after <see cref="Update"/>).</summary>
    public (Vector3 Right, Vector3 Up, Vector3 Forward) Basis => (_right, _up, _forward);

    /// <summary>Pixels per stereographic unit (valid after <see cref="Update"/>).</summary>
    public double Scale => _scale;

    /// <summary>The 3x3 J2000 -> horizontal rotation, row-major (E, N, U rows).</summary>
    public IReadOnlyList<double> EquatorialToHorizontal => _m;

    /// <summary>Recomputes the rotation and the camera from the current state. Call after changing anything.</summary>
    public void Update()
    {
        // Precession to the date, then the equator-to-horizon rotation (rows E, N, U).
        var p = Precession.J2000ToDateMatrix(Precession.CenturiesTT(Utc));
        var lst = AstronomyMath.GetLocalSiderealTime(Longitude, Utc) * Math.PI / 180.0;
        var phi = Latitude * Math.PI / 180.0;
        double sL = Math.Sin(lst), cL = Math.Cos(lst), sP = Math.Sin(phi), cP = Math.Cos(phi);
        double[,] enu =
        {
            { -sL, cL, 0 },
            { -sP * cL, -sP * sL, cP },
            { cP * cL, cP * sL, sP },
        };
        for (int r = 0; r < 3; r++)
            for (int c = 0; c < 3; c++)
                _m[r * 3 + c] = enu[r, 0] * p[0, c] + enu[r, 1] * p[1, c] + enu[r, 2] * p[2, c];

        CameraFrom(CenterAzimuth, CenterAltitude, out _right, out _up, out _forward);
        _scale = (HeightPx / 2.0) / (2.0 * Math.Tan(FovDegrees * Math.PI / 180.0 / 4.0));
    }

    private static void CameraFrom(double azDeg, double altDeg, out Vector3 right, out Vector3 up, out Vector3 forward)
    {
        forward = FromAzAlt(azDeg, altDeg);
        // looking out from inside the sphere with up = zenith: east is on the right when looking north
        var r = Vector3.Cross(forward, Vector3.UnitZ);
        if (r.LengthSquared() < 1e-12f) r = Vector3.Cross(forward, Vector3.UnitY); // looking straight up or down: north is up
        right = Vector3.Normalize(r);
        up = Vector3.Cross(right, forward);
    }

    /// <summary>The horizontal unit vector (E, N, U) of an azimuth (from north through east) and altitude, degrees.</summary>
    public static Vector3 FromAzAlt(double azDeg, double altDeg)
    {
        var az = azDeg * Math.PI / 180.0;
        var alt = altDeg * Math.PI / 180.0;
        return new Vector3((float)(Math.Cos(alt) * Math.Sin(az)), (float)(Math.Cos(alt) * Math.Cos(az)), (float)Math.Sin(alt));
    }

    /// <summary>The azimuth (0-360) and altitude (-90..90), degrees, of a horizontal vector.</summary>
    public static (double Az, double Alt) ToAzAlt(Vector3 h)
    {
        var az = Math.Atan2(h.X, h.Y) * 180.0 / Math.PI;
        if (az < 0) az += 360.0;
        var alt = Math.Atan2(h.Z, Math.Sqrt((double)h.X * h.X + (double)h.Y * h.Y)) * 180.0 / Math.PI;
        return (az, alt);
    }

    /// <summary>A J2000 equatorial unit vector in the horizontal frame (E, N, U).</summary>
    public Vector3 ToHorizontal(Vector3 j2000)
    {
        var x = _m[0] * j2000.X + _m[1] * j2000.Y + _m[2] * j2000.Z;
        var y = _m[3] * j2000.X + _m[4] * j2000.Y + _m[5] * j2000.Z;
        var z = _m[6] * j2000.X + _m[7] * j2000.Y + _m[8] * j2000.Z;
        return new Vector3((float)x, (float)y, (float)z);
    }

    /// <summary>A horizontal vector as a J2000 equatorial unit vector (the inverse of <see cref="ToHorizontal"/>; the matrix is a rotation, so its transpose).</summary>
    public Vector3 ToJ2000(Vector3 h)
    {
        var x = _m[0] * h.X + _m[3] * h.Y + _m[6] * h.Z;
        var y = _m[1] * h.X + _m[4] * h.Y + _m[7] * h.Z;
        var z = _m[2] * h.X + _m[5] * h.Y + _m[8] * h.Z;
        return new Vector3((float)x, (float)y, (float)z);
    }

    /// <summary>
    /// Where a horizontal direction falls on the screen. False if it is on the far side of the sky (more than about 177 degrees from the centre),
    /// where a stereographic projection runs off to infinity.
    /// </summary>
    public bool TryProject(Vector3 h, out double sx, out double sy)
    {
        var cx = Vector3.Dot(h, _right);
        var cy = Vector3.Dot(h, _up);
        var cz = Vector3.Dot(h, _forward);
        if (cz < -0.998f) { sx = sy = double.NaN; return false; }
        var k = 2.0 / (1.0 + cz);
        sx = WidthPx / 2.0 + cx * k * _scale;
        sy = HeightPx / 2.0 - cy * k * _scale;
        return true;
    }

    /// <summary>The horizontal direction under a screen position (the inverse of <see cref="TryProject"/>).</summary>
    public Vector3 Unproject(double sx, double sy)
    {
        var px = (sx - WidthPx / 2.0) / _scale;
        var py = -(sy - HeightPx / 2.0) / _scale;
        var rho2 = px * px + py * py;
        var k = 4.0 / (4.0 + rho2);
        var cz = (4.0 - rho2) / (4.0 + rho2);
        return Vector3.Normalize((float)(px * k) * _right + (float)(py * k) * _up + (float)cz * _forward);
    }

    /// <summary>How many screen pixels one arcminute spans at a screen position (the stereographic scale grows away from the centre).</summary>
    public double PixelsPerArcminute(Vector3 h)
    {
        var cz = Vector3.Dot(h, _forward);
        var k = 2.0 / (1.0 + Math.Max(cz, -0.998));
        return _scale * k * (Math.PI / 180.0 / 60.0);
    }

    // ---------------------------------------------------------------- interaction

    private double _dragCenterAz, _dragCenterAlt;
    private Vector3 _dragStartDirection;

    /// <summary>Starts a drag at a screen position: the point of sky under it stays under the pointer as it moves.</summary>
    public void BeginDrag(double sx, double sy)
    {
        _dragCenterAz = CenterAzimuth;
        _dragCenterAlt = CenterAltitude;
        _dragStartDirection = Unproject(sx, sy);
    }

    /// <summary>Moves the view so the sky point grabbed by <see cref="BeginDrag"/> is under the pointer at (sx, sy).</summary>
    public void DragTo(double sx, double sy)
    {
        // start from the view as it was when the drag began, then settle the grabbed point onto the pointer
        CenterAzimuth = _dragCenterAz;
        CenterAltitude = _dragCenterAlt;
        Update();
        AnchorToPointer(sx, sy, _dragStartDirection);
    }

    /// <summary>
    /// Turns the view until the sky direction <paramref name="target"/> is under the screen position (sx, sy). The view has no roll (zenith stays up), so a pure
    /// shift of the centre cannot be exact in one step; a few corrections converge to well under a thousandth of a degree.
    /// </summary>
    private void AnchorToPointer(double sx, double sy, Vector3 target)
    {
        var (targetAz, targetAlt) = ToAzAlt(target);
        for (int i = 0; i < 8; i++)
        {
            var (az, alt) = ToAzAlt(Unproject(sx, sy));
            var dAz = targetAz - az;
            if (dAz > 180) dAz -= 360; else if (dAz < -180) dAz += 360;
            var dAlt = targetAlt - alt;
            if (Math.Abs(dAz) * Math.Cos(alt * Math.PI / 180) < 1e-7 && Math.Abs(dAlt) < 1e-7) break;
            CenterAzimuth = Wrap360(CenterAzimuth + dAz);
            CenterAltitude = Math.Clamp(CenterAltitude + dAlt, -90.0, 90.0);
            Update();
        }
    }

    /// <summary>Zooms by <paramref name="factor"/> (below 1 zooms in) keeping the sky under (sx, sy) where it is.</summary>
    public void ZoomAt(double sx, double sy, double factor)
    {
        var anchor = Unproject(sx, sy);
        FovDegrees = Math.Clamp(FovDegrees * factor, MinFov, MaxFov);
        Update();
        AnchorToPointer(sx, sy, anchor);
    }

    /// <summary>Turns the view to look at an azimuth and altitude.</summary>
    public void LookAt(double azDeg, double altDeg)
    {
        CenterAzimuth = Wrap360(azDeg);
        CenterAltitude = Math.Clamp(altDeg, -90.0, 90.0);
        Update();
    }

    private static double Wrap360(double a)
    {
        a %= 360.0;
        return a < 0 ? a + 360.0 : a;
    }

    /// <summary>
    /// The faintest star magnitude to draw at this field of view: the naked-eye sky when the whole sky is in view, and deeper as you zoom, to the
    /// catalog's limit. The count stays modest on every screen (a few thousand stars).
    /// </summary>
    public double MagnitudeLimit => Math.Clamp(5.6 + 1.5 * Math.Log2(120.0 / Math.Max(FovDegrees, MinFov)), 4.8, 12.0);
}
