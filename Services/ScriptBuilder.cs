using System.Text;

namespace Recliner.Services;

/// <summary>
/// Generates bash scripts that run in WSL to manage ComfyUI instances.
/// All scripts end with "read -p" so the terminal stays open on completion.
/// </summary>
public static class ScriptBuilder
{
    // All model subdirectories ComfyUI / ComfyUI Manager expect
    private static readonly string[] ModelDirs =
    [
        "checkpoints", "clip", "clip_vision", "configs",
        "controlnet", "diffusion_models", "embeddings",
        "gligen", "hypernetworks", "ipadapter", "loras",
        "photomaker", "style_models", "text_encoders",
        "unet", "upscale_models", "vae", "vae_approx"
    ];

    public static string TorchInstallCmd(string cudaTag) => cudaTag.ToLower() switch
    {
        "cpu" => "pip install torch torchvision torchaudio",
        _     => $"pip install torch torchvision torchaudio --index-url https://download.pytorch.org/whl/{cudaTag}"
    };

    // ── New instance (fresh git clone + full setup) ────────────────────────
    public static string NewInstance(
        string instanceName,
        string parentWslPath,
        string sharedModelsWslPath,
        string sharedOutputWslPath,
        string cudaTag,
        string comfyuiGitUrl)
    {
        string instPath = $"{parentWslPath.TrimEnd('/')}/{instanceName}";
        string outPath  = $"{sharedOutputWslPath.TrimEnd('/')}/{instanceName}";

        var s = new StringBuilder();
        s.AppendLine("#!/bin/bash");
        s.AppendLine("set -e");
        s.AppendLine();
        Banner(s, $"RECLINER — New Instance: {instanceName}");
        s.AppendLine();

        s.AppendLine("echo '[preflight] Ensuring system dependencies (python3, venv, git)...'");
        s.AppendLine("sudo apt-get update -qq && sudo apt-get install -y python3 python3-pip python3-venv git 2>&1 | grep -E 'already|newly|upgraded' || true");
        s.AppendLine("echo ''");
        s.AppendLine();

        s.AppendLine("echo '[1/6] Cloning ComfyUI...'");
        s.AppendLine($"git clone \"{comfyuiGitUrl}\" \"{instPath}\"");
        s.AppendLine($"cd \"{instPath}\"");
        s.AppendLine("echo ''");

        s.AppendLine("echo '[2/6] Installing ComfyUI Manager...'");
        s.AppendLine($"git clone \"https://github.com/ltdrdata/ComfyUI-Manager\" \"{instPath}/custom_nodes/ComfyUI-Manager\"");
        s.AppendLine($"echo '  Manager installed'");
        s.AppendLine("echo ''");

        s.AppendLine("echo '[3/6] Creating virtual environment...'");
        s.AppendLine("python3 -m venv venv");
        s.AppendLine("source venv/bin/activate");
        s.AppendLine("pip install --upgrade pip setuptools wheel");
        s.AppendLine("echo ''");

        s.AppendLine($"echo '[4/6] Installing PyTorch ({cudaTag})...'");
        s.AppendLine(TorchInstallCmd(cudaTag));
        s.AppendLine("echo ''");

        s.AppendLine("echo '[5/6] Installing requirements.txt...'");
        s.AppendLine("pip install -r requirements.txt");
        s.AppendLine("echo ''");

        s.AppendLine("echo '[6/7] Configuring shared paths...'");
        AppendSharedDirs(s, sharedModelsWslPath, outPath);
        s.AppendLine("echo ''");

        s.AppendLine("echo '[7/7] Writing start.sh...'");
        AppendStartScript(s, instPath);
        s.AppendLine("echo ''");

        Banner(s, $"Done!  {instanceName} is ready.");
        s.AppendLine("echo '  Hit Refresh in RECLINER to see it.'");
        s.AppendLine("echo ''");
        return s.ToString();
    }

