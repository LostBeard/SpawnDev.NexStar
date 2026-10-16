namespace SpawnDev.NexStar.Sky;

/// <summary>
/// WGSL for the sky. Every pass shares one uniform (<c>View</c>): the J2000-to-horizon rotation, the camera basis and the projection scale, built by
/// <see cref="SkyView"/>, so what the GPU draws and what the HUD computes on the CPU are the same geometry. Catalog positions never leave the GPU: the vertex
/// shader rotates and projects each star itself.
/// </summary>
internal static class SkyShaders
{
    /// <summary>Shared by every pass.</summary>
    public const string Common = """
        struct View {
            rowE: vec4<f32>,
            rowN: vec4<f32>,
            rowU: vec4<f32>,
            right: vec4<f32>,
            up: vec4<f32>,
            fwd: vec4<f32>,
            screen: vec4<f32>,   // x width px, y height px, z pixels per stereographic unit, w magnitude limit
            style: vec4<f32>,    // x star size scale, y night mode (1 = red only), z layer flags, w deep-sky magnitude limit
        }
        @group(0) @binding(0) var<uniform> view: View;

        fn to_h(v: vec3<f32>) -> vec3<f32> {
            return vec3<f32>(dot(view.rowE.xyz, v), dot(view.rowN.xyz, v), dot(view.rowU.xyz, v));
        }

        // pixel position and the camera-space depth (cz) of a horizontal direction
        fn project(h: vec3<f32>) -> vec3<f32> {
            let c = vec3<f32>(dot(h, view.right.xyz), dot(h, view.up.xyz), dot(h, view.fwd.xyz));
            let k = 2.0 / (1.0 + c.z);
            return vec3<f32>(view.screen.x * 0.5 + c.x * k * view.screen.z,
                             view.screen.y * 0.5 - c.y * k * view.screen.z, c.z);
        }

        fn px_to_ndc(p: vec2<f32>) -> vec2<f32> {
            return vec2<f32>(p.x / view.screen.x * 2.0 - 1.0, 1.0 - p.y / view.screen.y * 2.0);
        }

        fn corner(vi: u32) -> vec2<f32> {
            var c = array<vec2<f32>, 6>(
                vec2<f32>(-1.0, -1.0), vec2<f32>(1.0, -1.0), vec2<f32>(-1.0, 1.0),
                vec2<f32>(-1.0, 1.0), vec2<f32>(1.0, -1.0), vec2<f32>(1.0, 1.0));
            return c[vi];
        }

        // night mode keeps only red: the light goes to the red channel by luminance, so a red-adapted eye is left alone
        fn night(rgb: vec3<f32>) -> vec3<f32> {
            if (view.style.y > 0.5) {
                let l = dot(rgb, vec3<f32>(0.3, 0.59, 0.11));
                return vec3<f32>(l, 0.0, 0.0);
            }
            return rgb;
        }

        fn has_flag(bit: f32) -> bool {
            return (u32(view.style.z) & u32(bit)) != 0u;
        }
        """;

