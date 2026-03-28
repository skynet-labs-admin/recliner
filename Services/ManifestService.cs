using System.IO;
using System.Text.RegularExpressions;

namespace Recliner.Services;

public static class ManifestService
{
    // ── Public API ────────────────────────────────────────────────────────────

    public static List<ComfyInstance> LoadInstances(AppSettings settings)
    {
        var instances = new List<ComfyInstance>();
        var dirs = WslService.ListSubdirectories(settings.WslParentPath, settings.WslDistro);
        foreach (var dirName in dirs)
        {
            string wslPath = $"{settings.WslParentPath.TrimEnd('/')}/{dirName}";
            string winPath = WslService.ToWindowsPath(wslPath, settings.WslDistro);
            instances.Add(ScanInstance(dirName, wslPath, winPath, settings));
            // Silently remove pip temp artifacts (~* dirs in site-packages)
            // Fire-and-forget — safe, idempotent, no user action required
            WslService.RunSilent(
                $"find \"{wslPath}/venv/lib\" -maxdepth 3 -type d -name '~*' " +
                $"-exec rm -rf {{}} + 2>/dev/null; true",
                settings.WslDistro);
        }
        return instances;
    }

    public static ComfyInstance ScanInstance(
        string dirName, string wslPath, string winPath, AppSettings settings)
    {
        var inst = new ComfyInstance
        {
            DirectoryName = dirName,
            WslPath       = wslPath,
            WindowsPath   = winPath,
        };

        // Versions — read directly from venv and source files
        inst.ComfyUIVersion     = ReadComfyVersion(winPath)          ?? "—";
        string? torch           = ReadPackageVersion(winPath, "torch");
        inst.PyTorchVersion     = torch != null ? StripBuildTag(torch) : "—";
        inst.CudaBuild          = torch != null ? ExtractCudaTag(torch) : "—";
        inst.TorchVisionVersion = StripBuildTag(ReadPackageVersion(winPath, "torchvision") ?? "—");
        inst.TorchAudioVersion  = StripBuildTag(ReadPackageVersion(winPath, "torchaudio")  ?? "—");

        // Port and extra args — stored in user.yaml (ComfyUI's own config file)
        inst.Port      = ReadPortFromUserYaml(winPath);
        inst.ExtraArgs = ReadExtraArgsFromUserYaml(winPath);

        // Output folder
        inst.OutputFolder = !string.IsNullOrEmpty(settings.SharedOutputPath)
            ? $"{settings.SharedOutputPath.TrimEnd('/')}/{dirName}"
            : $"{wslPath}/output";
        inst.OutputFileCount = WslService.CountOutputFiles(inst.OutputFolder, settings.WslDistro);

        // Custom nodes — scan directory directly
        string customNodesWin = Path.Combine(winPath, "custom_nodes");
        if (Directory.Exists(customNodesWin))
        {
            foreach (var nodeDir in Directory.GetDirectories(customNodesWin))
            {
                string nodeName = Path.GetFileName(nodeDir);
                if (nodeName.StartsWith('.') || nodeName == "__pycache__") continue;
                inst.CustomNodes.Add(new CustomNode
                {
                    Name    = nodeName,
                    Version = ReadNodeVersion(nodeDir) ?? "unknown",
                    Enabled = !File.Exists(Path.Combine(nodeDir, ".disabled"))
                });
            }
        }

        return inst;
    }

    // ── Port persistence (user.yaml) ──────────────────────────────────────────

    public static int ReadPortFromUserYaml(string winPath)
    {
        string yamlPath = Path.Combine(winPath, "user.yaml");
        try
        {
            if (!File.Exists(yamlPath)) return 8188;
            string content = File.ReadAllText(yamlPath);
            var m = Regex.Match(content, @"^port\s*:\s*(\d+)", RegexOptions.Multiline);
            return m.Success && int.TryParse(m.Groups[1].Value, out int p) ? p : 8188;
        }
        catch { return 8188; }
    }

    // ── ExtraArgs persistence (user.yaml recliner_extra_args field) ───────────

