using SpawnDev.NexStar.Vision;
using SpawnDev.SpawnJS.JSObjects;

namespace SpawnDev.NexStar.Vision.Gpu;

/// <summary>
/// Finds the stars in a picture on the GPU. The picture stays a texture: one pass measures the sky background and its noise in 32 x 32 pixel tiles (with the
/// stars clipped out), a second finds every pixel that stands well above the local background, is the brightest in its 5 x 5 neighbourhood and has bright
/// neighbours (a star is a blur, a hot pixel is a spike), and measures its centre of light over 7 x 7 pixels. Only the list of stars, a few hundred numbers,
/// comes back to .NET, because the plate solver needs them as numbers.
/// </summary>
public sealed class StarDetector : IDisposable
{
    public const int MaxStars = 256;
    private const int TileSize = 32;

    private readonly GPUDevice _device;
    private readonly GPUQueue _queue;
    private GPUComputePipeline? _statsPipeline, _detectPipeline;
    private GPUBuffer? _params, _tiles, _output, _staging;
    private int _tilesCapacity;

    /// <summary>A pixel must stand this many standard deviations of the local noise above the background.</summary>
    public float SigmaThreshold { get; set; } = 5.0f;

    /// <summary>... and at least this much (0 to 1, of full brightness), so a very quiet picture does not turn noise into stars.</summary>
    public float MinimumHeight { get; set; } = 0.035f;

    /// <summary>The four neighbours of a star's peak must be at least this fraction of its height (a hot pixel has none).</summary>
    public float MinimumNeighbourFraction { get; set; } = 0.15f;

    public StarDetector(GPUDevice device, GPUQueue queue)
    {
        _device = device;
        _queue = queue;
        _statsPipeline = Pipeline(StatsShader, "stats");
        _detectPipeline = Pipeline(DetectShader, "detect");
        _params = device.CreateBuffer(new GPUBufferDescriptor { Size = 48, Usage = GPUBufferUsage.Uniform | GPUBufferUsage.CopyDst });
        long outputBytes = 16 + MaxStars * 16;
        _output = device.CreateBuffer(new GPUBufferDescriptor { Size = (ulong)outputBytes, Usage = GPUBufferUsage.Storage | GPUBufferUsage.CopyDst | GPUBufferUsage.CopySrc });
        _staging = device.CreateBuffer(new GPUBufferDescriptor { Size = (ulong)outputBytes, Usage = GPUBufferUsage.MapRead | GPUBufferUsage.CopyDst });
    }

    private GPUComputePipeline Pipeline(string wgsl, string label)
    {
        using var module = _device.CreateShaderModule(new GPUShaderModuleDescriptor { Code = wgsl });
        return _device.CreateComputePipeline(new GPUComputePipelineDescriptor
        {
            Layout = "auto",
            Compute = new GPUProgrammableStage { Module = module, EntryPoint = "main" },
        });
    }