    public const string Stars = Common + """

        struct Star {
            pos: vec3<f32>,
            mag: f32,
            bv: f32,
            pmra: f32,
            pmdec: f32,
            hip: u32,
        }
        @group(0) @binding(1) var<storage, read> stars: array<Star>;

        // Johnson B-V to an approximate colour, through the colours of main-sequence stars
        fn star_color(bv: f32) -> vec3<f32> {
            let t = clamp(bv, -0.4, 2.0);
            if (t < 0.0) { return mix(vec3<f32>(0.60, 0.70, 1.00), vec3<f32>(0.78, 0.85, 1.00), (t + 0.4) / 0.4); }
            if (t < 0.4) { return mix(vec3<f32>(0.78, 0.85, 1.00), vec3<f32>(1.00, 0.98, 0.95), t / 0.4); }
            if (t < 0.8) { return mix(vec3<f32>(1.00, 0.98, 0.95), vec3<f32>(1.00, 0.90, 0.70), (t - 0.4) / 0.4); }
            if (t < 1.4) { return mix(vec3<f32>(1.00, 0.90, 0.70), vec3<f32>(1.00, 0.70, 0.45), (t - 0.8) / 0.6); }
            return mix(vec3<f32>(1.00, 0.70, 0.45), vec3<f32>(1.00, 0.50, 0.30), (t - 1.4) / 0.6);
        }

        struct VsOut {
            @builtin(position) pos: vec4<f32>,
            @location(0) offset: vec2<f32>,
            @location(1) color: vec3<f32>,
            @location(2) radius: f32,
        }

        @vertex
        fn vs_main(@builtin(vertex_index) vi: u32, @builtin(instance_index) ii: u32) -> VsOut {
            var out: VsOut;
            out.pos = vec4<f32>(2.0, 2.0, 2.0, 1.0); // off screen unless drawn below
            out.offset = vec2<f32>(0.0);
            out.color = vec3<f32>(0.0);
            out.radius = 1.0;
            let s = stars[ii + 1u]; // record 0 is the file header
            let limit = view.screen.w;
            if (s.mag > limit) { return out; }
            let p = project(to_h(s.pos));
            if (p.z < -0.95) { return out; }
            let t = clamp((limit - s.mag) / (limit + 1.5), 0.0, 1.4);
            let radius = (0.75 + 6.0 * pow(t, 1.6)) * view.style.x;
            let half = radius * 3.2 + 1.5;
            let c = corner(vi);
            out.pos = vec4<f32>(px_to_ndc(p.xy + c * half), 0.0, 1.0);
            out.offset = c * half;
            let fade = clamp((limit - s.mag) / 0.7, 0.0, 1.0);
            out.color = star_color(s.bv) * (0.62 + 0.38 * clamp(t * 1.6, 0.0, 1.0)) * fade;
            out.radius = radius;
            return out;
        }

        @fragment
        fn fs_main(in: VsOut) -> @location(0) vec4<f32> {
            let r2 = dot(in.offset, in.offset);
            let rad2 = in.radius * in.radius;
            let core = exp(-r2 / (rad2 * 0.5 + 0.35));
            let halo = 0.18 * exp(-r2 / (rad2 * 6.0 + 1.0)) * smoothstep(1.5, 4.0, in.radius);
            return vec4<f32>(night(in.color * (core + halo)), 1.0);
        }
        """;

    public const string Background = Common + """

        @vertex
        fn vs_main(@builtin(vertex_index) vi: u32) -> @builtin(position) vec4<f32> {
            var p = array<vec2<f32>, 3>(vec2<f32>(-1.0, -1.0), vec2<f32>(3.0, -1.0), vec2<f32>(-1.0, 3.0));
            return vec4<f32>(p[vi], 0.0, 1.0);
        }

        // 1 on a grid line, 0 away from it, with a constant width in pixels (the angle one pixel covers at this place on the sky is passed in)
        fn grid(angle: f32, step: f32, pixAngle: f32, widthPx: f32) -> f32 {
            let u = angle / step;
            let d = abs(fract(u + 0.5) - 0.5) * step;
            return 1.0 - smoothstep(0.0, pixAngle * widthPx, d);
        }

        @fragment
        fn fs_main(@builtin(position) frag: vec4<f32>) -> @location(0) vec4<f32> {
            let s = view.screen.z;
            let px = (frag.x - view.screen.x * 0.5) / s;
            let py = -(frag.y - view.screen.y * 0.5) / s;
            let rho2 = px * px + py * py;
            let k = 4.0 / (4.0 + rho2);
            let cz = (4.0 - rho2) / (4.0 + rho2);
            let h = normalize(px * k * view.right.xyz + py * k * view.up.xyz + cz * view.fwd.xyz);
            let pixAngle = 4.0 / (s * (4.0 + rho2)); // radians per pixel here
            var col = vec3<f32>(0.0);

            // ground
            if (has_flag(4.0) && h.z < 0.0) { col = vec3<f32>(0.020, 0.012, 0.010); }

            // equatorial grid (J2000): 15 degree RA lines and 10 degree declination lines; the celestial equator a little stronger
            if (has_flag(2.0)) {
                let v = vec3<f32>(
                    view.rowE.x * h.x + view.rowN.x * h.y + view.rowU.x * h.z,
                    view.rowE.y * h.x + view.rowN.y * h.y + view.rowU.y * h.z,
                    view.rowE.z * h.x + view.rowN.z * h.y + view.rowU.z * h.z);
                let ra = atan2(v.y, v.x);
                let dec = asin(clamp(v.z, -1.0, 1.0));
                let fade = clamp(cos(dec) * 4.0, 0.0, 1.0); // RA lines crowd at the poles
                var e = grid(dec, 0.174533, pixAngle, 1.0) * 0.20 + grid(ra, 0.261799, pixAngle, 1.0) * 0.20 * fade;
                e = e + (1.0 - smoothstep(0.0, pixAngle * 1.4, abs(dec))) * 0.35;
                col = col + vec3<f32>(0.10, 0.20, 0.34) * e;
            }

            // altitude / azimuth grid: 15 degrees
            if (has_flag(1.0)) {
                let alt = asin(clamp(h.z, -1.0, 1.0));
                let az = atan2(h.x, h.y);
                let fade = clamp(cos(alt) * 4.0, 0.0, 1.0);
                let g = grid(alt, 0.261799, pixAngle, 1.0) * 0.30 + grid(az, 0.261799, pixAngle, 1.0) * 0.30 * fade;
                col = col + vec3<f32>(0.34, 0.22, 0.10) * g;
            }

            // the horizon
            if (has_flag(4.0)) {
                let hz = 1.0 - smoothstep(0.0, pixAngle * 1.6, abs(h.z));
                col = col + vec3<f32>(0.75, 0.45, 0.18) * hz;
            }
            return vec4<f32>(night(col), 1.0);
        }
        """;