    // ── Clone existing instance ────────────────────────────────────────────
    public static string CloneInstance(
        string sourceName,
        string instanceName,
        string parentWslPath,
        string sharedModelsWslPath,
        string sharedOutputWslPath,
        string cudaTag)
    {
        string srcPath  = $"{parentWslPath.TrimEnd('/')}/{sourceName}";
        string destPath = $"{parentWslPath.TrimEnd('/')}/{instanceName}";
        string outPath  = $"{sharedOutputWslPath.TrimEnd('/')}/{instanceName}";

        var s = new StringBuilder();
        s.AppendLine("#!/bin/bash");
        s.AppendLine("set -e");
        s.AppendLine();
        Banner(s, $"RECLINER — Clone: {sourceName}  →  {instanceName}");
        s.AppendLine();

        s.AppendLine("echo '[preflight] Ensuring system dependencies (python3, venv, git)...'");
        s.AppendLine("sudo apt-get update -qq && sudo apt-get install -y python3 python3-pip python3-venv git 2>&1 | grep -E 'already|newly|upgraded' || true");
        s.AppendLine("echo ''");
        s.AppendLine();

        s.AppendLine("echo '[1/4] Copying instance files (excluding venv/output)...'");
        s.AppendLine($"rsync -a --info=progress2 \\");
        s.AppendLine($"    --exclude='venv/' \\");
        s.AppendLine($"    --exclude='.venv/' \\");
        s.AppendLine($"    --exclude='output/' \\");
        s.AppendLine($"    --exclude='__pycache__/' \\");
        s.AppendLine($"    --exclude='*.pyc' \\");
        s.AppendLine($"    \"{srcPath}/\" \"{destPath}/\"");
        s.AppendLine("echo ''");

        s.AppendLine("echo '[2/4] Recreating virtual environment...'");
        s.AppendLine($"cd \"{destPath}\"");
        s.AppendLine("python3 -m venv venv");
        s.AppendLine("source venv/bin/activate");
        s.AppendLine("pip install --upgrade pip setuptools wheel");

        // Use pip freeze from source if its venv exists, otherwise fresh install
        s.AppendLine($"if [ -f \"{srcPath}/venv/bin/pip\" ]; then");
        s.AppendLine("    echo 'Replicating source packages from pip freeze...'");
        // Torch CUDA wheels are only on the PyTorch index, not PyPI — strip them
        // from the freeze file and reinstall them with the correct --index-url.
        s.AppendLine($"    \"{srcPath}/venv/bin/pip\" freeze \\");
        s.AppendLine("        | grep -v '^torch==' | grep -v '^torchvision==' | grep -v '^torchaudio==' \\");
        s.AppendLine("        > /tmp/recliner_clone_reqs.txt");
        s.AppendLine($"    echo 'Installing PyTorch ({cudaTag}) from PyTorch index...'");
        s.AppendLine($"    {TorchInstallCmd(cudaTag)} || echo 'WARNING: PyTorch install had errors'");
        s.AppendLine("    pip install -r /tmp/recliner_clone_reqs.txt || echo 'WARNING: some packages had errors'");
        s.AppendLine("    rm /tmp/recliner_clone_reqs.txt");
        s.AppendLine("else");
        s.AppendLine($"    echo 'No source venv found — fresh torch install ({cudaTag})...'");
        s.AppendLine($"    {TorchInstallCmd(cudaTag)}");
        s.AppendLine("    pip install -r requirements.txt");
        s.AppendLine("fi");
        s.AppendLine("echo ''");

        s.AppendLine("echo '[3/4] Setting up output directory...'");
        s.AppendLine($"mkdir -p \"{outPath}\"");
        s.AppendLine("echo ''");


        s.AppendLine("echo '[5/5] Writing start.sh...'");
        AppendStartScript(s, destPath);
        s.AppendLine("echo ''");

        Banner(s, $"Clone complete: {instanceName}");
        s.AppendLine("echo '  Hit Refresh in RECLINER to see it.'");
        s.AppendLine("echo ''");
        return s.ToString();
    }

