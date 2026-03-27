#!/usr/bin/env python3
"""
generate_manifest.py
────────────────────
Scans every ComfyUI instance under a parent directory and writes a
manifest.json into each one.  Run this from WSL whenever you install
new custom nodes or update ComfyUI.

Usage:
    python3 generate_manifest.py
    python3 generate_manifest.py ~/ai-projects/comfyui
    python3 generate_manifest.py --port 8188 ~/ai-projects/comfyui/my-instance

The manifest.json is read by the ComfyUI Manifest Viewer (Windows WPF app).
"""

import os
import sys
import json
import re
import subprocess
from pathlib import Path
from datetime import datetime, timezone

# ─── Configuration ─────────────────────────────────────────────────────────────
DEFAULT_PARENT = os.path.expanduser("~/ai-projects/comfyui")
DEFAULT_PORT   = 8188


# ─── Helpers ───────────────────────────────────────────────────────────────────

def run(cmd: list[str], cwd: str | None = None, timeout: int = 10) -> str:
    """Run a command and return stdout, or '' on failure."""
    try:
        result = subprocess.run(
            cmd, capture_output=True, text=True,
            cwd=cwd, timeout=timeout
        )
        return result.stdout.strip()
    except Exception:
        return ""


def get_python_package_version(python: str, package: str) -> str:
    """Query an installed package version from a specific Python executable."""
    code = (
        f"import importlib.metadata as m; "
        f"print(m.version('{package}'))"
    )
    out = run([python, "-c", code])
    return out if out else "unknown"


def get_torch_info(python: str) -> dict:
    """Return PyTorch, TorchVision, TorchAudio versions + CUDA build string."""
    code = """
import torch, sys
try:
    import torchvision; tv = torchvision.__version__
except ImportError:
    tv = "not installed"
try:
    import torchaudio; ta = torchaudio.__version__
except ImportError:
    ta = "not installed"

cuda = torch.version.cuda or "cpu-only"
build = getattr(torch.version, 'cuda', None)
cuda_tag = ("cu" + build.replace(".", "")) if build else "cpu"

import json
print(json.dumps({
    "pytorch": torch.__version__,
    "torchvision": tv,
    "torchaudio": ta,
    "cuda": cuda,
    "cuda_build": cuda_tag,
}))
"""
    out = run([python, "-c", code])
    try:
        return json.loads(out)
    except Exception:
        return {
            "pytorch":    "unknown",
            "torchvision":"unknown",
            "torchaudio": "unknown",
            "cuda":       "unknown",
            "cuda_build": "unknown",
        }


def find_python(instance_dir: Path) -> str:
    """Locate the Python executable for this ComfyUI instance."""
    candidates = [
        instance_dir / "venv" / "bin" / "python",
        instance_dir / ".venv" / "bin" / "python",
        instance_dir / "env" / "bin" / "python",
        instance_dir / "venv" / "bin" / "python3",
    ]
    for c in candidates:
        if c.exists():
            return str(c)
    # Fall back to system python3
    return "python3"


def get_comfyui_version(instance_dir: Path) -> str:
    """Try to determine the ComfyUI version."""
    # 1. comfyui/__init__.py or root __init__.py
    for init in [
        instance_dir / "comfyui" / "__init__.py",
        instance_dir / "__init__.py",
    ]:
        if init.exists():
            text = init.read_text(errors="ignore")
            m = re.search(r'__version__\s*=\s*["\']([^"\']+)["\']', text)
            if m:
                return m.group(1)

    # 2. Git tag
    git_ver = run(["git", "describe", "--tags", "--abbrev=0"], cwd=str(instance_dir))
    if git_ver:
        return git_ver.lstrip("v")

    # 3. comfyui/version.py or similar
    ver_file = instance_dir / "comfyui" / "version.py"
    if ver_file.exists():
        text = ver_file.read_text(errors="ignore")
        m = re.search(r'["\']([0-9]+\.[0-9]+[^"\']*)["\']', text)
        if m:
            return m.group(1)

    return "unknown"


def get_port_from_extra_args(instance_dir: Path) -> int | None:
    """Try to read a custom port from extra_args.txt or similar config."""
    candidates = [
        instance_dir / "extra_args.txt",
        instance_dir / "args.txt",
        instance_dir / "launch_args.txt",
    ]
    for f in candidates:
        if f.exists():
            text = f.read_text(errors="ignore")
            m = re.search(r'--port\s+(\d+)', text)
            if m:
                return int(m.group(1))
    return None


def get_custom_nodes(instance_dir: Path) -> list[dict]:
    """Enumerate custom_nodes/, returning name + version + enabled status."""
    nodes = []
    custom_nodes_dir = instance_dir / "custom_nodes"
    if not custom_nodes_dir.is_dir():
        return nodes

    for entry in sorted(custom_nodes_dir.iterdir()):
        if not entry.is_dir():
            continue
        name = entry.name
        if name.startswith(".") or name == "__pycache__":
            continue

        enabled = not (entry / ".disabled").exists()
        version = detect_node_version(entry)

        nodes.append({
            "name":    name,
            "version": version,
            "enabled": enabled,
        })
    return nodes


