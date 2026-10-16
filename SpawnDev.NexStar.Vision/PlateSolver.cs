using System.Numerics;

namespace SpawnDev.NexStar.Vision;

/// <summary>A star found in the picture: pixel position from the picture centre (x right, y down) and its brightness.</summary>
public readonly record struct DetectedStar(double X, double Y, double Flux);

/// <summary>A star from a catalog: a unit vector in the J2000 equatorial frame (x to RA 0h Dec 0, z to the pole), its magnitude and any id.</summary>
public readonly record struct CatalogEntry(Vector3 Direction, float Magnitude, uint Id = 0);

public sealed class PlateSolveOptions
{
    /// <summary>The plate scale lies between these, arcseconds per pixel.</summary>
    public double MinArcsecPerPixel { get; set; } = 0.5;
    public double MaxArcsecPerPixel { get; set; } = 30;

    /// <summary>Try pictures that are mirror images too (a star diagonal flips an image).</summary>
    public bool AllowMirrored { get; set; } = true;

    /// <summary>A catalog star counts as matching a detection when they are this close, in pixels.</summary>
    public double MatchTolerancePixels { get; set; } = 3.0;

    /// <summary>How many of the brightest detections and catalog stars the pair search starts from.</summary>
    public int AnchorDetections { get; set; } = 12;
    /// <summary>How many of the brightest catalog stars (of those handed to one solve) the pair search starts from.</summary>
    public int AnchorCatalog { get; set; } = 400;

    /// <summary>How many detections the verification looks at (brightest first).</summary>
    public int MaxDetections { get; set; } = 64;

    /// <summary>The fewest matched stars that can be a solution.</summary>
    public int MinMatches { get; set; } = 5;

    /// <summary>A solution is believed only if the chance of its matches arising by accident is below this.</summary>
    public double MaxFalseProbability { get; set; } = 1e-6;

    /// <summary>A hard stop on the search.</summary>
    public long MaxHypotheses { get; set; } = 2_000_000;

    /// <summary>A search that finds this many matches (and over half of all the detections) stops: nothing better can be needed.</summary>
    public int CertainMatches { get; set; } = 12;
}

/// <summary>Where the picture centre points, how it is turned and scaled.</summary>
/// <param name="Center">The sky direction at the picture centre (J2000 unit vector)</param>
/// <param name="RaDegrees">Right ascension of the centre, 0-360</param>
/// <param name="DecDegrees">Declination of the centre</param>
/// <param name="RollDegrees">Position angle of the picture's up direction, from north through east (0 = north is up)</param>
/// <param name="ArcsecPerPixel">The plate scale</param>
/// <param name="Mirrored">The picture is a mirror image of the sky (as through a star diagonal)</param>
/// <param name="Matches">Detections matched to catalog stars</param>
/// <param name="RmsPixels">How far the matched detections lie from their catalog stars, root mean square</param>
/// <param name="FalseProbability">The chance that a search of this size would find this many matches by accident somewhere</param>
/// <param name="Pairs">Which detection (index into the detections passed in, brightest first order is NOT assumed) matched which catalog entry (index into the catalog passed in)</param>
public sealed record PlateSolution(Vector3 Center, double RaDegrees, double DecDegrees, double RollDegrees, double ArcsecPerPixel, bool Mirrored, int Matches, double RmsPixels,
    double FalseProbability, IReadOnlyList<(int Detection, int Catalog)> Pairs);

/// <summary>
/// Plate solving: given the stars detected in a picture and the catalog stars near where the telescope is thought to point, find exactly where it points,
/// how the picture is rotated and how large a pixel is. A pair of detected stars is matched against pairs of catalog stars whose separation fits the
/// allowed plate scale; each such guess fixes a rotation, scale and position, and is kept if many other detections land on catalog stars. The best is
/// refined by least squares and re-projected about its own centre, so the flat approximation of the sky cannot bend the answer.
/// </summary>
public static class PlateSolver
{
    private const double ArcsecToRad = Math.PI / 180.0 / 3600.0;

    /// <summary>
    /// Solves a picture against the catalog stars handed in, which should be the stars near where the telescope points (a cone a little wider than the picture
    /// plus the uncertainty of the pointing). For a larger uncertainty use <see cref="SolveSearch"/>.
    /// </summary>
    public static PlateSolution? Solve(IReadOnlyList<DetectedStar> detections, IReadOnlyList<CatalogEntry> catalog, Vector3 priorCenter, int width, int height, PlateSolveOptions? options = null)
    {
        options ??= new PlateSolveOptions();
        long trials = 0;
        return SolveCore(detections, catalog, priorCenter, width, height, options, ref trials);
    }

