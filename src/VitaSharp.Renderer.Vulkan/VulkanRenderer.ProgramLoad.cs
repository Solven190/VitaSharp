using Silk.NET.Vulkan;
using System.Collections.Concurrent;
using VitaSharp.Core.Gxm;
using VitaSharp.Core.Gxm.Shader;
using VitaSharp.Core.Util;

namespace VitaSharp.Renderer.Vulkan;

/// <summary>
/// "Compilar shaders ao carregar o programa" (<see cref="VulkanRendererOptions.CompileOnProgramLoad"/>): a Vita game
/// creates its vertex and fragment program objects (sceGxmShaderPatcherCreateVertexProgram / CreateFragmentProgram)
/// while it loads, long before their first draw, and those calls already fix most of what a translated shader and its
/// pipeline depend on: the vertex attribute formats and streams, the fragment program's output register format,
/// multisample mode and blend, and the vertex program it is made for. The shaders are queued to the shader workers
/// right then, with the rest predicted: textures by class (<see cref="ShaderHints.CanonicalTextureFormat"/>: every
/// four-unsigned-byte format, compressed ones included, translates alike), the colour surface of the last scene, and
/// for each vertex program object made from the named program the whole pipeline (triangles, filled, the predicted
/// scene). The first draw then finds its module and pipeline built instead of compiling them on the GPU thread.
/// <para>A draw whose exact formats differ from the prediction but translate to the same shader (same
/// <see cref="ShaderHints.CanonicalKey"/>) is served the predicted module (<see cref="TryProgramLoadModule"/>), so its
/// pipeline key, which names the modules, is the predicted one too. A wrong prediction only builds a module or a
/// pipeline nobody uses; the draw then compiles its own as before. This is the Dolphin/Ryujinx idea of compiling ahead
/// of use, at the earliest moment the console's API gives; it is not an ubershader (the shader is still the program's
/// own translation, so the cost of a program never seen at creation stays at its draw).</para>
/// </summary>
public sealed unsafe partial class VulkanRenderer
{
    private bool _compileOnProgramLoad;

    /// <summary>Vertex program objects by their program's hash (the latest <see cref="VertexProgramsPerProgram"/>): a
    /// fragment program names the program, the pipeline needs the object (attributes and streams).</summary>
    private readonly ConcurrentDictionary<ulong, GxmVertexProgram[]> _loadedVertexPrograms = new();
    private const int VertexProgramsPerProgram = 4;

    /// <summary>(program hash, <see cref="ShaderHints.CanonicalKey"/>) → the hint key a module was queued with at
    /// program load.</summary>
    private readonly ConcurrentDictionary<(ulong, ulong), ulong> _programLoadKeys = new();

    private int _statLoadShaders, _statLoadPipelines, _statLoadPipelinesBuilt, _statLoadAliases;

    /// <summary>Program-load jobs queued and modules/pipelines built (tests and the session line).</summary>
    public (int Shaders, int Pipelines, int PipelinesBuilt, int Aliases) ProgramLoadStats =>
        (Volatile.Read(ref _statLoadShaders), Volatile.Read(ref _statLoadPipelines), Volatile.Read(ref _statLoadPipelinesBuilt), Volatile.Read(ref _statLoadAliases));

    private void ConfigureProgramLoad(VulkanRendererOptions vulkanRendererOptions)
    {
        _compileOnProgramLoad = Environment.GetEnvironmentVariable(EnvVars.CompileOnProgramLoad)?.Trim().ToLowerInvariant() switch
        {
            "0" or "off" or "false" or "no" => false,
            "1" or "on" or "true" or "yes" => true,
            _ => vulkanRendererOptions.CompileOnProgramLoad,
        };
    }

    private bool ProgramLoadActive => _compileOnProgramLoad && !_stopShaderJobs && !_deviceLost;