    public static string ReadExtraArgsFromUserYaml(string winPath)
    {
        string yamlPath = Path.Combine(winPath, "user.yaml");
        try
        {
            if (!File.Exists(yamlPath)) return "";
            string content = File.ReadAllText(yamlPath);
            var m = Regex.Match(content,
                @"^recliner_extra_args\s*:\s*(.+)$", RegexOptions.Multiline);
            return m.Success ? m.Groups[1].Value.Trim().Trim('"', '\'') : "";
        }
        catch { return ""; }
    }

    public static void WriteExtraArgs(string winPath, string args)
    {
        string yamlPath = Path.Combine(winPath, "user.yaml");
        try
        {
            string content = File.Exists(yamlPath) ? File.ReadAllText(yamlPath) : "";
            var rx = new Regex(@"^recliner_extra_args\s*:.*$", RegexOptions.Multiline);
            string newLine = $"recliner_extra_args: {args}";
            content = rx.IsMatch(content)
                ? rx.Replace(content, newLine)
                : content.TrimEnd() + "\n" + newLine + "\n";
            File.WriteAllText(yamlPath, content);
        }
        catch { }
    }

    // ── Version readers ───────────────────────────────────────────────────────

    private static string? ReadComfyVersion(string winPath)
    {
        string[] candidates =
        [
            Path.Combine(winPath, "comfyui", "version.py"),
            Path.Combine(winPath, "comfyui", "__init__.py"),
            Path.Combine(winPath, "comfyui_version.py"),
            Path.Combine(winPath, "__init__.py"),
            Path.Combine(winPath, "version.txt"),
        ];
        foreach (var c in candidates)
        {
            try
            {
                if (!File.Exists(c)) continue;
                string text = File.ReadAllText(c);
                if (c.EndsWith("version.txt")) return text.Trim();
                var m = Regex.Match(text,
                    @"__version__\s*=\s*[^\d]*(\d+\.\d[\d.]*)");
                if (m.Success) return m.Groups[1].Value;
            }
            catch { }
        }
        return null;
    }

    private static string? ReadPackageVersion(string winPath, string packageName)
    {
        // Check both venv and .venv — either can be used depending on how the instance was set up
        string[] venvCandidates =
        [
            Path.Combine(winPath, "venv",  "lib"),
            Path.Combine(winPath, ".venv", "lib"),
        ];
        foreach (var libPath in venvCandidates)
        {
            if (!Directory.Exists(libPath)) continue;
            try
            {
                foreach (var pyDir in Directory.GetDirectories(libPath, "python*"))
                {
                    string versionFile = Path.Combine(
                        pyDir, "site-packages", packageName, "version.py");
                    if (!File.Exists(versionFile)) continue;
                    string text = File.ReadAllText(versionFile);
                    var m = Regex.Match(text,
                        @"__version__\s*=\s*[^\d]*(\d+\.\d[\d.+a-zA-Z]*)");
                    if (m.Success) return m.Groups[1].Value;
                }
            }
            catch { }
        }
        return null;
    }

    private static string? ReadNodeVersion(string nodeWinPath)
    {
        string[] candidates =
        [
            Path.Combine(nodeWinPath, "pyproject.toml"),
            Path.Combine(nodeWinPath, "setup.cfg"),
            Path.Combine(nodeWinPath, "version.txt"),
            Path.Combine(nodeWinPath, "__init__.py"),
        ];
        foreach (var c in candidates)
        {
            try
            {
                if (!File.Exists(c)) continue;
                string text = File.ReadAllText(c);
                var m = Regex.Match(text,
                    @"version\s*[=:]\s*[^\d]*(\d+\.\d[\w.]*)");
                if (m.Success) return m.Groups[1].Value;
            }
            catch { }
        }
        return null;
    }

    private static string StripBuildTag(string version)
    {
        int plus = version.IndexOf('+');
        return plus > 0 ? version[..plus] : version;
    }

    private static string ExtractCudaTag(string version)
    {
        var m = Regex.Match(version, @"\+(cu\d+)");
        return m.Success ? m.Groups[1].Value : "—";
    }

    // Keep these public for backward compatibility with WslService.WritePortConfig
    public static string? ReadComfyVersionFromSource(string winRootPath)
        => ReadComfyVersion(winRootPath);

    public static string? ReadTorchVersionFromSource(string winRootPath)
        => ReadPackageVersion(winRootPath, "torch");
}