    /// <summary>
    /// Solves when the pointing is only known to within <paramref name="searchRadiusDegrees"/>: the sky around the prior is tiled with overlapping patches the
    /// size of the picture and each is tried, nearest the prior first, until one solves. The chance of an accidental match is counted over every guess in
    /// every patch, so searching wider never makes a wrong answer more believable.
    /// </summary>
    public static PlateSolution? SolveSearch(IReadOnlyList<DetectedStar> detections, IReadOnlyList<CatalogEntry> catalog, Vector3 priorCenter, double searchRadiusDegrees, int width, int height, PlateSolveOptions? options = null)
    {
        options ??= new PlateSolveOptions();
        double fieldRadiusDeg = 0.5 * Math.Sqrt((double)width * width + (double)height * height) * options.MaxArcsecPerPixel / 3600.0;
        double spacing = Math.Max(fieldRadiusDeg * 0.9, 0.05);          // patches overlap so any picture centre is within spacing/2 of some patch centre
        double patchRadiusDeg = fieldRadiusDeg + spacing * 0.75;
        double cosPatch = Math.Cos(patchRadiusDeg * Math.PI / 180.0);

        var c0 = Normalize(ToDouble(priorCenter));
        var (e0, n0) = Basis(c0);
        // patch centres on a square grid in the tangent plane about the prior, ordered by distance from it
        var centers = new List<(double Dist, Vector3d Center)>();
        double stepRad = spacing * Math.PI / 180.0, limit = searchRadiusDegrees * Math.PI / 180.0;
        int rings = (int)Math.Ceiling(limit / stepRad);
        for (int ix = -rings; ix <= rings; ix++)
            for (int iy = -rings; iy <= rings; iy++)
            {
                double xi = ix * stepRad, eta = iy * stepRad, r = Math.Sqrt(xi * xi + eta * eta);
                if (r > limit + stepRad * 0.5) continue;
                centers.Add((r, Unproject(c0, e0, n0, xi, eta)));
            }
        centers.Sort((p, q) => p.Dist.CompareTo(q.Dist));

        long trials = 0;
        foreach (var (_, center) in centers)
        {
            var patch = new List<CatalogEntry>();
            foreach (var entry in catalog)
                if (ToDouble(entry.Direction).Dot(center) >= cosPatch) patch.Add(entry);
            if (patch.Count < options.MinMatches) continue;
            var cf = new Vector3((float)center.X, (float)center.Y, (float)center.Z);
            var solution = SolveCore(detections, patch, cf, width, height, options, ref trials);
            if (solution != null)
            {
                // the patch's catalog indices are positions in the patch: map them back to the catalog handed in
                var map = new Dictionary<uint, int>();
                var back = solution.Pairs.Select(p => (p.Detection, Catalog: IndexInCatalog(catalog, patch[p.Catalog]))).ToList();
                return solution with { Pairs = back };
            }
            if (trials > options.MaxHypotheses * 8) break;
        }
        return null;
    }

    private static int IndexInCatalog(IReadOnlyList<CatalogEntry> catalog, CatalogEntry entry)
    {
        for (int i = 0; i < catalog.Count; i++) if (catalog[i].Direction == entry.Direction && catalog[i].Id == entry.Id) return i;
        return -1;
    }

