using SpawnDev.SpawnJS.JSObjects;

namespace SpawnDev.NexStar.Sky;

/// <summary>Which layers of the sky to draw.</summary>
[Flags]
public enum SkyLayers
{
    None = 0,
    AltAzGrid = 1,
    EquatorialGrid = 2,
    GroundAndHorizon = 4,
    Stars = 8,
    ConstellationLines = 16,
    ConstellationBoundaries = 32,
    DeepSky = 64,
    Default = GroundAndHorizon | Stars | ConstellationLines | DeepSky,
}

/// <summary>
/// Draws the sky in one WebGPU render pass: the background (ground, horizon and grids, computed per pixel), constellation boundaries and figures, deep-sky
/// markers, then the stars (instanced point sprites, rotated and projected in the vertex shader). The catalogs live in GPU storage buffers and are never
/// read back. The HUD (GameUI) is drawn afterwards by another pass over the same target.
/// </summary>
public sealed class SkyRenderer : IDisposable
{
    private readonly GPUDevice _device;
    private readonly GPUQueue _queue;
    private readonly SkyData _data;
    private GPUBuffer? _viewBuffer;
    private GPUBuffer? _figureStyle, _boundaryStyle;
    private GPURenderPipeline? _backgroundPipeline, _starPipeline, _linePipeline, _deepSkyPipeline;
    private GPUBindGroup? _backgroundGroup, _starGroup, _figureGroup, _boundaryGroup, _deepSkyGroup;
    private readonly float[] _uniform = new float[32];
    private readonly float[] _styleData = new float[8];

    /// <summary>What to draw.</summary>
    public SkyLayers Layers { get; set; } = SkyLayers.Default;

    /// <summary>Scales every star's size (1 = normal).</summary>
    public float StarSize { get; set; } = 1.0f;

    /// <summary>Red-only drawing for dark adaptation.</summary>
    public bool NightMode { get; set; } = true;

    public SkyRenderer(GPUDevice device, GPUQueue queue, string canvasFormat, SkyData data)
    {
        _device = device;
        _queue = queue;
        _data = data;

        _viewBuffer = device.CreateBuffer(new GPUBufferDescriptor { Size = 128, Usage = GPUBufferUsage.Uniform | GPUBufferUsage.CopyDst });
        _figureStyle = device.CreateBuffer(new GPUBufferDescriptor { Size = 32, Usage = GPUBufferUsage.Uniform | GPUBufferUsage.CopyDst });
        _boundaryStyle = device.CreateBuffer(new GPUBufferDescriptor { Size = 32, Usage = GPUBufferUsage.Uniform | GPUBufferUsage.CopyDst });

        _backgroundPipeline = Pipeline(SkyShaders.Background, canvasFormat, additive: false);
        _starPipeline = Pipeline(SkyShaders.Stars, canvasFormat, additive: true);
        _linePipeline = Pipeline(SkyShaders.Lines, canvasFormat, additive: true);
        _deepSkyPipeline = Pipeline(SkyShaders.DeepSky, canvasFormat, additive: true);

        _backgroundGroup = Group(_backgroundPipeline, null, null);
        _starGroup = Group(_starPipeline, data.Stars, null);
        _figureGroup = Group(_linePipeline, data.ConstellationLines, _figureStyle);
        _boundaryGroup = Group(_linePipeline, data.ConstellationBoundaries, _boundaryStyle);
        _deepSkyGroup = Group(_deepSkyPipeline, data.DeepSkyBuffer, null);
    }

    private GPURenderPipeline Pipeline(string wgsl, string format, bool additive)
    {
        using var module = _device.CreateShaderModule(new GPUShaderModuleDescriptor { Code = wgsl });
        return _device.CreateRenderPipeline(new GPURenderPipelineDescriptor
        {
            Layout = "auto",
            Vertex = new GPUVertexState { Module = module, EntryPoint = "vs_main" },
            Fragment = new GPUFragmentState
            {
                Module = module,
                EntryPoint = "fs_main",
                Targets = new[]
                {
                    new GPUColorTargetState
                    {
                        Format = format,
                        Blend = additive
                            ? new GPUBlendState
                            {
                                Color = new GPUBlendComponent { SrcFactor = GPUBlendFactor.One, DstFactor = GPUBlendFactor.One, Operation = GPUBlendOperation.Add },
                                Alpha = new GPUBlendComponent { SrcFactor = GPUBlendFactor.One, DstFactor = GPUBlendFactor.One, Operation = GPUBlendOperation.Add },
                            }
                            : null,
                    }
                }
            },
            Primitive = new GPUPrimitiveState { Topology = GPUPrimitiveTopology.TriangleList },
        });
    }

    private GPUBindGroup Group(GPURenderPipeline pipeline, GPUBuffer? storage, GPUBuffer? style)
    {
        var entries = new List<GPUBindGroupEntry> { new() { Binding = 0, Resource = new GPUBufferBinding { Buffer = _viewBuffer! } } };
        if (storage != null) entries.Add(new GPUBindGroupEntry { Binding = 1, Resource = new GPUBufferBinding { Buffer = storage } });
        if (style != null) entries.Add(new GPUBindGroupEntry { Binding = 2, Resource = new GPUBufferBinding { Buffer = style } });
        using var layout = pipeline.GetBindGroupLayout(0);
        return _device.CreateBindGroup(new GPUBindGroupDescriptor { Layout = layout, Entries = entries.ToArray() });
    }