    // ── Setup environment on an existing bare instance ─────────────────────
    public static string SetupEnvironment(
        string instanceName,
        string instanceWslPath,
        string sharedModelsWslPath,
        string sharedOutputWslPath,
        string cudaTag)
    {
        string outPath = $"{sharedOutputWslPath.TrimEnd('/')}/{instanceName}";

        var s = new StringBuilder();
        s.AppendLine("#!/bin/bash");
        // No set -e — each step is individually error-checked so the manifest
        // generation at the end always runs regardless of pip failures.
        s.AppendLine();
        Banner(s, $"RECLINER — Setup Environment: {instanceName}");
        s.AppendLine();
        s.AppendLine($"cd \"{instanceWslPath}\"");
        s.AppendLine();

        s.AppendLine("echo '[preflight] Ensuring system dependencies (python3, venv, git)...'");
        s.AppendLine("sudo apt-get update -qq && sudo apt-get install -y python3 python3-pip python3-venv git 2>&1 | grep -E 'already|newly|upgraded' || true");
        s.AppendLine("echo ''");
        s.AppendLine();

        s.AppendLine("echo '[1/6] Ensuring ComfyUI Manager is installed...'");
        s.AppendLine($"if [ ! -d \"{instanceWslPath}/custom_nodes/ComfyUI-Manager\" ]; then");
        s.AppendLine($"    git clone \"https://github.com/ltdrdata/ComfyUI-Manager\" \"{instanceWslPath}/custom_nodes/ComfyUI-Manager\"");
        s.AppendLine($"    echo '  Manager installed'");
        s.AppendLine("else");
        s.AppendLine("    echo '  Manager already present'");
        s.AppendLine("fi");
        s.AppendLine("echo ''");

        s.AppendLine("echo '[2/6] Activating virtual environment...'");
        s.AppendLine("if [ ! -d venv ]; then");
        s.AppendLine("    echo 'No venv found — creating one...'");
        s.AppendLine("    python3 -m venv venv || { echo 'ERROR: could not create venv'; }");
        s.AppendLine("fi");
        s.AppendLine("source venv/bin/activate 2>/dev/null || true");
        s.AppendLine("pip install --upgrade pip setuptools wheel 2>&1 | tail -1");
        s.AppendLine("echo ''");

        s.AppendLine($"echo '[3/6] Installing PyTorch ({cudaTag})...'");
        s.AppendLine(TorchInstallCmd(cudaTag) + " || echo 'WARNING: PyTorch install had errors — check output above'");
        s.AppendLine("echo ''");

        s.AppendLine("echo '[4/6] Installing requirements.txt...'");
        s.AppendLine("pip install -r requirements.txt || echo 'WARNING: requirements.txt install had errors'");
        s.AppendLine("echo ''");

        s.AppendLine("echo '[5/6] Configuring shared paths...'");
        AppendSharedDirs(s, sharedModelsWslPath, outPath);
        s.AppendLine("echo ''");

        s.AppendLine("echo '[6/6] Writing start.sh...'");
        AppendStartScript(s, instanceWslPath);
        s.AppendLine("echo ''");

        Banner(s, $"Setup complete: {instanceName}");
        s.AppendLine("echo '  RECLINER will auto-update — no refresh needed.'");
        s.AppendLine("echo ''");
        return s.ToString();
    }