    private static PlateSolution? SolveCore(IReadOnlyList<DetectedStar> detections, IReadOnlyList<CatalogEntry> catalog, Vector3 priorCenter, int width, int height, PlateSolveOptions options, ref long trials)
    {
        if (detections.Count < options.MinMatches || catalog.Count < options.MinMatches) return null;
        if (!(options.MinArcsecPerPixel > 0 && options.MaxArcsecPerPixel > options.MinArcsecPerPixel)) throw new ArgumentException("the plate scale range is not a range");

        // detections, brightest first (remembering where each came from)
        var detOrder = Enumerable.Range(0, detections.Count)
            .Where(i => double.IsFinite(detections[i].X) && double.IsFinite(detections[i].Y) && double.IsFinite(detections[i].Flux))
            .OrderByDescending(i => detections[i].Flux).Take(options.MaxDetections).ToArray();
        if (detOrder.Length < options.MinMatches) return null;
        var det = detOrder.Select(i => detections[i]).ToArray();

        // catalog, brightest first
        var order = Enumerable.Range(0, catalog.Count).OrderBy(i => catalog[i].Magnitude).ToArray();
        var dirs = order.Select(i => ToDouble(catalog[i].Direction)).ToArray();
        int nCat = dirs.Length;
        var cx = new double[nCat];
        var cy = new double[nCat];

        // the tangent plane about the prior centre
        var c0 = Normalize(ToDouble(priorCenter));
        ProjectAll(c0, dirs, cx, cy);
        if (cx.Count(v => !double.IsNaN(v)) < options.MinMatches) return null;

        double sMin = options.MinArcsecPerPixel * ArcsecToRad, sMax = options.MaxArcsecPerPixel * ArcsecToRad;
        var grid = new Grid(cx, cy, Math.Max(options.MatchTolerancePixels * sMax, 1e-9));

        // catalog anchors: the brightest stars on this side of the sky, and their pairs by separation
        var anchors = Enumerable.Range(0, nCat).Where(i => !double.IsNaN(cx[i])).Take(options.AnchorCatalog).ToArray();
        var pairs = new List<(double L, int A, int B)>(anchors.Length * (anchors.Length - 1) / 2);
        for (int i = 0; i < anchors.Length; i++)
            for (int j = i + 1; j < anchors.Length; j++)
            {
                double dx = cx[anchors[j]] - cx[anchors[i]], dy = cy[anchors[j]] - cy[anchors[i]];
                pairs.Add((Math.Sqrt(dx * dx + dy * dy), anchors[i], anchors[j]));
            }
        pairs.Sort((p, q) => p.L.CompareTo(q.L));
        var lengths = pairs.Select(p => p.L).ToArray();

        int ad = Math.Min(options.AnchorDetections, det.Length);
        long hypotheses = 0;   // this tile's guesses; the caller's running total is trials
        Hypothesis? best = null;

        for (int a = 0; a < ad; a++)
        {
            for (int b = a + 1; b < ad; b++)
            {
                double dxp = det[b].X - det[a].X, dyp = det[b].Y - det[a].Y;
                double dPix = Math.Sqrt(dxp * dxp + dyp * dyp);
                if (dPix < 4) continue;   // too close to give a rotation or a scale worth anything
                int lo = LowerBound(lengths, dPix * sMin), hi = LowerBound(lengths, dPix * sMax * 1.0000001);
                for (int sigmaIndex = 0; sigmaIndex < (options.AllowMirrored ? 2 : 1); sigmaIndex++)
                {
                    double sigma = sigmaIndex == 0 ? 1.0 : -1.0;
                    double zax = det[a].X, zay = sigma * det[a].Y;
                    double angDet = Math.Atan2(sigma * (det[b].Y - det[a].Y), det[b].X - det[a].X);
                    for (int k = lo; k < hi; k++)
                    {
                        for (int orient = 0; orient < 2; orient++)
                        {
                            if (++hypotheses > options.MaxHypotheses || trials + hypotheses > options.MaxHypotheses * 4) goto done;
                            var (L, ia, ib) = pairs[k];
                            int A = orient == 0 ? ia : ib, B = orient == 0 ? ib : ia;
                            double s = L / dPix;
                            double theta = Math.Atan2(cy[B] - cy[A], cx[B] - cx[A]) - angDet;
                            double cs = Math.Cos(theta) * s, sn = Math.Sin(theta) * s;
                            // w = s R(theta) z + t, with t so that detection a lands on catalog star A
                            double tx = cx[A] - (cs * zax - sn * zay), ty = cy[A] - (sn * zax + cs * zay);
                            var h = Verify(det, grid, cs, sn, tx, ty, sigma, options.MatchTolerancePixels, quickLook: 10, quickNeed: 4);
                            if (h.Matches < options.MinMatches) continue;
                            if (best == null || h.Matches > best.Matches || (h.Matches == best.Matches && h.Rms < best.Rms)) best = h;
                            if (best.Matches >= options.CertainMatches && best.Matches * 2 > det.Length) goto done;
                        }
                    }
                }
            }
        }
    done:
        trials += hypotheses;
        if (best == null) return null;

        // Refine. Fit a similarity to the matches, re-match with it, move the plane's centre to the picture centre and project the catalog afresh about
        // it (the flat plane about the prior centre is only an approximation, and a plane about the wrong centre turns the picture a little wrong),
        // and repeat until the centre stops moving.
        var matches = best.Pairs;
        double sigmaFinal = best.Sigma;
        var center = c0;
        for (int iteration = 0; iteration < 6; iteration++)
        {
            var (ec, nc) = Basis(center);
            ProjectAll(center, dirs, cx, cy);
            var fit = Fit(det, cx, cy, matches, sigmaFinal);
            if (fit == null) return null;
            var (a, bRe, bIm, _) = fit.Value;
            if (iteration < 4)
            {
                double sNow = Math.Sqrt(a.Re * a.Re + a.Im * a.Im);
                var g = new Grid(cx, cy, Math.Max(options.MatchTolerancePixels * sNow, 1e-9));
                var h = Verify(det, g, a.Re, a.Im, bRe, bIm, sigmaFinal, iteration < 2 ? options.MatchTolerancePixels : Math.Min(options.MatchTolerancePixels, 2.0));
                if (h.Matches >= Math.Max(3, matches.Count - 1)) matches = h.Pairs;
                fit = Fit(det, cx, cy, matches, sigmaFinal);
                if (fit == null) return null;
                (a, bRe, bIm, _) = fit.Value;
            }
            var moved = Unproject(center, ec, nc, bRe, bIm);
            double movement = Math.Acos(Math.Clamp(moved.Dot(center), -1, 1));
            center = moved;
            if (movement < 1e-10 && iteration >= 2) break;
        }

        // the final plane is about the final centre
        ProjectAll(center, dirs, cx, cy);
        var last = Fit(det, cx, cy, matches, sigmaFinal);
        if (last == null) return null;
        var af = last.Value.A;
        double rms = last.Value.Rms;
        double scaleArcsec = Math.Sqrt(af.Re * af.Re + af.Im * af.Im) / ArcsecToRad;

        // how likely is it that this many matches happened by chance? expected random matches = detections x catalog stars in view x (match area / picture area)
        int inView = CountInView(cx, cy, af.Re, af.Im, sigmaFinal, width, height);
        double matchAreaPx = Math.PI * options.MatchTolerancePixels * options.MatchTolerancePixels;
        double lambda = det.Length * inView * matchAreaPx / ((double)width * height);
        // the chance of that many matches at one guess, times the number of guesses the search made: somewhere among them chance could have done it
        double pFalse = Math.Min(1.0, PoissonTail(matches.Count, lambda) * Math.Max(1, trials + hypotheses));
        if (matches.Count < options.MinMatches || pFalse > options.MaxFalseProbability || rms > options.MatchTolerancePixels) return null;

        // the picture's up direction on the sky: w = a * (x + i sigma y) for (x, y) = (0, -1) is a * (-i sigma) = sigma * (Im - i Re), as (xi, eta)
        double upXi = sigmaFinal * af.Im, upEta = -sigmaFinal * af.Re;
        double roll = Math.Atan2(upXi, upEta) * 180.0 / Math.PI;
        if (roll < 0) roll += 360.0;

        var centerF = new Vector3((float)center.X, (float)center.Y, (float)center.Z);
        double ra = Math.Atan2(center.Y, center.X) * 180.0 / Math.PI; if (ra < 0) ra += 360.0;
        double dec = Math.Asin(Math.Clamp(center.Z, -1, 1)) * 180.0 / Math.PI;
        var mapped = matches.Select(m => (detOrder[m.Det], order[m.Cat])).ToList();
        return new PlateSolution(centerF, ra, dec, roll, scaleArcsec, sigmaFinal < 0, matches.Count, rms, pFalse, mapped);
    }

