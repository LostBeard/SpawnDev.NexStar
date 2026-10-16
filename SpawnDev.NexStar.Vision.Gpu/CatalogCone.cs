using System.Numerics;
using SpawnDev.NexStar.Vision;
using SpawnDev.SpawnJS.JSObjects;

namespace SpawnDev.NexStar.Vision.Gpu;

/// <summary>
/// The stars of the catalog within a cone of the sky, picked out on the GPU from the buffer the sky chart already holds (so the 118,000 stars never
/// come into .NET): the plate solver gets only the few hundred stars near where the telescope points.
/// </summary>
public sealed class CatalogCone : IDisposable
{
    public const int MaxResults = 4096;

    private readonly GPUDevice _device;
    private readonly GPUQueue _queue;
    private GPUComputePipeline? _pipeline;
    private GPUBuffer? _params, _output, _staging;

    public CatalogCone(GPUDevice device, GPUQueue queue)
    {
        _device = device;
        _queue = queue;
        using var module = device.CreateShaderModule(new GPUShaderModuleDescriptor { Code = Shader });
        _pipeline = device.CreateComputePipeline(new GPUComputePipelineDescriptor { Layout = "auto", Compute = new GPUProgrammableStage { Module = module, EntryPoint = "main" } });
        _params = device.CreateBuffer(new GPUBufferDescriptor { Size = 48, Usage = GPUBufferUsage.Uniform | GPUBufferUsage.CopyDst });
        long bytes = 16 + MaxResults * 32;
        _output = device.CreateBuffer(new GPUBufferDescriptor { Size = (ulong)bytes, Usage = GPUBufferUsage.Storage | GPUBufferUsage.CopyDst | GPUBufferUsage.CopySrc });
        _staging = device.CreateBuffer(new GPUBufferDescriptor { Size = (ulong)bytes, Usage = GPUBufferUsage.MapRead | GPUBufferUsage.CopyDst });
    }

    /// <summary>
    /// The catalog stars within <paramref name="radiusDegrees"/> of <paramref name="center"/> (a J2000 unit vector) and no fainter than
    /// <paramref name="magnitudeLimit"/>, from a stars buffer in the app's format (a 32-byte header record, then 32-byte star records). If more than
    /// <see cref="MaxResults"/> qualify, which of them come back is arbitrary, so ask for a fainter limit only where the cone is not crowded.
    /// </summary>
    public async Task<List<CatalogEntry>> SearchAsync(GPUBuffer stars, int starCount, Vector3 center, double radiusDegrees, float magnitudeLimit)
    {
        var c = Vector3.Normalize(center);
        var p = new byte[48];
        BitConverter.GetBytes(c.X).CopyTo(p, 0);
        BitConverter.GetBytes(c.Y).CopyTo(p, 4);
        BitConverter.GetBytes(c.Z).CopyTo(p, 8);
        BitConverter.GetBytes((float)Math.Cos(radiusDegrees * Math.PI / 180.0)).CopyTo(p, 12);
        BitConverter.GetBytes(magnitudeLimit).CopyTo(p, 16);
        BitConverter.GetBytes((uint)starCount).CopyTo(p, 20);
        BitConverter.GetBytes((uint)MaxResults).CopyTo(p, 24);
        _queue.WriteBuffer(_params!, 0, p);
        _queue.WriteBuffer(_output!, 0, new byte[16]);

        using var layout = _pipeline!.GetBindGroupLayout(0);
        using var group = _device.CreateBindGroup(new GPUBindGroupDescriptor
        {
            Layout = layout,
            Entries = new[]
            {
                new GPUBindGroupEntry { Binding = 0, Resource = new GPUBufferBinding { Buffer = _params! } },
                new GPUBindGroupEntry { Binding = 1, Resource = new GPUBufferBinding { Buffer = stars } },
                new GPUBindGroupEntry { Binding = 2, Resource = new GPUBufferBinding { Buffer = _output! } },
            },
        });
        using (var encoder = _device.CreateCommandEncoder())
        {
            using (var pass = encoder.BeginComputePass())
            {
                pass.SetPipeline(_pipeline);
                pass.SetBindGroup(0, group);
                pass.DispatchWorkgroups((uint)((starCount + 63) / 64));
                pass.End();
            }
            encoder.CopyBufferToBuffer(_output!, 0, _staging!, 0, (ulong)(16 + MaxResults * 32));
            using var commands = encoder.Finish();
            _queue.Submit(new[] { commands });
        }
        await _staging!.MapAsync(GPUMapMode.Read);
        byte[] bytes;
        using (var mapped = _staging.GetMappedRange()) bytes = mapped.ReadBytes();
        _staging.Unmap();

        int count = (int)Math.Min(BitConverter.ToUInt32(bytes, 0), MaxResults);
        var list = new List<CatalogEntry>(count);
        for (int i = 0; i < count; i++)
        {
            int o = 16 + i * 32;
            list.Add(new CatalogEntry(new Vector3(BitConverter.ToSingle(bytes, o), BitConverter.ToSingle(bytes, o + 4), BitConverter.ToSingle(bytes, o + 8)), BitConverter.ToSingle(bytes, o + 12), BitConverter.ToUInt32(bytes, o + 28)));
        }
        list.Sort((a, b) => a.Magnitude.CompareTo(b.Magnitude));
        return list;
    }

    public void Dispose()
    {
        _pipeline?.Dispose(); _params?.Dispose(); _output?.Dispose(); _staging?.Dispose();
        _pipeline = null; _params = _output = _staging = null;
    }

    private const string Shader = @"
struct Params { center: vec3<f32>, cosRadius: f32, magLimit: f32, count: u32, maxResults: u32, pad: u32 };
struct Star { pos: vec3<f32>, mag: f32, bv: f32, pmRa: f32, pmDec: f32, hip: u32 };
struct Output { count: atomic<u32>, pad0: u32, pad1: u32, pad2: u32, stars: array<Star>, };
@group(0) @binding(0) var<uniform> P: Params;
@group(0) @binding(1) var<storage, read> allStars: array<Star>;
@group(0) @binding(2) var<storage, read_write> outBuf: Output;

@compute @workgroup_size(64)
fn main(@builtin(global_invocation_id) gid: vec3<u32>) {
    if (gid.x >= P.count) { return; }
    let s = allStars[gid.x + 1u];   // record 0 is the file header
    if (s.mag > P.magLimit) { return; }
    if (dot(s.pos, P.center) < P.cosRadius) { return; }
    let idx = atomicAdd(&outBuf.count, 1u);
    if (idx < P.maxResults) { outBuf.stars[idx] = s; }
}
";
}
