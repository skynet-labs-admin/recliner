using System.Diagnostics;
using System.IO;
using System.Text;

namespace Recliner.Services;

public static class WslService
{
    /// <summary>
    /// Converts a WSL path like /home/user/... to a Windows UNC path \\wsl$\Distro\home\user\...
    /// </summary>
    public static string ToWindowsPath(string wslPath, string distro)
    {
        string normalized = wslPath.TrimStart('/').Replace('/', '\\');
        return $@"\\wsl$\{distro}\{normalized}";
    }

    /// <summary>
    /// Reverse of ToWindowsPath — converts a \\wsl$\Distro\... UNC path back to /home/...
    /// Also handles the newer \\wsl.localhost\Distro\... form.
    /// Returns the original string unchanged if it doesn't match either form.
    /// </summary>
    public static string ToWslPath(string windowsUncPath, string distro)
    {
        string[] prefixes =
        [
            $@"\\wsl$\{distro}\",
            $@"\\wsl.localhost\{distro}\",
        ];
        foreach (var prefix in prefixes)
        {
            if (windowsUncPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                string rest = windowsUncPath[prefix.Length..].Replace('\\', '/');
                return "/" + rest;
            }
        }
        return windowsUncPath; // not a WSL UNC path — return as-is
    }

    /// <summary>
    /// Lists subdirectory names under a WSL path via the \\wsl$ UNC share.
    /// Returns empty list if the path doesn't exist or WSL isn't available.
    /// </summary>
    public static List<string> ListSubdirectories(string wslPath, string distro)
    {
        string winPath = ToWindowsPath(wslPath, distro);
        try
        {
            if (!Directory.Exists(winPath)) return [];
            return [.. Directory.GetDirectories(winPath)
                        .Select(Path.GetFileName)
                        .Where(n => n != null && !n.StartsWith('.'))
                        .OrderBy(n => n)
                        .Select(n => n!)];
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// Reads a file from WSL via the \\wsl$ share. Returns null on failure.
    /// </summary>
    public static string? ReadFile(string wslFilePath, string distro)
    {
        string winPath = ToWindowsPath(wslFilePath, distro);
        try
        {
            return File.Exists(winPath) ? File.ReadAllText(winPath) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Returns the list of available WSL distros by running wsl.exe --list.
    /// </summary>
    public static List<string> GetDistros()
    {
        var result = new List<string>();
        try
        {
            var psi = new ProcessStartInfo("wsl.exe", "--list --quiet")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.Unicode
            };
            using var proc = Process.Start(psi);
            if (proc == null) return result;
            string output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(5000);
            foreach (var line in output.Split('\n'))
            {
                string name = line.Trim().TrimEnd('\r').Replace("\0", "");
                if (!string.IsNullOrWhiteSpace(name) && name != "(Default)")
                    result.Add(name);
            }
        }
        catch { /* WSL not available */ }
        return result;
    }

    /// <summary>
    /// Launch ComfyUI in a new terminal window.
    /// Delegates to the instance's own start.sh — written by Setup/Clone/New.
    /// RECLINER only passes --port (and optionally --output-directory); everything
    /// else — venv resolution, activation, entry point — lives in start.sh.
    /// </summary>
    public static Process? LaunchComfyUI(
        string wslPath, string distro, int port,
        string? launchCommand, string defaultCommand,
        string? outputWslPath = null,
        string? extraArgs = null,
        string? sharedModelsPath = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("#!/bin/bash");
        sb.AppendLine($"cd \"{wslPath}\"");

        // Write a fresh extra_model_paths.yaml before every launch so the shared
        // models path always reflects the current RECLINER Settings value.
        // ComfyUI auto-detects this file in its own directory — no flag needed.
        if (!string.IsNullOrWhiteSpace(sharedModelsPath))
        {
            string models = sharedModelsPath.TrimEnd('/');
            sb.AppendLine($"cat > \"{wslPath}/extra_model_paths.yaml\" << 'RECLINER_YAML_EOF'");
            sb.AppendLine("comfyui:");
            sb.AppendLine($"    base_path: {models}/");
            foreach (var dir in new[]{ "checkpoints","clip","clip_vision","configs",
                "controlnet","diffusion_models","embeddings","gligen","hypernetworks",
                "ipadapter","loras","photomaker","style_models","text_encoders",
                "unet","upscale_models","vae","vae_approx" })
                sb.AppendLine($"    {dir}: {dir}/");
            sb.AppendLine("RECLINER_YAML_EOF");
        }
        else
        {
            // No shared models configured — remove any stale yaml so ComfyUI
            // doesn't try to open a path that no longer exists.
            sb.AppendLine($"rm -f \"{wslPath}/extra_model_paths.yaml\"");
        }

        // start.sh written by RECLINER at setup time is the authoritative launcher.
        // Fall back to the stored / default command if it's somehow missing.
        string args = $"--port {port}";
        if (!string.IsNullOrEmpty(outputWslPath))
            args += $" --output-directory \"{outputWslPath}\"";
        if (!string.IsNullOrWhiteSpace(extraArgs))
            args += $" {extraArgs.Trim()}";

        sb.AppendLine($"if [ -f \"{wslPath}/start.sh\" ]; then");
        sb.AppendLine($"    bash \"{wslPath}/start.sh\" {args}");
        sb.AppendLine( "else");
        // Fallback: resolve venv python ourselves (same probe as manifest generator)
        sb.AppendLine($"    RECLINER_PY=\"{wslPath}/venv/bin/python\"");
        sb.AppendLine($"    [ -x \"$RECLINER_PY\" ] || RECLINER_PY=\"{wslPath}/.venv/bin/python\"");
        sb.AppendLine( "    [ -x \"$RECLINER_PY\" ] || RECLINER_PY=python3");
        sb.AppendLine( "    source venv/bin/activate 2>/dev/null || true");
        string fallbackCmd = launchCommand ?? defaultCommand;
        int sp = fallbackCmd.IndexOf(' ');
        string fallbackEntry = sp > 0 ? fallbackCmd[sp..].TrimStart() : "main.py";
        sb.AppendLine($"    \"$RECLINER_PY\" {fallbackEntry} {args}");
        sb.AppendLine( "fi");

        return RunInTerminal(distro, sb.ToString(), $"ComfyUI :{port}");
    }

    /// <summary>
    /// Write a bash script to a temp file and execute it in a WSL terminal window.
    /// The script path passed to the terminal is a plain path with no shell operators,
    /// so Windows Terminal / cmd.exe cannot misparse it.
    /// </summary>
    public static void RunScript(string scriptContent, string distro, string tabTitle = "RECLINER")
        => RunInTerminal(distro, scriptContent, tabTitle);

    /// <summary>
    /// Public overload — opens a terminal running an inline bash command string.
    /// Used by the Open Terminal button to drop into an instance venv shell.
    /// </summary>
    public static void RunInTerminalPublic(string distro, string bashCommand, string title)
        => RunInTerminal(distro, $"#!/bin/bash\n{bashCommand}", title);

    /// <summary>
    /// Converts a Windows absolute path to the WSL /mnt/ mount path.
    /// e.g. C:\Users\foo\bar.txt  →  /mnt/c/Users/foo/bar.txt
    /// </summary>
    public static string WinPathToWslMount(string winPath)
    {
        string drive = char.ToLower(winPath[0]).ToString();
        string rest  = winPath[2..].Replace('\\', '/');
        return $"/mnt/{drive}{rest}";
    }

    /// <summary>
    /// Count files in an output folder. Returns -1 on error.
    /// </summary>
    public static int CountOutputFiles(string wslOutputPath, string distro)
    {
        string winPath = ToWindowsPath(wslOutputPath, distro);
        try
        {
            if (!Directory.Exists(winPath)) return -1;
            return Directory.GetFiles(winPath, "*", SearchOption.AllDirectories).Length;
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>
    /// Writes (or updates) the port setting in user.yaml inside the instance
    /// directory so ComfyUI picks it up on next launch.
    /// </summary>
    public static void WritePortConfig(string windowsInstancePath, int port)
    {
        string yamlPath = Path.Combine(windowsInstancePath, "user.yaml");
        try
        {
            string content = File.Exists(yamlPath) ? File.ReadAllText(yamlPath) : "";
            var rx = new System.Text.RegularExpressions.Regex(
                @"^port\s*:\s*\d+",
                System.Text.RegularExpressions.RegexOptions.Multiline);
            content = rx.IsMatch(content)
                ? rx.Replace(content, $"port: {port}")
                : $"port: {port}\n" + content;
            File.WriteAllText(yamlPath, content);
        }
        catch { /* inaccessible — silently skip */ }
    }

    /// <summary>
    /// Returns true if a ComfyUI process is running on the given port in WSL.
    /// Uses pgrep to search for main.py with the matching --port argument.
    /// </summary>
    public static bool IsComfyRunning(string distro, int port)
    {
        try
        {
            var psi = new ProcessStartInfo(
                "wsl.exe", $"-d {distro} -- pgrep -f \"main.py.*--port {port}\"")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return false;
            proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(3000);
            return proc.ExitCode == 0; // pgrep exits 0 when a match is found
        }
        catch { return false; }
    }

    /// <summary>
    /// Sends SIGTERM to any ComfyUI process running on the given port in WSL.
    /// pkill matches on the same pattern as IsComfyRunning.
    /// </summary>
    public static void StopComfyUI(string distro, int port)
    {
        try
        {
            var psi = new ProcessStartInfo(
                "wsl.exe", $"-d {distro} -- pkill -f \"main.py.*--port {port}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit(5000);
        }
        catch { }
    }

    /// <summary>
    /// Runs a bash script in WSL silently (no terminal window).
    /// Fire-and-forget — used for background operations like manifest regeneration.
    /// The DispatcherTimer in MainWindow will detect any file changes within 2 s.
    /// </summary>
    public static void RunSilent(string scriptContent, string distro)
    {
        string tempWin = Path.Combine(
            Path.GetTempPath(), $"recliner_{Guid.NewGuid():N}.sh");
        File.WriteAllText(tempWin,
            scriptContent.Replace("\r\n", "\n"),
            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        string wslScript = WinPathToWslMount(tempWin);
        var psi = new ProcessStartInfo("wsl.exe",
            $"-d {distro} -- bash \"{wslScript}\"")
        {
            UseShellExecute  = false,
            CreateNoWindow   = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
        };
        Process.Start(psi); // intentionally not awaited — watcher picks up the result
    }

    // ── Private helpers ────────────────────────────────────────────────────

    /// <summary>
    /// Core launcher: writes scriptContent to a temp .sh file, then opens a
    /// terminal running  wsl -d {distro} -- bash /mnt/c/.../script.sh
    /// — a simple path with no shell operators that cannot be misinterpreted
    /// by Windows Terminal's command parser or cmd.exe.
    /// </summary>
    private static Process? RunInTerminal(string distro, string scriptContent, string title)
    {
        // Write Unix-line-ending, BOM-free temp script
        string tempWin = Path.Combine(
            Path.GetTempPath(), $"recliner_{Guid.NewGuid():N}.sh");

        File.WriteAllText(tempWin,
            scriptContent.Replace("\r\n", "\n"),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        string wslScript = WinPathToWslMount(tempWin);   // /mnt/c/Users/.../recliner_xxx.sh

        return TryWindowsTerminal(distro, wslScript, title)
            ?? FallbackConsole(distro, wslScript);
    }

    /// <summary>
    /// Try Windows Terminal first (preferred — supports named tabs).
    /// --window new forces a fresh WT window so we get back a real Process
    /// that RECLINER can Kill() when the user clicks Stop.
    /// Argument to wt.exe is deliberately simple: just a file path in quotes.
    /// Windows Terminal uses `;` as a command separator so we must NEVER embed
    /// raw bash (which contains `;`, `&&`, `$var`) directly in wt.exe args.
    /// </summary>
    private static Process? TryWindowsTerminal(string distro, string wslScriptPath, string title)
    {
        try
        {
            string safeTitle = title.Replace("\"", "");

            // --window new → dedicated WT window per launch so we can kill it later
            string wtArgs = $"--window new new-tab --title \"{safeTitle}\" " +
                            $"wsl.exe -d {distro} -- bash '{wslScriptPath}'";

            var psi = new ProcessStartInfo("wt.exe", wtArgs)
            {
                UseShellExecute = false  // false → we get back a real Process handle
            };
            return Process.Start(psi);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Fallback: open a cmd.exe window running wsl.exe directly.
    /// Again the argument to cmd is a plain script path — no bash operators.
    /// </summary>
    private static Process? FallbackConsole(string distro, string wslScriptPath)
    {
        try
        {
            string cmdArgs = $"/k wsl.exe -d {distro} -- bash \"{wslScriptPath}\"";

            var psi = new ProcessStartInfo("cmd.exe", cmdArgs)
            {
                UseShellExecute = true
            };
            return Process.Start(psi);
        }
        catch (Exception ex)
        {
            // Surface failure to the caller via a standard Windows error dialog
            System.Windows.MessageBox.Show(
                $"Could not open a terminal window.\n\n{ex.Message}",
                "RECLINER — Launch Error",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
            return null;
        }
    }
}
