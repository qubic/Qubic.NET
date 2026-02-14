# Qubic.Toolkit

Cross-platform desktop application for interacting with the Qubic network. Built with [Photino.Blazor](https://github.com/tryphotino/photino.Blazor) for native window rendering on Windows (WebView2), macOS (WKWebView), and Linux (WebKitGTK).

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

Download the latest release for your platform from the [Releases](https://github.com/user/Qubic.Net/releases) page.

> [!IMPORTANT]  
> **Always verify the SHA-256 hash** of the downloaded file against the checksum published in the release notes to ensure the binary has not been tampered with:

```bash
# Windows (PowerShell)
Get-FileHash Qubic.Net.Toolkit-win-x64.zip -Algorithm SHA256

# macOS / Linux
sha256sum Qubic.Net.Toolkit-*.zip
```

### Windows

1. Download `Qubic.Net.Toolkit-win-x64.zip`
2. Extract and run `Qubic.Net.Toolkit.exe`

To run in server mode (opens in browser instead of native window):

```
Qubic.Net.Toolkit.exe --server
```

### macOS

1. Download `Qubic.Net.Toolkit-osx-x64.zip`
2. Extract and run:

```bash
chmod +x Qubic.Net.Toolkit
codesign --force --deep -s - Qubic.Net.Toolkit
xattr -d com.apple.quarantine Qubic.Net.Toolkit
./Qubic.Net.Toolkit
```

### Linux

Requires WebKitGTK for desktop mode:

```bash
# Ubuntu/Debian
sudo apt install libwebkit2gtk-4.1-0
```

1. Download `Qubic.Net.Toolkit-linux-x64.zip`
2. Extract and run:

```bash
chmod +x Qubic.Net.Toolkit
./Qubic.Net.Toolkit
```

To run in server mode (no WebKitGTK required):

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
dotnet publish tools/Qubic.Toolkit -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

### macOS

```bash
dotnet publish tools/Qubic.Toolkit -c Release -r osx-x64 --self-contained -p:PublishSingleFile=true
```

### Linux

Requires WebKitGTK (`libwebkit2gtk-4.1`):

```bash
# Ubuntu/Debian
sudo apt install libwebkit2gtk-4.1-0

dotnet publish tools/Qubic.Toolkit -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true
```

## Architecture

- **SDK**: `Microsoft.NET.Sdk.Razor` with `FrameworkReference` to `Microsoft.AspNetCore.App`
- **Desktop**: Photino.Blazor 4.0.13 renders Blazor components in a native OS webview
- **Server**: Standard Blazor Server with interactive server-side rendering
- **Static assets**: Bootstrap 5.3.2 and Bootstrap Icons 1.11.3 bundled locally (no CDN)
- **Single-file publish**: wwwroot is embedded as a zip resource and extracted to `%LOCALAPPDATA%/Qubic.Toolkit` on first run (auto-refreshes when the build changes)
