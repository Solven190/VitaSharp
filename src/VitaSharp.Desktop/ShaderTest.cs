using VitaSharp.Core.Gxm.Shader;
using VitaSharp.Core.Package;
using VitaSharp.Renderer.Vulkan;

namespace VitaSharp.Desktop;

/// <summary>Finds every GXP program inside a title's files and checks it translates to valid SPIR-V.</summary>
static class ShaderTest
{
    public static int Run(string appDir, string[] args)
    {
        string outDir = Path.Combine(Program.DataRoot, "shadertest");
        Directory.CreateDirectory(outDir);
        bool dumpAll = args.Contains("--dump");
        bool perShader = args.Contains("--warnings"); // each program's warnings by name (which shaders read an unsupported bank, ...)
        // --lower-dynamic-indexing: translate as for Qualcomm's proprietary driver (GpuQuirk.DynamicIndexLowering)
        ShaderGenerator.LowerDynamicIndexing = args.Contains("--lower-dynamic-indexing");
        // --far-plane=discard|hardware: translate as for a device without shaderClipDistance (Mali, GpuQuirk.FarPlaneDiscard)
        ShaderGenerator.FarPlane = args.FirstOrDefault(a => a.StartsWith("--far-plane="))?["--far-plane=".Length..] switch
        {
            "discard" => ShaderGenerator.FarPlaneClip.Discard,
            "hardware" => ShaderGenerator.FarPlaneClip.Hardware,
            _ => ShaderGenerator.FarPlaneClip.ClipDistance,
        };
        byte[]? klic = null;
        var work = Path.Combine(appDir, "sce_sys", "package", "work.bin");
        if (File.Exists(work))
        {
            klic = PkgInstaller.ReadKlicensee(work);
        }

        using var compiler = new ShaderCompiler(null);
        int ok = 0, fail = 0, total = 0, pointSize = 0;
        var warnings = new Dictionary<string, int>();
        List<GxpProgram> programs = GxpScanner.ScanDirectory(appDir, klic, out GxpScanner.Stats scan,
            onInvalid: (file, offset, bad) => Console.WriteLine($"{Path.GetFileName(file)} +0x{offset:X}: not a valid GXP program, skipped: {bad}"));
        int invalid = scan.Invalid;
        foreach (GxpProgram prog in programs)
        {
            total++;
            if (prog.WritesPointSize)
            {
                pointSize++; // point sprites (their size follows the internal resolution)
            }

            string name = $"{prog.Hash:x16}.{(prog.IsVertex ? "vert" : "frag")}";
            GeneratedShader gen;
            try
            {
                gen = ShaderGenerator.Generate(prog, new ShaderHints(Enumerable.Repeat(0x0C000000u, 16).ToArray(), [], 0));
            }
            catch (Exception ex)
            {
                fail++;
                Console.WriteLine($"{name}: translate exception {ex.GetType().Name}: {ex.Message}");
                File.WriteAllBytes(Path.Combine(outDir, name + ".gxp"), prog.Data);
                continue;
            }
            foreach (var w in gen.Warnings)
            {
                warnings[w] = warnings.GetValueOrDefault(w) + 1;
            }

            if (perShader)
            {
                foreach (var w in gen.Warnings.Distinct())
                {
                    Console.WriteLine($"{name}: warning: {w}");
                }
            }

            var spv = compiler.Compile(gen.Glsl, gen.IsVertex, out var err);
            if (spv == null)
            {
                fail++;
                Console.WriteLine($"{name}: {err?.Split('\n').FirstOrDefault()}");
                File.WriteAllText(Path.Combine(outDir, name), gen.Glsl + "\n/*\n" + err + "\n*/\n");
                File.WriteAllBytes(Path.Combine(outDir, name + ".gxp"), prog.Data);
            }
            else
            {
                ok++;
                if (dumpAll)
                {
                    File.WriteAllText(Path.Combine(outDir, name), gen.Glsl);
                }
            }
        }
        Console.WriteLine($"GXP programs: {total}, compiled {ok}, failed {fail}" + (invalid > 0 ? $", {invalid} invalid GXP blocks skipped" : "") + $"; {pointSize} vertex programs write the point size");
        foreach (var (w, n) in warnings.OrderByDescending(kv => kv.Value).Take(25))
        {
            Console.WriteLine($"  {n,5}x {w}");
        }

        return fail == 0 ? 0 : 2;
    }
}