    private static void ProjectAll(Vector3d center, Vector3d[] dirs, double[] cx, double[] cy)
    {
        var (e, n) = Basis(center);
        for (int i = 0; i < dirs.Length; i++)
        {
            if (Project(center, e, n, dirs[i], out var xi, out var eta)) { cx[i] = xi; cy[i] = eta; }
            else { cx[i] = double.NaN; cy[i] = double.NaN; }
        }
    }

    // ---------- the candidate and its check ----------

    private sealed record Hypothesis(int Matches, double Rms, double Sigma, double Scale, List<(int Det, int Cat)> Pairs);

    /// <summary>Applies w = (cs + i sn)(x + i sigma y) + t to every detection and counts those that land on a catalog star.</summary>
    private static Hypothesis Verify(DetectedStar[] det, Grid grid, double cs, double sn, double tx, double ty, double sigma, double tolPx, int quickLook = 0, int quickNeed = 0)
    {
        double scale = Math.Sqrt(cs * cs + sn * sn);
        double tol = tolPx * scale;
        var pairs = new List<(int, int)>();
        double sum = 0;
        var used = new HashSet<int>();
        for (int k = 0; k < det.Length; k++)
        {
            double zx = det[k].X, zy = sigma * det[k].Y;
            double wx = cs * zx - sn * zy + tx, wy = sn * zx + cs * zy + ty;
            int nearest = grid.Nearest(wx, wy, tol, out double d2);
            if (nearest >= 0 && used.Add(nearest))
            {
                pairs.Add((k, nearest));
                sum += d2;
            }
            // most wrong guesses are obvious from the brightest few detections alone
            if (k + 1 == quickLook && pairs.Count < quickNeed) return new Hypothesis(pairs.Count, double.PositiveInfinity, sigma, scale, pairs);
        }
        double rms = pairs.Count == 0 ? double.PositiveInfinity : Math.Sqrt(sum / pairs.Count) / scale;
        return new Hypothesis(pairs.Count, rms, sigma, scale, pairs);
    }

