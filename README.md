# RECLINER

> **R**egulatory **E**nvironment for **C**omfyUI **L**inux **I**nstances — **N**o-**E**rror **R**egulator

A Windows desktop app that gives ComfyUI a proper management console. Launch instances, create isolated environments, track versions and custom nodes — all from a point-and-click UI with no terminal required after setup.

---

## Already have ComfyUI running in WSL?

Download the exe, open it, click **⚙ Settings**, point it at the folder your ComfyUI lives in, and you're done. RECLINER finds your instance automatically. No install. No configuration files to write.

---

## Never used ComfyUI before?

Start here — in order.

### 1. Check if you have WSL2

WSL2 (Windows Subsystem for Linux) is built into Windows 10/11. It lets you run Linux software without leaving Windows. ComfyUI runs inside it.

Open PowerShell and run:
```
wsl --list --verbose
```

If you see a distro listed (Ubuntu is the most common), you're set. If you get an error or an empty list, install WSL2:
```
wsl --install
```
Restart your machine when it asks. Ubuntu installs by default.

### 2. Download RECLINER

Grab `RECLINER.exe` from the [Releases](../../releases) page. Drop it anywhere. Run it.

### 3. Configure Settings

Click **⚙ Settings** on first launch:

| Setting | What it is |
|---|---|
| WSL Distro | The name from your `wsl --list` output — usually `Ubuntu` |
| Parent Directory | A WSL folder that will hold all your ComfyUI instances — e.g. `/home/yourname/comfyui` |
| Shared Models Path | One central folder for all your model files (checkpoints, LoRAs, etc.) |
| Shared Output Path | Where generated images go, organized by instance |
| CUDA Tag | Your GPU's CUDA version — `cu124` for most modern NVIDIA cards, `cpu` if no GPU |

### 4. Create your first instance

Click **＋ New Instance**. RECLINER clones ComfyUI, creates a Python virtual environment, installs PyTorch, and sets everything up automatically in a terminal window. When it closes, your instance appears in the sidebar.

---

## The shared folders explained

When you have multiple ComfyUI instances, you don't want 50GB of model files copied into each one. The shared models folder solves this — all instances read from one central location. RECLINER wires each new instance to it automatically.

**Is it mandatory?** No. If you already have ComfyUI with models in the default location, RECLINER will manage it as-is. Shared folders only matter when you start creating additional instances — which is when RECLINER earns its place.

The shared output folder works the same way. Each instance gets its own subfolder inside it, so generated images stay organized without mixing between environments.

---

## What RECLINER manages

- **Isolation** — each instance is its own Python venv. Installing a node in one cannot break another.
- **Versions** — see ComfyUI version, PyTorch, CUDA build, TorchVision, TorchAudio per instance at a glance
- **Custom nodes** — full inventory per instance, enable/disable state visible
- **Ports** — each instance runs on its own port, editable inline
- **Startup flags** — configure `--lowvram`, `--preview-method auto`, etc. per instance with an in-app reference
- **Launch** — one click opens a terminal tab and optionally launches the browser
- **Live updates** — no refresh button. Add an instance, delete one, change a config — the UI updates within 2 seconds on its own

---

## Prerequisites

- Windows 10 or 11
- WSL2 (see above — likely already installed)
- That's it for the self-contained exe

To build from source: .NET 8 SDK

---

## Build from source

```bash
git clone https://github.com/YOUR_USERNAME/RECLINER.git
cd RECLINER
dotnet build -c Release
# Output: bin/Release/net8.0-windows/RECLINER.exe
```

---

## License

Apache 2.0 — see [LICENSE](LICENSE)