    /// <summary>
    /// Finds the stars in <paramref name="frame"/> (an rgba8unorm texture view of <paramref name="width"/> x <paramref name="height"/>), brightest first, at
    /// most <paramref name="maxStars"/> of them. Positions are in pixels from the picture's centre (x right, y down).
    /// </summary>
    public async Task<List<DetectedStar>> DetectAsync(GPUTextureView frame, int width, int height, int maxStars = 64)
    {
        if (width < 16 || height < 16) throw new ArgumentException("the picture is too small");
        int tilesX = (width + TileSize - 1) / TileSize, tilesY = (height + TileSize - 1) / TileSize;
        EnsureTiles(tilesX * tilesY);

        // parameters
        var p = new byte[48];
        BitConverter.GetBytes((uint)width).CopyTo(p, 0);
        BitConverter.GetBytes((uint)height).CopyTo(p, 4);
        BitConverter.GetBytes((uint)tilesX).CopyTo(p, 8);
        BitConverter.GetBytes((uint)tilesY).CopyTo(p, 12);
        BitConverter.GetBytes(SigmaThreshold).CopyTo(p, 16);
        BitConverter.GetBytes(MinimumHeight).CopyTo(p, 20);
        BitConverter.GetBytes(MinimumNeighbourFraction).CopyTo(p, 24);
        BitConverter.GetBytes((uint)MaxStars).CopyTo(p, 28);
        _queue.WriteBuffer(_params!, 0, p);
        _queue.WriteBuffer(_output!, 0, new byte[16]);   // the count back to zero

        using var statsLayout = _statsPipeline!.GetBindGroupLayout(0);
        using var detectLayout = _detectPipeline!.GetBindGroupLayout(0);
        using var statsGroup = _device.CreateBindGroup(new GPUBindGroupDescriptor
        {
            Layout = statsLayout,
            Entries = new[]
            {
                new GPUBindGroupEntry { Binding = 0, Resource = new GPUBufferBinding { Buffer = _params! } },
                new GPUBindGroupEntry { Binding = 1, Resource = frame },
                new GPUBindGroupEntry { Binding = 2, Resource = new GPUBufferBinding { Buffer = _tiles! } },
            },
        });
        using var detectGroup = _device.CreateBindGroup(new GPUBindGroupDescriptor
        {
            Layout = detectLayout,
            Entries = new[]
            {
                new GPUBindGroupEntry { Binding = 0, Resource = new GPUBufferBinding { Buffer = _params! } },
                new GPUBindGroupEntry { Binding = 1, Resource = frame },
                new GPUBindGroupEntry { Binding = 2, Resource = new GPUBufferBinding { Buffer = _tiles! } },
                new GPUBindGroupEntry { Binding = 3, Resource = new GPUBufferBinding { Buffer = _output! } },
            },
        });

        using (var encoder = _device.CreateCommandEncoder())
        {
            using (var pass = encoder.BeginComputePass())
            {
                pass.SetPipeline(_statsPipeline);
                pass.SetBindGroup(0, statsGroup);
                pass.DispatchWorkgroups((uint)tilesX, (uint)tilesY);
                pass.SetPipeline(_detectPipeline);
                pass.SetBindGroup(0, detectGroup);
                pass.DispatchWorkgroups((uint)((width + 7) / 8), (uint)((height + 7) / 8));
                pass.End();
            }
            encoder.CopyBufferToBuffer(_output!, 0, _staging!, 0, (ulong)(16 + MaxStars * 16));
            using var commands = encoder.Finish();
            _queue.Submit(new[] { commands });
        }

        await _staging!.MapAsync(GPUMapMode.Read);
        byte[] bytes;
        using (var mapped = _staging.GetMappedRange()) bytes = mapped.ReadBytes();
        _staging.Unmap();

        int count = (int)Math.Min(BitConverter.ToUInt32(bytes, 0), MaxStars);
        var stars = new List<DetectedStar>(count);
        for (int i = 0; i < count; i++)
        {
            int o = 16 + i * 16;
            float x = BitConverter.ToSingle(bytes, o), y = BitConverter.ToSingle(bytes, o + 4), flux = BitConverter.ToSingle(bytes, o + 8);
            stars.Add(new DetectedStar(x - width / 2.0, y - height / 2.0, flux));
        }
        stars.Sort((a, b) => b.Flux.CompareTo(a.Flux));
        if (stars.Count > maxStars) stars.RemoveRange(maxStars, stars.Count - maxStars);
        return stars;
    }

    private void EnsureTiles(int count)
    {
        if (_tiles != null && count <= _tilesCapacity) return;
        _tiles?.Dispose();
        _tilesCapacity = Math.Max(count, 64);
        _tiles = _device.CreateBuffer(new GPUBufferDescriptor { Size = (ulong)(_tilesCapacity * 8), Usage = GPUBufferUsage.Storage });
    }

    public void Dispose()
    {
        _statsPipeline?.Dispose(); _detectPipeline?.Dispose();
        _params?.Dispose(); _tiles?.Dispose(); _output?.Dispose(); _staging?.Dispose();
        _statsPipeline = _detectPipeline = null;
        _params = _tiles = _output = _staging = null;
    }

    private const string Common = @"
struct Params {
    width: u32, height: u32, tilesX: u32, tilesY: u32,
    sigma: f32, minHeight: f32, neighbourFraction: f32, maxStars: u32,
};
@group(0) @binding(0) var<uniform> P: Params;
@group(0) @binding(1) var frame: texture_2d<f32>;

fn luma(x: i32, y: i32) -> f32 {
    let cx = clamp(x, 0, i32(P.width) - 1);
    let cy = clamp(y, 0, i32(P.height) - 1);
    let c = textureLoad(frame, vec2<i32>(cx, cy), 0);
    return dot(c.rgb, vec3<f32>(0.299, 0.587, 0.114));
}
";