    /// <summary>The least-squares similarity w = a z + b over the matches (z = x + i sigma y, w = xi + i eta): closed form in complex numbers.</summary>
    private static ((double Re, double Im) A, double BRe, double BIm, double Rms)? Fit(DetectedStar[] det, double[] cx, double[] cy, IReadOnlyList<(int Det, int Cat)> pairs, double sigma)
    {
        int n = pairs.Count;
        if (n < 2) return null;
        double zxm = 0, zym = 0, wxm = 0, wym = 0;
        foreach (var (d, c) in pairs) { zxm += det[d].X; zym += sigma * det[d].Y; wxm += cx[c]; wym += cy[c]; }
        zxm /= n; zym /= n; wxm /= n; wym /= n;
        double numRe = 0, numIm = 0, den = 0;
        foreach (var (d, c) in pairs)
        {
            double zx = det[d].X - zxm, zy = sigma * det[d].Y - zym, wx = cx[c] - wxm, wy = cy[c] - wym;
            numRe += zx * wx + zy * wy;      // conj(z) * w
            numIm += zx * wy - zy * wx;
            den += zx * zx + zy * zy;
        }
        if (den <= 0) return null;
        double aRe = numRe / den, aIm = numIm / den;
        double bRe = wxm - (aRe * zxm - aIm * zym), bIm = wym - (aIm * zxm + aRe * zym);
        double s = Math.Sqrt(aRe * aRe + aIm * aIm);
        if (!(s > 0)) return null;
        double sum = 0;
        foreach (var (d, c) in pairs)
        {
            double zx = det[d].X, zy = sigma * det[d].Y;
            double wx = aRe * zx - aIm * zy + bRe, wy = aIm * zx + aRe * zy + bIm;
            double ex = wx - cx[c], ey = wy - cy[c];
            sum += ex * ex + ey * ey;
        }
        return ((aRe, aIm), bRe, bIm, Math.Sqrt(sum / n) / s);
    }

    /// <summary>How many catalog stars fall inside the picture, given w = a z about the picture's own centre.</summary>
    private static int CountInView(double[] cx, double[] cy, double aRe, double aIm, double sigma, int width, int height)
    {
        double den = aRe * aRe + aIm * aIm;
        int count = 0;
        for (int i = 0; i < cx.Length; i++)
        {
            if (double.IsNaN(cx[i])) continue;
            double zx = (cx[i] * aRe + cy[i] * aIm) / den, zy = (cy[i] * aRe - cx[i] * aIm) / den;
            if (Math.Abs(zx) <= width / 2.0 && Math.Abs(zy * sigma) <= height / 2.0) count++;
        }
        return count;
    }

    /// <summary>P(X >= k) for a Poisson variable with mean lambda.</summary>
    public static double PoissonTail(int k, double lambda)
    {
        if (k <= 0) return 1;
        if (lambda <= 0) return 0;
        double term = Math.Exp(-lambda), cdf = term;
        for (int i = 1; i < k; i++) { term *= lambda / i; cdf += term; }
        return Math.Max(0, 1 - cdf);
    }

    private static int LowerBound(double[] sorted, double value)
    {
        int lo = 0, hi = sorted.Length;
        while (lo < hi) { int mid = (lo + hi) / 2; if (sorted[mid] < value) lo = mid + 1; else hi = mid; }
        return lo;
    }

    // ---------- the flat sky about a centre ----------

