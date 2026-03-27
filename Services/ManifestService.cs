using System.IO;
using Newtonsoft.Json;

namespace Recliner.Services;

public static class ManifestService
{
    private const string ManifestFileName = "manifest.json";

    /// <summary>
    /// Load all ComfyUI instances from the parent WSL path.
    /// For each subdirectory, attempts to read manifest.json.
    /// Falls back to directory-scan data if no manifest exists.
    /// </summary>
    public static List<ComfyInstance> LoadInstances(AppSettings settings)
    {
        var instances = new List<ComfyInstance>();
        var dirs = WslService.ListSubdirectories(settings.WslParentPath, settings.WslDistro);

        foreach (var dirName in dirs)
        {
            string wslDirPath = $"{settings.WslParentPath.TrimEnd('/')}/{dirName}";
            string winPath = WslService.ToWindowsPath(wslDirPath, settings.WslDistro);

            var instance = TryLoadManifest(wslDirPath, winPath, settings.WslDistro)
                        ?? ScanDirectory(wslDirPath, winPath, settings.WslDistro);

            instance.DirectoryName = dirName;
            instance.WslPath = wslDirPath;
            instance.WindowsPath = winPath;

            instances.Add(instance);
        }

        return instances;
    }

    /// <summary>
    /// Updates only the port field in an existing manifest.json on disk.
    /// Used for the inline port editor — avoids a full re-scan.
    /// </summary>
    public static void PatchPort(string windowsInstancePath, int port)
    {
        string manifestPath = Path.Combine(windowsInstancePath, ManifestFileName);
        try
        {
            if (!File.Exists(manifestPath)) return;
            var obj = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(manifestPath));
            obj["port"] = port;
            File.WriteAllText(manifestPath,
                obj.ToString(Newtonsoft.Json.Formatting.Indented));
        }
        catch { }
    }

    /// <summary>
    /// Updates only the extra_args field in an existing manifest.json on disk.
    /// </summary>
    public static void PatchExtraArgs(string windowsInstancePath, string args)
    {
        string manifestPath = Path.Combine(windowsInstancePath, ManifestFileName);
        try
        {
            if (!File.Exists(manifestPath)) return;
            var obj = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(manifestPath));
            obj["extra_args"] = args;
            File.WriteAllText(manifestPath,
                obj.ToString(Newtonsoft.Json.Formatting.Indented));
        }
        catch { }
    }

    /// <summary>
    /// Reload a single instance from disk — used by the live watcher when
    /// manifest.json changes. Public so MainWindow can call it directly.
    /// </summary>
    public static ComfyInstance ReloadSingle(
        string dirName, string wslPath, string winPath, string distro)
    {
        var inst = TryLoadManifest(wslPath, winPath, distro)
                ?? ScanDirectory(wslPath, winPath, distro);
        inst.DirectoryName = dirName;
        inst.WslPath       = wslPath;
        inst.WindowsPath   = winPath;
        return inst;
    }

    /// <summary>
    /// Load a single instance from its manifest.json if present.
    /// </summary>
    private static ComfyInstance? TryLoadManifest(string wslDirPath, string winPath, string distro)
    {
        string manifestWin = Path.Combine(winPath, ManifestFileName);
        try
        {
            if (!File.Exists(manifestWin)) return null;
            string json = File.ReadAllText(manifestWin);
            var inst = JsonConvert.DeserializeObject<ComfyInstance>(json);
            if (inst == null) return null;
            inst.HasManifest = true;

            // If output_file_count was -1 in manifest, try to count live
            if (inst.OutputFileCount < 0 && !string.IsNullOrEmpty(inst.OutputFolder))
                inst.OutputFileCount = WslService.CountOutputFiles(inst.OutputFolder, distro);

            return inst;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Minimal data gathered by scanning the ComfyUI directory without a manifest.
    /// </summary>
    private static ComfyInstance ScanDirectory(string wslDirPath, string winPath, string distro)
    {
        var inst = new ComfyInstance { HasManifest = false };

        // Try to read ComfyUI version from comfyui/__init__.py
        inst.ComfyUIVersion = TryReadComfyVersion(winPath) ?? "unknown";

        // Enumerate custom_nodes subdirectories
        string customNodesWin = Path.Combine(winPath, "custom_nodes");
        if (Directory.Exists(customNodesWin))
        {
            foreach (var nodeDir in Directory.GetDirectories(customNodesWin))
            {
                string nodeName = Path.GetFileName(nodeDir);
                if (nodeName.StartsWith('.') || nodeName == "__pycache__") continue;

                var node = new CustomNode
                {
                    Name = nodeName,
                    Version = TryReadNodeVersion(nodeDir) ?? "unknown",
                    Enabled = !File.Exists(Path.Combine(nodeDir, ".disabled"))
                };
                inst.CustomNodes.Add(node);
            }
        }

        // Try to find output folder
        string defaultOutput = Path.Combine(winPath, "output");
        if (Directory.Exists(defaultOutput))
        {
            inst.OutputFolder = $"{wslDirPath}/output";
            inst.OutputFileCount = WslService.CountOutputFiles(inst.OutputFolder, distro);
        }

        return inst;
    }

    /// <summary>
    /// Reads the ComfyUI version directly from the source files on disk —
    /// the ground truth against which the manifest is validated.
    /// Public so MainWindow can compare without loading a full instance.
    /// </summary>
    public static string? ReadComfyVersionFromSource(string winRootPath)
        => TryReadComfyVersion(winRootPath);

    /// <summary>
    /// Reads the PyTorch version directly from the venv's installed package
    /// metadata — torch/version.py inside site-packages.
    /// Returns null if the venv doesn't exist or torch isn't installed.
    /// </summary>
    public static string? ReadTorchVersionFromSource(string winRootPath)
    {
        string libPath = Path.Combine(winRootPath, "venv", "lib");
        if (!Directory.Exists(libPath)) return null;
        try
        {
            foreach (var pyDir in Directory.GetDirectories(libPath, "python*"))
            {
                string versionFile = Path.Combine(
                    pyDir, "site-packages", "torch", "version.py");
                if (!File.Exists(versionFile)) continue;
                string text = File.ReadAllText(versionFile);
                var m = System.Text.RegularExpressions.Regex.Match(
                    text, @"__version__\s*=\s*[^\d]*(\d+\.\d[\d.+a-zA-Z]*)");
                if (m.Success) return m.Groups[1].Value;
            }
        }
        catch { }
        return null;
    }

    private static string? TryReadComfyVersion(string winRootPath)
    {
        // Check comfyui/__init__.py for __version__
        string[] candidates =
        [
            Path.Combine(winRootPath, "comfyui", "version.py"),   // comfy-org/ComfyUI (current)
            Path.Combine(winRootPath, "comfyui", "__init__.py"),
            Path.Combine(winRootPath, "comfyui_version.py"),
            Path.Combine(winRootPath, "__init__.py"),
            Path.Combine(winRootPath, "version.txt"),
        ];

        foreach (var candidate in candidates)
        {
            try
            {
                if (!File.Exists(candidate)) continue;
                string content = File.ReadAllText(candidate);

                // Look for __version__ = "x.y.z"
                var match = System.Text.RegularExpressions.Regex.Match(
                    content, @"__version__\s*=\s*[^\d]*(\d+\.\d[\d.]*)");
                if (match.Success) return match.Groups[1].Value;

                // Plain version.txt
                if (candidate.EndsWith("version.txt")) return content.Trim();
            }
            catch { }
        }
        return null;
    }

    private static string? TryReadNodeVersion(string nodeWinPath)
    {
        // Try pyproject.toml, setup.cfg, version.txt, __init__.py
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
                var m = System.Text.RegularExpressions.Regex.Match(
                    text, @"version\s*[=:]\s*[^\d]*(\d+\.\d[\w.]*)");
                if (m.Success) return m.Groups[1].Value;
            }
            catch { }
        }
        return null;
    }
}
