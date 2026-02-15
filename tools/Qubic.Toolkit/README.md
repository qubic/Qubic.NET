# Qubic.Toolkit

A cross-platform desktop application for interacting with the Qubic network. Runs as a native desktop window on Windows, macOS, and Linux — or as a local web app in your browser with `--server` mode.

![toolkit-architecture](https://github.com/user-attachments/assets/87f36279-1693-4388-aa64-c32e5e77a011)

> [!NOTE] 
> **Beta Software** — This tool is a playground and under active development. Errors may occur. Use it with caution.


> [!IMPORTANT]  
> **Seed Safety** — The Toolkit never shares or sends your seed to the network. Your seed is only held locally in memory while the application is running. Close the app when you are not actively using it. Qubic will never contact you to ask for your seed — **DO NOT SHARE your seed with anyone.**

## Features

- Wallet management with seed-based identity
- Transaction building and broadcasting (via RPC, Bob, or direct TCP)
- Smart contract interaction (Qx, QUtil, and more)
- Real-time tick monitoring
- Transaction tracking
- Asset registry browsing
- Oracle machine queries

## Running Pre-Built Releases

Download the latest release for your platform from the [Releases](https://github.com/qubic/Qubic.Net/releases) page.

> [!IMPORTANT]
> **Always verify the SHA-256 hashes** to ensure files have not been tampered with.

**Verify the zip download** against the `.zip.sha256` file published alongside each release:

```bash
# Windows (PowerShell)
Get-FileHash Qubic.Net.Toolkit-win-x64.zip -Algorithm SHA256

# macOS / Linux
sha256sum -c Qubic.Net.Toolkit-linux-x64.zip.sha256
```

**Verify the binary** after extracting — each zip contains a `.sha256` file for the binary:

```bash
# Windows (PowerShell)
Get-FileHash Qubic.Net.Toolkit.exe -Algorithm SHA256

# macOS / Linux
sha256sum -c Qubic.Net.Toolkit.sha256
```

### Windows

1. Download and extract `Qubic.Net.Toolkit-win-x64.zip`
2. Open the `Qubic.Net.Toolkit-win-x64` folder and run `Qubic.Net.Toolkit.exe`

To run in server mode (opens in browser instead of native window):

```
Qubic.Net.Toolkit.exe --server
```

### macOS

Requires **macOS 12 (Monterey)** or later.

> [!NOTE]
> Pre-built macOS binaries only support **server mode** (`--server`). Native desktop window mode requires [compiling from source](#running-from-source).

1. Download the zip for your architecture:
   - **Apple Silicon** (M1/M2/M3/M4): `Qubic.Net.Toolkit-osx-arm64.zip`
   - **Intel**: `Qubic.Net.Toolkit-osx-x64.zip`
2. Extract and run in server mode:

```bash
unzip Qubic.Net.Toolkit-osx-arm64.zip
cd Qubic.Net.Toolkit-osx-arm64
chmod +x Qubic.Net.Toolkit
codesign --force --deep -s - Qubic.Net.Toolkit
xattr -d com.apple.quarantine Qubic.Net.Toolkit
./Qubic.Net.Toolkit --server
```

### Linux

Desktop mode requires **GLIBC 2.38+** and **WebKitGTK**. Supported distributions:

| Distribution | Version | Desktop Mode | Server Mode |
|---|---|---|---|
| Ubuntu | 24.04+ (Noble) | Yes | Yes |
| Debian | 13+ (Trixie) | Yes | Yes |
| Fedora | 39+ | Yes | Yes |
| Arch Linux | Rolling | Yes | Yes |
| Ubuntu | 22.04 (Jammy) | No | Yes |
| Debian | 12 (Bookworm) | No | Yes |

Install WebKitGTK for desktop mode:

```bash
# Ubuntu/Debian
sudo apt install libwebkit2gtk-4.1-0
```

1. Download and extract `Qubic.Net.Toolkit-linux-x64.zip`
2. Run:

```bash
cd Qubic.Net.Toolkit-linux-x64
chmod +x Qubic.Net.Toolkit
./Qubic.Net.Toolkit
```

If desktop mode is not supported on your system, the app automatically falls back to server mode.

To run in server mode directly (no GLIBC 2.38 or WebKitGTK required):

```bash
./Qubic.Net.Toolkit --server
```

## Running From Source

### Desktop Mode (default)

Opens a native desktop window:

```bash
dotnet run --project tools/Qubic.Toolkit
```

### Server Mode

Runs as a Blazor Server app and opens the browser:

```bash
dotnet run --project tools/Qubic.Toolkit -- --server
```

## Publishing

### Windows Single-File

```bash
dotnet publish tools/Qubic.Toolkit -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

### macOS (Intel)

```bash
dotnet publish tools/Qubic.Toolkit -c Release -r osx-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

### macOS (Apple Silicon)

```bash
dotnet publish tools/Qubic.Toolkit -c Release -r osx-arm64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

### Linux

Requires WebKitGTK (`libwebkit2gtk-4.1`):

```bash
# Ubuntu/Debian
sudo apt install libwebkit2gtk-4.1-0

dotnet publish tools/Qubic.Toolkit -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

## Architecture

- **SDK**: `Microsoft.NET.Sdk.Razor` with `FrameworkReference` to `Microsoft.AspNetCore.App`
- **Desktop**: Photino.Blazor 4.0.13 renders Blazor components in a native OS webview
- **Server**: Standard Blazor Server with interactive server-side rendering
- **Static assets**: Bootstrap 5.3.2 and Bootstrap Icons 1.11.3 bundled locally (no CDN)
- **Single-file publish**: wwwroot is embedded as a zip resource and extracted to `%LOCALAPPDATA%/Qubic.Toolkit` on first run (auto-refreshes when the build changes)

## Screenshots
<img width="500"  alt="image" src="https://github.com/user-attachments/assets/37a05bb2-3471-4fa9-9e9b-247af50b34c5" />
<img width="800" alt="image" src="https://github.com/user-attachments/assets/e0e6654c-5c1c-48c1-af31-ad5795c1e4ae" />
<img width="500"  alt="image" src="https://github.com/user-attachments/assets/28fb6a7c-fb30-4c97-a83d-d8cc9130f2e7" />
<img width="800"  alt="image" src="https://github.com/user-attachments/assets/eb60147a-5bab-418c-8a22-2e5d48941337" />
<img width="800" alt="image" src="https://github.com/user-attachments/assets/33f8649c-76b2-4901-aa6b-2b2b2efa9353" />
<img width="500"  alt="image" src="https://github.com/user-attachments/assets/379ddd39-1c5c-463a-b693-170356872ef8" />