    internal readonly record struct Vector3d(double X, double Y, double Z)
    {
        public static Vector3d operator +(Vector3d a, Vector3d b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Vector3d operator *(Vector3d a, double s) => new(a.X * s, a.Y * s, a.Z * s);
        public double Dot(Vector3d o) => X * o.X + Y * o.Y + Z * o.Z;
        public Vector3d Cross(Vector3d o) => new(Y * o.Z - Z * o.Y, Z * o.X - X * o.Z, X * o.Y - Y * o.X);
    }

    private static Vector3d ToDouble(Vector3 v) => new(v.X, v.Y, v.Z);

    private static Vector3d Normalize(Vector3d v)
    {
        double l = Math.Sqrt(v.Dot(v));
        if (!(l > 0)) throw new ArgumentException("not a direction");
        return v * (1 / l);
    }

    /// <summary>East and north unit vectors of the tangent plane at <paramref name="c"/>.</summary>
    internal static (Vector3d East, Vector3d North) Basis(Vector3d c)
    {
        var pole = new Vector3d(0, 0, 1);
        var east = pole.Cross(c);
        if (east.Dot(east) < 1e-18) east = new Vector3d(0, 1, 0);
        east = Normalize(east);
        return (east, c.Cross(east));
    }

    /// <summary>Gnomonic projection of a direction about a centre; false when it is on the far side.</summary>
    internal static bool Project(Vector3d c, Vector3d east, Vector3d north, Vector3d u, out double xi, out double eta)
    {
        double d = u.Dot(c);
        if (d <= 1e-6) { xi = eta = 0; return false; }
        xi = u.Dot(east) / d;
        eta = u.Dot(north) / d;
        return true;
    }

    internal static Vector3d Unproject(Vector3d c, Vector3d east, Vector3d north, double xi, double eta) => Normalize(c + east * xi + north * eta);

    /// <summary>A grid over points in the plane for nearest-neighbour checks within a small tolerance. Points that are NaN are left out.</summary>
    private sealed class Grid
    {
        private readonly double _cell;
        private readonly double[] _x, _y;
        private readonly Dictionary<long, List<int>> _cells = new();

        public Grid(double[] x, double[] y, double cell)
        {
            _cell = cell;
            _x = x;
            _y = y;
            for (int i = 0; i < x.Length; i++)
            {
                if (double.IsNaN(x[i])) continue;
                var key = Key((long)Math.Floor(x[i] / cell), (long)Math.Floor(y[i] / cell));
                if (!_cells.TryGetValue(key, out var list)) _cells[key] = list = new List<int>(2);
                list.Add(i);
            }
        }

        private static long Key(long x, long y) => (x << 32) ^ (y & 0xFFFFFFFFL);

        /// <summary>How many points with an index below <paramref name="below"/> (the brighter ones) lie within <paramref name="radius"/>; counting stops at <paramref name="stopAt"/>.</summary>
        public int CountWithin(double x, double y, double radius, int below, int stopAt)
        {
            long cx = (long)Math.Floor(x / _cell), cy = (long)Math.Floor(y / _cell);
            int reach = Math.Max(1, (int)Math.Ceiling(radius / _cell));
            int count = 0;
            double r2 = radius * radius;
            for (long gx = cx - reach; gx <= cx + reach; gx++)
                for (long gy = cy - reach; gy <= cy + reach; gy++)
                    if (_cells.TryGetValue(Key(gx, gy), out var list))
                        foreach (var i in list)
                        {
                            if (i >= below) continue;
                            double dx = _x[i] - x, dy = _y[i] - y;
                            if (dx * dx + dy * dy <= r2 && ++count >= stopAt) return count;
                        }
            return count;
        }

        /// <summary>The index of the nearest point within <paramref name="tol"/>, or -1; <paramref name="d2"/> is the squared distance.</summary>
        public int Nearest(double x, double y, double tol, out double d2)
        {
            long cx = (long)Math.Floor(x / _cell), cy = (long)Math.Floor(y / _cell);
            int reach = Math.Max(1, (int)Math.Ceiling(tol / _cell));
            int best = -1; d2 = double.PositiveInfinity;
            for (long gx = cx - reach; gx <= cx + reach; gx++)
                for (long gy = cy - reach; gy <= cy + reach; gy++)
                    if (_cells.TryGetValue(Key(gx, gy), out var list))
                        foreach (var i in list)
                        {
                            double dx = _x[i] - x, dy = _y[i] - y, dd = dx * dx + dy * dy;
                            if (dd < d2 && dd <= tol * tol) { d2 = dd; best = i; }
                        }
            return best;
        }
    }
}