    public void VertexProgramCreated(GxmVertexProgram vertexProgram)
    {
        if (!ProgramLoadActive || vertexProgram.Info == null)
        {
            return;
        }

        _loadedVertexPrograms.AddOrUpdate(vertexProgram.Info.Program.Hash, _ => [vertexProgram], (_, old) =>
        {
            if (Array.IndexOf(old, vertexProgram) >= 0)
            {
                return old;
            }

            int keep = Math.Min(old.Length, VertexProgramsPerProgram - 1);
            var list = new GxmVertexProgram[keep + 1];
            Array.Copy(old, old.Length - keep, list, 0, keep);
            list[keep] = vertexProgram;
            return list;
        });
        // without varying trimming the vertex shader does not depend on its fragment program (the usual case)
        if (!s_varyingTrim)
        {
            QueueProgramLoadShader(vertexProgram.Info.Program, PredictedVertexHints(vertexProgram, 0, 0));
        }
    }

    public void FragmentProgramCreated(GxmFragmentProgram fragmentProgram, GxmProgramInfo? vertexProgram)
    {
        if (!ProgramLoadActive || fragmentProgram.IsMaskUpdate || fragmentProgram.Info == null)
        {
            return;
        }

        uint colorFormat = _prefetchColorFormat;
        ShaderHints fragmentHints = PredictedFragmentHints(fragmentProgram, colorFormat, fetchInput: false);
        ulong fragmentKey = QueueProgramLoadShader(fragmentProgram.Info.Program, fragmentHints);
        if (vertexProgram == null || !_loadedVertexPrograms.TryGetValue(vertexProgram.Program.Hash, out GxmVertexProgram[]? vertexPrograms))
        {
            return;
        }

        PredictedScene scene = _prefetchScene.HasColor ? _prefetchScene : DefaultScene();
        bool multisampled = scene.Samples != SampleCountFlags.Count1Bit;
        ShaderHints? fetchHints = multisampled ? PredictedFragmentHints(fragmentProgram, colorFormat, fetchInput: true) : null;
        uint varyingMask = s_varyingTrim ? ShaderGenerator.FragmentVaryingMask(fragmentProgram.Info.Program) : 0;
        foreach (GxmVertexProgram vp in vertexPrograms)
        {
            ulong vertexKey = QueueProgramLoadShader(vp.Info.Program, PredictedVertexHints(vp, varyingMask, fragmentProgram.TexcoordRemap));
            // drivers with broken extended dynamic state bake the draw's depth/stencil state into the pipeline: not predictable
            if (StaticEds || !EnsureShaderWorkers())
            {
                continue;
            }

            var job = new PipelineJob(vp, vertexKey, fragmentProgram.Info.Program, fragmentKey, fetchHints?.Key() ?? 0, fetchHints, scene, fragmentProgram.Blend,
                FragEnabled: true, PrimitiveType: 0, PolygonMode: 0, Static: 0, ProgramLoad: true);
            Interlocked.Increment(ref _statLoadPipelines);
            _pipelineJobs.Enqueue(job);
            _workAvailable.Release();
        }
    }

    /// <summary>The scene a draw before any submitted scene is predicted to render into: one-sample U8U8U8U8 (the
    /// colour format 0, A8B8G8R8).</summary>
    private PredictedScene DefaultScene()
    {
        Format f = RenderableFormat(SurfaceFormat(0));
        return new PredictedScene(true, f, f, SampleCountFlags.Count1Bit);
    }

    private static uint[] DefaultTextureFormats()
    {
        uint[] formats = new uint[16];
        Array.Fill(formats, 0x0C000000u);
        return formats;
    }

    /// <summary>The hints of the program's draws as <see cref="VertexHints"/> builds them, with vertex textures (rare)
    /// predicted as four unsigned bytes.</summary>
    private static ShaderHints PredictedVertexHints(GxmVertexProgram vertexProgram, uint varyingMask, ulong texcoordRemap) =>
        new(DefaultTextureFormats(), vertexProgram.Attributes.Select(a => ((uint)a.RegIndex, (int)a.ComponentCount, AttributeFormatKey(a, vertexProgram.Streams))).ToArray(), 0,
            VaryingMask: varyingMask, ScaledPointSize: vertexProgram.Info.Program.WritesPointSize, TexcoordRemap: texcoordRemap);