    /// <summary>
    /// Draws the sky into <paramref name="target"/>, clearing it first. <paramref name="pixelRatio"/> is the canvas pixels per CSS pixel the
    /// <paramref name="view"/> is measured in.
    /// </summary>
    public void Render(GPUCommandEncoder encoder, GPUTextureView target, SkyView view, double pixelRatio)
    {
        if (_viewBuffer == null) return;
        var ratio = (float)pixelRatio;
        var m = view.EquatorialToHorizontal;
        var (right, up, forward) = view.Basis;
        var u = _uniform;
        for (int row = 0; row < 3; row++)
        {
            u[row * 4 + 0] = (float)m[row * 3 + 0];
            u[row * 4 + 1] = (float)m[row * 3 + 1];
            u[row * 4 + 2] = (float)m[row * 3 + 2];
            u[row * 4 + 3] = 0;
        }
        u[12] = right.X; u[13] = right.Y; u[14] = right.Z; u[15] = 0;
        u[16] = up.X; u[17] = up.Y; u[18] = up.Z; u[19] = 0;
        u[20] = forward.X; u[21] = forward.Y; u[22] = forward.Z; u[23] = 0;
        u[24] = (float)(view.WidthPx * pixelRatio);
        u[25] = (float)(view.HeightPx * pixelRatio);
        u[26] = (float)(view.Scale * pixelRatio);
        u[27] = (float)view.MagnitudeLimit;
        u[28] = StarSize * ratio;
        u[29] = NightMode ? 1f : 0f;
        u[30] = (float)(int)Layers;
        u[31] = (float)Math.Clamp(7.5 + 1.6 * Math.Log2(120.0 / Math.Max(view.FovDegrees, SkyView.MinFov)), 7.0, 16.0);
        _queue.WriteBuffer(_viewBuffer, 0, ToBytes(u));

        // line styles: figures brighter than the boundaries
        _styleData[0] = 0.30f; _styleData[1] = 0.55f; _styleData[2] = 0.95f; _styleData[3] = 0.55f; _styleData[4] = 1.2f * ratio;
        _queue.WriteBuffer(_figureStyle!, 0, ToBytes(_styleData));
        _styleData[0] = 0.55f; _styleData[1] = 0.40f; _styleData[2] = 0.15f; _styleData[3] = 0.35f; _styleData[4] = 1.0f * ratio;
        _queue.WriteBuffer(_boundaryStyle!, 0, ToBytes(_styleData));

        using var pass = encoder.BeginRenderPass(new GPURenderPassDescriptor
        {
            ColorAttachments = new[]
            {
                new GPURenderPassColorAttachment
                {
                    View = target,
                    LoadOp = GPULoadOp.Clear,
                    StoreOp = GPUStoreOp.Store,
                    ClearValue = new GPUColorDict { R = 0, G = 0, B = 0, A = 1 },
                }
            }
        });

        pass.SetPipeline(_backgroundPipeline!);
        pass.SetBindGroup(0, _backgroundGroup);
        pass.Draw(3);

        if (Layers.HasFlag(SkyLayers.ConstellationBoundaries) && _data.ConstellationBoundaryCount > 0)
        {
            pass.SetPipeline(_linePipeline!);
            pass.SetBindGroup(0, _boundaryGroup);
            pass.Draw(6, (uint)_data.ConstellationBoundaryCount);
        }
        if (Layers.HasFlag(SkyLayers.ConstellationLines) && _data.ConstellationLineCount > 0)
        {
            pass.SetPipeline(_linePipeline!);
            pass.SetBindGroup(0, _figureGroup);
            pass.Draw(6, (uint)_data.ConstellationLineCount);
        }
        if (Layers.HasFlag(SkyLayers.DeepSky) && _data.DeepSkyCount > 0)
        {
            pass.SetPipeline(_deepSkyPipeline!);
            pass.SetBindGroup(0, _deepSkyGroup);
            pass.Draw(6, (uint)_data.DeepSkyCount);
        }
        if (Layers.HasFlag(SkyLayers.Stars) && _data.StarCount > 0)
        {
            pass.SetPipeline(_starPipeline!);
            pass.SetBindGroup(0, _starGroup);
            pass.Draw(6, (uint)_data.StarCount);
        }
        pass.End();
    }

    private static byte[] ToBytes(float[] values)
    {
        var bytes = new byte[values.Length * sizeof(float)];
        Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    public void Dispose()
    {
        _backgroundGroup?.Dispose(); _starGroup?.Dispose(); _figureGroup?.Dispose(); _boundaryGroup?.Dispose(); _deepSkyGroup?.Dispose();
        _backgroundPipeline?.Dispose(); _starPipeline?.Dispose(); _linePipeline?.Dispose(); _deepSkyPipeline?.Dispose();
        _viewBuffer?.Dispose(); _figureStyle?.Dispose(); _boundaryStyle?.Dispose();
        _backgroundGroup = _starGroup = _figureGroup = _boundaryGroup = _deepSkyGroup = null;
        _backgroundPipeline = _starPipeline = _linePipeline = _deepSkyPipeline = null;
        _viewBuffer = _figureStyle = _boundaryStyle = null;
    }
}