    public const string Lines = Common + """

        // the file stores a segment as six consecutive floats, then two of padding: a vec3 here would put the second end at offset 16
        struct Segment {
            ax: f32, ay: f32, az: f32,
            bx: f32, by: f32, bz: f32,
            pad0: f32, pad1: f32,
        }
        struct LineStyle {
            color: vec4<f32>,
            width: vec4<f32>, // x width px
        }
        @group(0) @binding(1) var<storage, read> segments: array<Segment>;
        @group(0) @binding(2) var<uniform> style: LineStyle;

        struct VsOut {
            @builtin(position) pos: vec4<f32>,
            @location(0) edge: f32,
        }

        @vertex
        fn vs_main(@builtin(vertex_index) vi: u32, @builtin(instance_index) ii: u32) -> VsOut {
            var out: VsOut;
            out.pos = vec4<f32>(2.0, 2.0, 2.0, 1.0);
            out.edge = 0.0;
            let sg = segments[ii + 1u]; // record 0 is the file header
            let pa = project(to_h(vec3<f32>(sg.ax, sg.ay, sg.az)));
            let pb = project(to_h(vec3<f32>(sg.bx, sg.by, sg.bz)));
            if (pa.z < -0.9 || pb.z < -0.9) { return out; }
            let d = pb.xy - pa.xy;
            let len = length(d);
            if (len < 0.001 || len > view.screen.x * 3.0) { return out; }
            let half = style.width.x * 0.5 + 0.75;
            let n = vec2<f32>(-d.y, d.x) / len * half;
            // corners: 0 a-n, 1 a+n, 2 b-n, 3 b-n, 4 a+n, 5 b+n
            var base = array<vec2<f32>, 6>(pa.xy - n, pa.xy + n, pb.xy - n, pb.xy - n, pa.xy + n, pb.xy + n);
            var side = array<f32, 6>(-1.0, 1.0, -1.0, -1.0, 1.0, 1.0);
            out.pos = vec4<f32>(px_to_ndc(base[vi]), 0.0, 1.0);
            out.edge = side[vi];
            return out;
        }

        @fragment
        fn fs_main(in: VsOut) -> @location(0) vec4<f32> {
            let a = style.color.a * (1.0 - smoothstep(0.35, 1.0, abs(in.edge)));
            return vec4<f32>(night(style.color.rgb) * a, 1.0);
        }
        """;