def detect_node_version(node_dir: Path) -> str:
    """Try pyproject.toml → setup.cfg → version.txt → __init__.py → git tag."""
    # pyproject.toml
    ppt = node_dir / "pyproject.toml"
    if ppt.exists():
        text = ppt.read_text(errors="ignore")
        m = re.search(r'version\s*=\s*["\']([^"\']+)["\']', text)
        if m:
            return m.group(1)

    # setup.cfg
    cfg = node_dir / "setup.cfg"
    if cfg.exists():
        text = cfg.read_text(errors="ignore")
        m = re.search(r'version\s*=\s*([^\s\n]+)', text)
        if m:
            return m.group(1)

    # version.txt
    vt = node_dir / "version.txt"
    if vt.exists():
        return vt.read_text(errors="ignore").strip()

    # __init__.py
    init = node_dir / "__init__.py"
    if init.exists():
        text = init.read_text(errors="ignore")
        m = re.search(r'__version__\s*=\s*["\']([^"\']+)["\']', text)
        if m:
            return m.group(1)

    # git tag
    git_ver = run(["git", "describe", "--tags", "--abbrev=0"], cwd=str(node_dir))
    if git_ver:
        return git_ver.lstrip("v")

    return "unknown"


def get_failed_imports(instance_dir: Path) -> list[str]:
    """
    Read failed custom node imports from ComfyUI's log or the
    custom_nodes/__MACOSX / .disabled list that ComfyUI Manager writes.
    Falls back to scanning for nodes that have a .disabled marker but
    also checking comfyui-manager's failed list if present.
    """
    failed: list[str] = []

    # ComfyUI Manager stores a failed list here:
    manager_failed = (
        instance_dir / "custom_nodes" / "ComfyUI-Manager" / "failed_nodes.json"
    )
    if manager_failed.exists():
        try:
            data = json.loads(manager_failed.read_text())
            if isinstance(data, list):
                failed.extend(str(x) for x in data)
            elif isinstance(data, dict):
                failed.extend(data.keys())
        except Exception:
            pass

    # Also scan the ComfyUI log file for "Cannot import" lines
    log_candidates = [
        instance_dir / "comfyui.log",
        instance_dir / "comfy.log",
        Path.home() / ".comfyui" / "comfyui.log",
    ]
    import_error_re = re.compile(
        r"Cannot import.*?'([^']+)'|"
        r"IMPORT FAILED.*?'([^']+)'|"
        r"Failed to import.*?'([^']+)'",
        re.IGNORECASE,
    )
    for log in log_candidates:
        if log.exists():
            try:
                text = log.read_text(errors="ignore")
                for m in import_error_re.finditer(text):
                    name = m.group(1) or m.group(2) or m.group(3)
                    if name and name not in failed:
                        failed.append(name)
            except Exception:
                pass

    return list(dict.fromkeys(failed))  # deduplicate, preserve order


def count_output_files(instance_dir: Path) -> int:
    output_dir = instance_dir / "output"
    if not output_dir.is_dir():
        return -1
    try:
        return sum(1 for _ in output_dir.rglob("*") if _.is_file())
    except Exception:
        return -1


# ─── Main ──────────────────────────────────────────────────────────────────────

def build_manifest(instance_dir: Path, port: int) -> dict:
    python = find_python(instance_dir)
    torch  = get_torch_info(python)

    custom_port = get_port_from_extra_args(instance_dir)
    effective_port = custom_port if custom_port else port

    output_folder = str(instance_dir / "output")
    output_count  = count_output_files(instance_dir)

    return {
        "comfyui_version":    get_comfyui_version(instance_dir),
        "pytorch_version":    torch["pytorch"],
        "torchvision_version":torch["torchvision"],
        "torchaudio_version": torch["torchaudio"],
        "cuda_build":         torch["cuda_build"],
        "port":               effective_port,
        "output_folder":      output_folder,
        "output_file_count":  output_count,
        "custom_nodes":       get_custom_nodes(instance_dir),
        "failed_imports":     get_failed_imports(instance_dir),
        "launch_command":     "python main.py",
        "generated_at":       datetime.now(timezone.utc).isoformat(),
    }


def process_instance(instance_dir: Path, port: int, verbose: bool = True) -> None:
    if verbose:
        print(f"\n  ▶ {instance_dir.name}", end=" ", flush=True)

    manifest = build_manifest(instance_dir, port)
    out_path  = instance_dir / "manifest.json"
    out_path.write_text(json.dumps(manifest, indent=2))

    if verbose:
        ver   = manifest["comfyui_version"]
        nodes = len(manifest["custom_nodes"])
        failed= len(manifest["failed_imports"])
        print(f"→ v{ver}  |  {nodes} nodes  |  {failed} failed  ✓")


def main() -> None:
    args = sys.argv[1:]

    # Simple arg parsing: optional --port N, then optional path
    port = DEFAULT_PORT
    paths: list[str] = []
    i = 0
    while i < len(args):
        if args[i] == "--port" and i + 1 < len(args):
            port = int(args[i + 1])
            i += 2
        else:
            paths.append(args[i])
            i += 1

    if not paths:
        paths = [DEFAULT_PARENT]

    print("ComfyUI Manifest Generator")
    print("──────────────────────────")

    for raw_path in paths:
        target = Path(os.path.expanduser(raw_path)).resolve()

        # If the target itself looks like a ComfyUI dir (has main.py), generate for it
        if (target / "main.py").exists():
            process_instance(target, port)
            continue

        # Otherwise treat as parent directory
        if not target.is_dir():
            print(f"  ✗ Not found: {target}")
            continue

        print(f"\nScanning parent: {target}")
        subdirs = sorted(
            d for d in target.iterdir()
            if d.is_dir() and not d.name.startswith(".")
        )
        if not subdirs:
            print("  (no subdirectories found)")
            continue

        for sub in subdirs:
            process_instance(sub, port)

    print("\nDone. Reload the viewer to see updated manifests.\n")


if __name__ == "__main__":
    main()
