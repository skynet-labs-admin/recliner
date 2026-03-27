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

        s.AppendLine("echo '[1/5] Cloning ComfyUI...'");
        s.AppendLine($"git clone \"{comfyuiGitUrl}\" \"{instPath}\"");
        s.AppendLine($"cd \"{instPath}\"");
        s.AppendLine("echo ''");

        s.AppendLine("echo '[2/5] Creating virtual environment...'");
        s.AppendLine("python3 -m venv venv");
        s.AppendLine("source venv/bin/activate");
        s.AppendLine("pip install --upgrade pip setuptools wheel");
        s.AppendLine("echo ''");

        s.AppendLine($"echo '[3/5] Installing PyTorch ({cudaTag})...'");
        s.AppendLine(TorchInstallCmd(cudaTag));
        s.AppendLine("echo ''");

        s.AppendLine("echo '[4/5] Installing requirements.txt...'");
        s.AppendLine("pip install -r requirements.txt");
        s.AppendLine("echo ''");

        s.AppendLine("echo '[5/6] Configuring shared paths...'");
        AppendSharedDirs(s, sharedModelsWslPath, outPath);
        AppendModelPathsYaml(s, instPath, sharedModelsWslPath);
        s.AppendLine("echo ''");

        s.AppendLine("echo '[6/7] Writing start.sh...'");
        AppendStartScript(s, instPath);
        s.AppendLine("echo ''");

        s.AppendLine("echo '[7/7] Generating manifest...'");
        AppendManifestGeneration(s, instPath, outPath);
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

        s.AppendLine("echo '[4/6] Writing extra_model_paths.yaml...'");
        AppendModelPathsYaml(s, destPath, sharedModelsWslPath);
        s.AppendLine("echo ''");

        s.AppendLine("echo '[5/6] Writing start.sh...'");
        AppendStartScript(s, destPath);
        s.AppendLine("echo ''");

        s.AppendLine("echo '[6/6] Generating manifest...'");
        AppendManifestGeneration(s, destPath, outPath);
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

        s.AppendLine("echo '[1/5] Activating virtual environment...'");
        s.AppendLine("if [ ! -d venv ]; then");
        s.AppendLine("    echo 'No venv found — creating one...'");
        s.AppendLine("    python3 -m venv venv || { echo 'ERROR: could not create venv'; }");
        s.AppendLine("fi");
        s.AppendLine("source venv/bin/activate 2>/dev/null || true");
        s.AppendLine("pip install --upgrade pip setuptools wheel 2>&1 | tail -1");
        s.AppendLine("echo ''");

        s.AppendLine($"echo '[2/5] Installing PyTorch ({cudaTag})...'");
        s.AppendLine(TorchInstallCmd(cudaTag) + " || echo 'WARNING: PyTorch install had errors — check output above'");
        s.AppendLine("echo ''");

        s.AppendLine("echo '[3/5] Installing requirements.txt...'");
        s.AppendLine("pip install -r requirements.txt || echo 'WARNING: requirements.txt install had errors'");
        s.AppendLine("echo ''");

        s.AppendLine("echo '[4/5] Configuring shared paths...'");
        AppendSharedDirs(s, sharedModelsWslPath, outPath);
        AppendModelPathsYaml(s, instanceWslPath, sharedModelsWslPath);
        s.AppendLine("echo ''");

        s.AppendLine("echo '[5/6] Writing start.sh...'");
        AppendStartScript(s, instanceWslPath);
        s.AppendLine("echo ''");

        // Manifest step is unconditional — runs even if earlier steps had warnings
        s.AppendLine("echo '[6/6] Generating manifest...'");
        AppendManifestGeneration(s, instanceWslPath, outPath);
        s.AppendLine("echo ''");

        Banner(s, $"Setup complete: {instanceName}");
        s.AppendLine("echo '  RECLINER will auto-update — no refresh needed.'");
        s.AppendLine("echo ''");
        return s.ToString();
    }

    // ── Manifest-only regeneration (no install, no terminal) ──────────────
    /// <summary>
    /// Writes the latest generate_manifest.py to the instance directory and
    /// immediately runs it with the instance venv Python.  No terminal is
    /// opened — intended for silent background correction of stale manifests.
    /// </summary>
    public static string RegenerateManifest(string instPath, string outPath)
    {
        var s = new StringBuilder();
        s.AppendLine("#!/bin/bash");
        AppendManifestGeneration(s, instPath, outPath);
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

    /// <summary>
    /// Writes generate_manifest.py to the instance directory (as a heredoc so no
    /// external file is needed) then runs it with the active venv Python.
    /// Passes --output-folder so the manifest records the shared output path.
    /// Failures are non-fatal — a warning is printed but the script continues.
    /// </summary>
    private static void AppendManifestGeneration(StringBuilder s, string instPath, string outPath)
    {
        // Write the script via a heredoc — single-quote delimiter means no
        // variable expansion inside, so Python strings are safe.
        s.AppendLine($"cat > \"{instPath}/generate_manifest.py\" << 'RECLINER_PY_EOF'");
        s.AppendLine(ManifestPyScript);
        s.AppendLine("RECLINER_PY_EOF");

        // Resolve python explicitly from the venv — never rely on shell activation state.
        // Tries venv/bin/python, then .venv/bin/python, then falls back to python3.
        // This guarantees the correct interpreter (with torch installed) is always used.
        s.AppendLine($"RECLINER_PY=\"{instPath}/venv/bin/python\"");
        s.AppendLine($"if [ ! -x \"$RECLINER_PY\" ]; then RECLINER_PY=\"{instPath}/.venv/bin/python\"; fi");
        s.AppendLine($"if [ ! -x \"$RECLINER_PY\" ]; then RECLINER_PY=python3; fi");
        s.AppendLine($"\"$RECLINER_PY\" \"{instPath}/generate_manifest.py\" " +
                     $"--output-folder \"{outPath}\" && " +
                     $"echo '  ✓ manifest.json written' || " +
                     $"echo '  ⚠  Manifest generation had errors — check output above'");
    }

    // ── Embedded generate_manifest.py ────────────────────────────────────
    // Written as a heredoc into every instance during setup/clone.
    // Single-quoted heredoc delimiter on the bash side prevents expansion,
    // so Python string literals here do not need any extra escaping.
    private const string ManifestPyScript = @"#!/usr/bin/env python3
""""""
RECLINER — generate_manifest.py
Run from inside the instance venv to regenerate manifest.json.
Usage: python generate_manifest.py [--output-folder /path] [--port 8188]
""""""
import argparse
import importlib.metadata
import json
import os
import re
import subprocess
import sys
from datetime import datetime, timezone
from pathlib import Path

ROOT = Path(__file__).parent


def pkg_version(name):
    try:
        return importlib.metadata.version(name)
    except Exception:
        try:
            mod = __import__(name)
            return getattr(mod, '__version__', 'unknown')
        except Exception:
            return 'unknown'


def torch_info():
    try:
        import torch
        return torch.__version__, (torch.version.cuda or 'cpu')
    except ImportError:
        return 'unknown', 'unknown'


def comfyui_version():
    candidates = [
        ROOT / 'comfyui' / 'version.py',      # comfy-org/ComfyUI (current)
        ROOT / 'comfyui' / '__init__.py',
        ROOT / 'comfyui_version.py',
        ROOT / '__init__.py',
        ROOT / 'version.txt',
    ]
    for c in candidates:
        if not c.exists():
            continue
        text = c.read_text(encoding='utf-8', errors='ignore')
        m = re.search(r'__version__\s*=\s*[^\d]*(\d+\.\d[\d.]*)', text)
        if m:
            return m.group(1)
        if c.name == 'version.txt':
            return text.strip()
    return 'unknown'


def custom_nodes():
    out = []
    nd = ROOT / 'custom_nodes'
    if not nd.exists():
        return out
    for d in sorted(nd.iterdir()):
        if not d.is_dir() or d.name.startswith('.') or d.name == '__pycache__':
            continue
        enabled = not (d / '.disabled').exists()
        ver = 'unknown'
        git_hash = None
        try:
            r = subprocess.run(['git', 'rev-parse', '--short', 'HEAD'],
                               cwd=d, capture_output=True, text=True, timeout=5)
            if r.returncode == 0:
                git_hash = r.stdout.strip()
        except Exception:
            pass
        for vf in ('pyproject.toml', 'setup.cfg', 'version.txt', '__init__.py'):
            p = d / vf
            if not p.exists():
                continue
            try:
                m = re.search(r'version\s*[=:]\s*[^\d]*(\d+\.\d[\w.]*)',
                               p.read_text(encoding='utf-8', errors='ignore'))
                if m:
                    ver = m.group(1)
                    break
            except Exception:
                pass
        out.append({'name': d.name, 'version': ver, 'enabled': enabled, 'git_hash': git_hash})
    return out


def failed_imports():
    failed = []
    for log_name in ('comfyui_startup.log', '.comfyui_startup.log'):
        p = ROOT / log_name
        if p.exists():
            for m in re.finditer(r'(?:ERROR|FAILED)[^\n]*import[^\n]+',
                                  p.read_text(errors='ignore'), re.IGNORECASE):
                failed.append(m.group(0).strip())
    return failed


def detect_port():
    for cfg in ('user.yaml', 'config.yaml', 'extra_config.yaml'):
        p = ROOT / cfg
        if p.exists():
            m = re.search(r'port\s*[=:]\s*(\d+)', p.read_text(errors='ignore'))
            if m:
                return int(m.group(1))
    return 8188


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--output-folder', default=None)
    ap.add_argument('--port', type=int, default=None)
    args = ap.parse_args()

    pytorch_ver, cuda_build = torch_info()
    manifest = {
        'comfyui_version':    comfyui_version(),
        'pytorch_version':    pytorch_ver,
        'torchvision_version': pkg_version('torchvision'),
        'torchaudio_version':  pkg_version('torchaudio'),
        'cuda_build':         cuda_build,
        'port':               args.port or detect_port(),
        'output_folder':      args.output_folder or str(ROOT / 'output'),
        'output_file_count':  -1,
        'custom_nodes':       custom_nodes(),
        'failed_imports':     failed_imports(),
        'launch_command':     'python main.py',
        'generated_at':       datetime.now(timezone.utc).isoformat(),
    }
    out = ROOT / 'manifest.json'
    out.write_text(json.dumps(manifest, indent=2), encoding='utf-8')
    print(f'manifest.json -> {out}')


if __name__ == '__main__':
    main()
";
}