    /// <summary>The hints of the program's draws as <see cref="FragmentHints"/> / <see cref="FragmentHintsInputFetch"/>
    /// build them, with the textures predicted as four unsigned bytes.</summary>
    private static ShaderHints PredictedFragmentHints(GxmFragmentProgram fragmentProgram, uint colorFormat, bool fetchInput) =>
        new(DefaultTextureFormats(), [], colorFormat, FetchInputAttachment: fetchInput, OutputRegisterFormat: NativeOutputFormat(fragmentProgram));

    /// <summary>Queues the module of <paramref name="hints"/> unless it exists or is queued, and remembers it under its
    /// canonical key. Returns its hint key.</summary>
    private ulong QueueProgramLoadShader(GxpProgram program, ShaderHints hints)
    {
        ulong hintKey = hints.Key();
        _programLoadKeys.TryAdd((program.Hash, hints.CanonicalKey()), hintKey);
        if (NeedsJob(program, hintKey))
        {
            Interlocked.Increment(ref _statLoadShaders);
            Queue(program, hintKey, hints);
        }

        return hintKey;
    }

    /// <summary>
    /// A draw's module that is not in the tables under its own key: the module queued at program load for hints that
    /// translate to the same shader (<see cref="ShaderHints.CanonicalKey"/>), published under the draw's key as well
    /// (waited for when a worker is still building it). False when there is none.
    /// </summary>
    private bool TryProgramLoadModule(GxpProgram program, ulong hintKey, ShaderHints hints, bool wait, out ModuleEntry? module)
    {
        module = null;
        if (_programLoadKeys.IsEmpty || !_programLoadKeys.TryGetValue((program.Hash, hints.CanonicalKey()), out ulong loadKey) || loadKey == hintKey)
        {
            return false;
        }

        if (!_modules.TryGetValue((program.Hash, loadKey), out ModuleEntry? loaded))
        {
            if (!_inflight.TryGetValue((program.Hash, loadKey), out ShaderJob? job))
            {
                return _modules.TryGetValue((program.Hash, loadKey), out loaded) && PublishAlias(program.Hash, hintKey, loaded, out module);
            }

            if (!wait)
            {
                return true; // the GPU thread waits for it (or runs it) at the draw
            }

            loaded = job.RunOrWait(this);
            Interlocked.Increment(ref _statAsyncWaited);
        }

        return PublishAlias(program.Hash, hintKey, loaded, out module);
    }

    /// <summary>Publishes <paramref name="loaded"/> under a second key (never destroyed twice: <see cref="DestroyPipelines"/>
    /// destroys each module once).</summary>
    private bool PublishAlias(ulong programHash, ulong hintKey, ModuleEntry loaded, out ModuleEntry? module)
    {
        module = _modules.GetOrAdd((programHash, hintKey), loaded);
        if (ReferenceEquals(module, loaded))
        {
            Interlocked.Increment(ref _statLoadAliases);
        }

        return true;
    }

    /// <summary>
    /// Waits until the shader workers have nothing queued or running for draws or program loads (the boot rebuild and the
    /// prewarm are not waited for): the GPU dump replay's stand-in for a game's loading screen, and tests. False when
    /// <paramref name="cancel"/> stopped the wait first.
    /// </summary>
    public bool WaitForBackgroundCompiles(CancellationToken cancel)
    {
        var spin = new SpinWait();
        while (!_shaderJobs.IsEmpty || !_pipelineJobs.IsEmpty || !_inflight.IsEmpty || !_pipelineInflight.IsEmpty || Volatile.Read(ref _pipelineJobsRunning) != 0)
        {
            if (cancel.IsCancellationRequested || _stopShaderJobs || _deviceLost)
            {
                return false;
            }

            spin.SpinOnce(sleep1Threshold: 20);
        }

        return true;
    }

    private string ProgramLoadReport() => _compileOnProgramLoad
        ? $"; at program load: {_statLoadShaders} shaders and {_statLoadPipelines} pipelines queued ({_statLoadPipelinesBuilt} built), {_statLoadAliases} draws served by a module of the same canonical hints"
        : "; at program load: off";
}