    // One workgroup per 32 x 32 tile: mean and standard deviation of the luma with the stars clipped out (three rounds of 3-sigma clipping).
    private const string StatsShader = Common + @"
@group(0) @binding(2) var<storage, read_write> tiles: array<vec2<f32>>;
var<workgroup> sumA: array<f32, 256>;
var<workgroup> sumB: array<f32, 256>;
var<workgroup> sumC: array<f32, 256>;

@compute @workgroup_size(16, 16)
fn main(@builtin(workgroup_id) wg: vec3<u32>, @builtin(local_invocation_index) li: u32, @builtin(local_invocation_id) lid: vec3<u32>) {
    let x0 = i32(wg.x) * 32 + i32(lid.x) * 2;
    let y0 = i32(wg.y) * 32 + i32(lid.y) * 2;
    var mean = 0.0;
    var sd = 1.0;
    for (var round = 0; round < 4; round = round + 1) {
        var s = 0.0; var s2 = 0.0; var n = 0.0;
        for (var dy = 0; dy < 2; dy = dy + 1) {
            for (var dx = 0; dx < 2; dx = dx + 1) {
                let px = x0 + dx; let py = y0 + dy;
                if (px < i32(P.width) && py < i32(P.height)) {
                    let v = luma(px, py);
                    if (round == 0 || abs(v - mean) <= 3.0 * sd) { s = s + v; s2 = s2 + v * v; n = n + 1.0; }
                }
            }
        }
        sumA[li] = s; sumB[li] = s2; sumC[li] = n;
        workgroupBarrier();
        for (var stride = 128u; stride > 0u; stride = stride >> 1u) {
            if (li < stride) { sumA[li] = sumA[li] + sumA[li + stride]; sumB[li] = sumB[li] + sumB[li + stride]; sumC[li] = sumC[li] + sumC[li + stride]; }
            workgroupBarrier();
        }
        let total = max(sumC[0], 1.0);
        mean = sumA[0] / total;
        sd = sqrt(max(sumB[0] / total - mean * mean, 1e-8));
        workgroupBarrier();
    }
    if (li == 0u) { tiles[wg.y * P.tilesX + wg.x] = vec2<f32>(mean, sd); }
}
";

    // One thread per pixel: is it the peak of a star? then its centre of light.
    private const string DetectShader = Common + @"
@group(0) @binding(2) var<storage, read_write> tiles: array<vec2<f32>>;
struct Output { count: atomic<u32>, pad0: u32, pad1: u32, pad2: u32, stars: array<vec4<f32>>, };
@group(0) @binding(3) var<storage, read_write> outBuf: Output;

fn tile(tx: i32, ty: i32) -> vec2<f32> {
    let cx = clamp(tx, 0, i32(P.tilesX) - 1);
    let cy = clamp(ty, 0, i32(P.tilesY) - 1);
    return tiles[u32(cy) * P.tilesX + u32(cx)];
}

// background and noise at a pixel, interpolated between the centres of the four nearest tiles
fn background(x: i32, y: i32) -> vec2<f32> {
    let fx = (f32(x) + 0.5) / 32.0 - 0.5;
    let fy = (f32(y) + 0.5) / 32.0 - 0.5;
    let ix = i32(floor(fx)); let iy = i32(floor(fy));
    let ax = fx - floor(fx); let ay = fy - floor(fy);
    let a = tile(ix, iy); let b = tile(ix + 1, iy); let c = tile(ix, iy + 1); let d = tile(ix + 1, iy + 1);
    return mix(mix(a, b, ax), mix(c, d, ax), ay);
}

@compute @workgroup_size(8, 8)
fn main(@builtin(global_invocation_id) gid: vec3<u32>) {
    let x = i32(gid.x); let y = i32(gid.y);
    if (x < 4 || y < 4 || x >= i32(P.width) - 4 || y >= i32(P.height) - 4) { return; }
    let v = luma(x, y);
    let bs = background(x, y);
    let bg = bs.x; let sd = max(bs.y, 0.002);
    let height = v - bg;
    if (height < max(P.sigma * sd, P.minHeight)) { return; }

    // the brightest in its 5 x 5 neighbourhood (ties go to the first in raster order)
    for (var dy = -2; dy <= 2; dy = dy + 1) {
        for (var dx = -2; dx <= 2; dx = dx + 1) {
            if (dx == 0 && dy == 0) { continue; }
            let w = luma(x + dx, y + dy);
            if (w > v) { return; }
            if (w == v && (dy < 0 || (dy == 0 && dx < 0))) { return; }
        }
    }

    // a star is a blur: its four neighbours are lit too (a hot pixel is a spike)
    let nb = 0.25 * (luma(x - 1, y) + luma(x + 1, y) + luma(x, y - 1) + luma(x, y + 1)) - bg;
    if (nb < P.neighbourFraction * height) { return; }

    // centre of light over 7 x 7, weights above the noise
    var sw = 0.0; var sx = 0.0; var sy = 0.0; var flux = 0.0;
    for (var dy = -3; dy <= 3; dy = dy + 1) {
        for (var dx = -3; dx <= 3; dx = dx + 1) {
            let l = luma(x + dx, y + dy) - bg;
            flux = flux + max(l, 0.0);
            let w = max(l - sd, 0.0);
            sw = sw + w; sx = sx + w * f32(dx); sy = sy + w * f32(dy);
        }
    }
    if (sw <= 0.0) { return; }
    let idx = atomicAdd(&outBuf.count, 1u);
    if (idx < P.maxStars) {
        outBuf.stars[idx] = vec4<f32>(f32(x) + sx / sw + 0.5, f32(y) + sy / sw + 0.5, flux, height);
    }
}
";
}