    public const string DeepSky = Common + """

        struct Dso {
            pos: vec3<f32>,
            major: f32,
            minor: f32,
            pa: f32,
            mag: f32,
            packed: u32,
        }
        @group(0) @binding(1) var<storage, read> objects: array<Dso>;

        struct VsOut {
            @builtin(position) pos: vec4<f32>,
            @location(0) offset: vec2<f32>,
            @location(1) shape: vec4<f32>, // x radius px, y type, z axis ratio, w alpha
        }

        @vertex
        fn vs_main(@builtin(vertex_index) vi: u32, @builtin(instance_index) ii: u32) -> VsOut {
            var out: VsOut;
            out.pos = vec4<f32>(2.0, 2.0, 2.0, 1.0);
            out.offset = vec2<f32>(0.0);
            out.shape = vec4<f32>(1.0, 0.0, 1.0, 0.0);
            let o = objects[ii + 1u]; // record 0 is the file header
            let h = to_h(o.pos);
            let p = project(h);
            if (p.z < -0.9) { return out; }
            // apparent size in pixels: the major axis in arcminutes times the local scale (it grows away from the centre of a stereographic chart)
            let k = 2.0 / (1.0 + p.z);
            let pxPerArcmin = view.screen.z * k * (3.14159265 / 180.0 / 60.0);
            let sizePx = o.major * pxPerArcmin;
            let limit = view.style.w;
            // a marker if the object is bright enough for this field of view, or big enough to see as a shape
            if (o.mag > limit && sizePx < 6.0) { return out; }
            let radius = max(sizePx * 0.5, 4.0);
            let half = radius + 3.0;
            let c = corner(vi);
            out.pos = vec4<f32>(px_to_ndc(p.xy + c * half), 0.0, 1.0);
            out.offset = c * half;
            let ratio = select(clamp(o.minor / max(o.major, 0.001), 0.25, 1.0), 1.0, o.major <= 0.0);
            out.shape = vec4<f32>(radius, f32(o.packed & 255u), ratio, 1.0);
            return out;
        }

        fn type_color(t: u32) -> vec3<f32> {
            if (t == 1u || t == 2u) { return vec3<f32>(1.00, 0.62, 0.30); }                  // galaxies
            if (t == 3u) { return vec3<f32>(1.00, 0.88, 0.35); }                             // globular clusters
            if (t == 4u || t == 5u) { return vec3<f32>(0.45, 0.85, 1.00); }                  // open clusters, associations
            if (t == 6u || t == 7u || t == 11u || t == 12u) { return vec3<f32>(0.40, 1.00, 0.55); } // nebulae
            if (t == 8u) { return vec3<f32>(0.55, 0.55, 0.65); }                             // dark nebulae
            if (t == 9u) { return vec3<f32>(0.40, 1.00, 0.85); }                             // planetary nebulae
            if (t == 10u) { return vec3<f32>(1.00, 0.45, 0.70); }                            // supernova remnants
            return vec3<f32>(0.75, 0.75, 0.75);
        }

        @fragment
        fn fs_main(in: VsOut) -> @location(0) vec4<f32> {
            let r = length(in.offset);
            let radius = in.shape.x;
            let t = u32(in.shape.y);
            var d = abs(r - radius);
            var a = 1.0 - smoothstep(0.0, 1.1, d);
            if (t == 3u) { // globular: ring and a cross
                let arm = min(abs(in.offset.x), abs(in.offset.y));
                a = max(a, (1.0 - smoothstep(0.0, 1.0, arm)) * step(r, radius) * 0.9);
            } else if (t == 4u || t == 5u) { // open cluster: a dotted ring
                let ang = atan2(in.offset.y, in.offset.x);
                a = a * step(0.0, sin(ang * 14.0));
            } else if (t >= 6u && t <= 12u && t != 9u && t != 10u) { // nebula: a square
                let m = max(abs(in.offset.x), abs(in.offset.y));
                a = 1.0 - smoothstep(0.0, 1.1, abs(m - radius));
            } else if (t == 9u) { // planetary: ring and four ticks
                let ticks = (1.0 - smoothstep(0.0, 1.0, min(abs(in.offset.x), abs(in.offset.y)))) * step(radius, r) * step(r, radius + 4.0);
                a = max(a, ticks);
            }
            return vec4<f32>(night(type_color(t)) * a * 0.85, 1.0);
        }
        """;
}