    // ── Nuke (delete) an instance ─────────────────────────────────────────
    public static string NukeInstance(string instanceWslPath, string instanceName)
    {
        var s = new StringBuilder();
        s.AppendLine("#!/bin/bash");
        s.AppendLine();
        s.AppendLine("echo ''");
        s.AppendLine("echo '██████████████████████████████████████'");
        s.AppendLine("echo '  RECLINER — DELETE INSTANCE'");
        s.AppendLine("echo '██████████████████████████████████████'");
        s.AppendLine("echo ''");
        s.AppendLine($"echo '  Target : {instanceWslPath}'");
        s.AppendLine("echo '  Shared models and output are NOT affected.'");
        s.AppendLine("echo ''");
        s.AppendLine($"read -p 'Type the instance name to confirm [{instanceName}]: ' CONFIRM");
        s.AppendLine($"if [ \"$CONFIRM\" = \"{instanceName}\" ]; then");
        s.AppendLine($"    rm -rf \"{instanceWslPath}\"");
        s.AppendLine("    echo ''");
        s.AppendLine("    echo '✓ Instance deleted.'");
        s.AppendLine("else");
        s.AppendLine("    echo ''");
        s.AppendLine("    echo '✗ Name did not match — instance NOT deleted.'");
        s.AppendLine("fi");
        s.AppendLine("echo ''");
        return s.ToString();
    }

    // ── Private helpers ───────────────────────────────────────────────────

    private static void Banner(StringBuilder s, string msg)
    {
        string bar = new('═', Math.Max(msg.Length + 4, 44));
        s.AppendLine($"echo '╔{bar}╗'");
        s.AppendLine($"echo '║  {msg.PadRight(bar.Length - 2)}║'");
        s.AppendLine($"echo '╚{bar}╝'");
    }

    private static void AppendSharedDirs(StringBuilder s, string sharedModels, string outPath)
    {
        foreach (var dir in ModelDirs)
            s.AppendLine($"mkdir -p \"{sharedModels}/{dir}\"");
        s.AppendLine($"mkdir -p \"{outPath}\"");
        s.AppendLine($"echo '  ✓ Shared model directories ready'");
        s.AppendLine($"echo '  ✓ Output directory: {outPath}'");
    }

    private static void AppendModelPathsYaml(StringBuilder s, string instPath, string sharedModels)
    {
        s.AppendLine($"cat > \"{instPath}/extra_model_paths.yaml\" << 'RECLINER_YAML_EOF'");
        s.AppendLine("comfyui:");
        s.AppendLine($"    base_path: {sharedModels}/");
        foreach (var dir in ModelDirs)
            s.AppendLine($"    {dir}: {dir}/");
        s.AppendLine("RECLINER_YAML_EOF");
        s.AppendLine($"echo '  ✓ extra_model_paths.yaml written'");
    }

    /// <summary>
    /// Writes start.sh to the instance directory.
    /// Called at the end of every setup/clone/new-instance script so there is
    /// always a canonical launcher present.  The script resolves the venv Python
    /// by explicit path — no reliance on shell activation state — then exec's
    /// into ComfyUI so the terminal process IS python, not a wrapper shell.
    /// </summary>
    private static void AppendStartScript(StringBuilder s, string instPath)
    {
        s.AppendLine($"cat > \"{instPath}/start.sh\" << 'RECLINER_START_EOF'");
        s.AppendLine("#!/bin/bash");
        s.AppendLine("# RECLINER — generated launcher.  Re-run 'Setup Environment' to regenerate.");
        s.AppendLine("cd \"$(dirname \"$(realpath \"$0\")\")\"");
        s.AppendLine("RECLINER_PY=\"./venv/bin/python\"");
        s.AppendLine("[ -x \"$RECLINER_PY\" ] || RECLINER_PY=\"./.venv/bin/python\"");
        s.AppendLine("[ -x \"$RECLINER_PY\" ] || RECLINER_PY=python3");
        s.AppendLine("source venv/bin/activate 2>/dev/null || true");
        s.AppendLine("exec \"$RECLINER_PY\" main.py \"$@\"");
        s.AppendLine("RECLINER_START_EOF");
        s.AppendLine($"chmod +x \"{instPath}/start.sh\"");
        s.AppendLine($"echo '  start.sh written'");
    }

}
