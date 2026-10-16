namespace SpawnDev.NexStar.Eyepiece;

/// <summary>Where the phone's rear camera points, from the phone's own sensors.</summary>
/// <param name="Azimuth">Degrees clockwise from north (0-360)</param>
/// <param name="Altitude">Degrees above the horizon (-90 to 90)</param>
/// <param name="Roll">Degrees the phone is turned about the camera's own axis: 0 when the top of the phone is toward the sky, positive when it has turned clockwise
/// as seen from behind the phone. Undefined (0) when the camera points straight up or down.</param>
public readonly record struct CameraPose(double Azimuth, double Altitude, double Roll)
{
    /// <summary>
    /// The camera pose from the W3C device-orientation angles (degrees). The device frame is x to the right of the screen, y toward the top of the phone
    /// and z out of the screen; the rear camera looks along -z. The Earth frame is x east, y north, z up, and the browser's rotation from one to the other
    /// is Rz(alpha) Rx(beta) Ry(gamma) (alpha counter-clockwise seen from above, as the specification says).
    /// With <c>deviceorientationabsolute</c> (or an iPhone's compass heading made into alpha) the azimuth is against north, magnetic north: the true value
    /// needs the magnetic declination added by the caller.
    /// </summary>
    public static CameraPose FromDeviceOrientation(double alphaDegrees, double betaDegrees, double gammaDegrees)
    {
        var a = alphaDegrees * Math.PI / 180.0;
        var b = betaDegrees * Math.PI / 180.0;
        var g = gammaDegrees * Math.PI / 180.0;
        double ca = Math.Cos(a), sa = Math.Sin(a), cb = Math.Cos(b), sb = Math.Sin(b), cg = Math.Cos(g), sg = Math.Sin(g);

        // R = Rz(a) * Rx(b) * Ry(g), column by column: what the device's x, y, z axes are in the Earth frame
        // Rx(b)*Ry(g): rows
        //   [ cg,        0,   sg      ]
        //   [ sb*sg,     cb, -sb*cg   ]
        //   [ -cb*sg,    sb,  cb*cg   ]
        // then Rz(a) mixes the first two rows
        double m00 = ca * cg - sa * sb * sg, m01 = -sa * cb, m02 = ca * sg + sa * sb * cg;
        double m10 = sa * cg + ca * sb * sg, m11 = ca * cb, m12 = sa * sg - ca * sb * cg;
        double m20 = -cb * sg, m21 = sb, m22 = cb * cg;

        // the camera axis is -z of the device; the top of the phone is +y
        double ax = -m02, ay = -m12, az = -m22;
        double ux = m01, uy = m11, uz = m21;

        var azimuth = Math.Atan2(ax, ay) * 180.0 / Math.PI;
        if (azimuth < 0) azimuth += 360.0;
        var altitude = Math.Asin(Math.Clamp(az, -1.0, 1.0)) * 180.0 / Math.PI;

        // roll: the angle from the sky's "up" (z, projected across the view) to the phone's top
        double dot = az;   // z . axis
        double px = -dot * ax, py = -dot * ay, pz = 1 - dot * az;   // z minus its component along the axis
        var pl = Math.Sqrt(px * px + py * py + pz * pz);
        double roll = 0;
        if (pl > 1e-6)
        {
            px /= pl; py /= pl; pz /= pl;
            // sign: positive when the top of the phone is clockwise from sky-up as seen from behind the phone looking along the axis
            double cx = py * uz - pz * uy, cy = pz * ux - px * uz, cz = px * uy - py * ux;   // p x u
            double sin = cx * ax + cy * ay + cz * az;
            double cos = px * ux + py * uy + pz * uz;
            roll = Math.Atan2(sin, cos) * 180.0 / Math.PI;
        }
        return new CameraPose(azimuth, altitude, roll);
    }

    /// <summary>Adds a declination (true north minus magnetic north, east positive) to turn a compass azimuth into a true one.</summary>
    public CameraPose WithDeclination(double declinationDegrees)
    {
        var az = (Azimuth + declinationDegrees) % 360.0;
        return this with { Azimuth = az < 0 ? az + 360.0 : az };
    }
}
