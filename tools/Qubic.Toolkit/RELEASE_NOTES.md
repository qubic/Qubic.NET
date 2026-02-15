# Qubic.Net.Toolkit Release Note v0.1.0

> [!NOTE]
> **This is beta software.** Errors may occur — use with caution.

> [!IMPORTANT]
> **Seed Safety:** The Toolkit never shares or sends your seed to the network. Your seed is only held locally in memory while the application runs. Close the app when not actively using it. Qubic will never contact you to ask for your seed — **DO NOT SHARE your seed with anyone.**

## What is Qubic.Net Toolkit?

A cross-platform desktop application for interacting with the Qubic network. Runs as a native desktop window on Windows, macOS, and Linux — or as a local web app in your browser with `--server` mode.

## Highlights

- **Native desktop window** — powered by Photino.Blazor using the OS webview (WebView2 on Windows, WKWebView on macOS, WebKitGTK on Linux)
- **Three backend options** — connect via official RPC, QubicBob JSON-RPC, or direct TCP to a Qubic node
- **Single-file binary** — self-contained, no .NET runtime required
- **Fully offline capable** — all CSS and fonts are bundled locally, no CDN or internet required for the UI

## Features

**Wallet & Transactions**
- **Send QU's** (single and batch)
- Burn QU, IPO bidding, custom transaction builder
- **Offline transaction** builder for air-gapped signing
- Message signing and verification
- Transaction history and tracking with **auto-resend**

**Smart Contracts**
- Interactive contract browser with auto-discovered functions and procedures
- DeFi suite: Qx, QSwap, QEarn, QBond, Quottery
- Utilities: QUtil, MSVault, Nostromo, QVault

**Explorer**
- Balance lookup and asset portfolio
- Transaction and transfer history lookup
- Tick data, computor list, active IPOs
- Transaction inclusion verification

**Tools**
- Identity generator (seed to public key)
- Broadcast pre-signed transactions
- Crypto toolkit (hashing, key derivation)
- **Oracle machine** queries
- Bob API playground

**Computor Operations** (RPC / Direct Network)
- Governance participation
- CCF performance metrics
- Node peer management

## Download

| Platform | File |
|----------|------|
| Windows x64 | `Qubic.Net.Toolkit-win-x64.zip` |
| macOS Apple Silicon (M1/M2/M3/M4) | `Qubic.Net.Toolkit-osx-arm64.zip` |
| macOS Intel | `Qubic.Net.Toolkit-osx-x64.zip` |
| Linux x64 | `Qubic.Net.Toolkit-linux-x64.zip` |

### Verify your download

> [!IMPORTANT]
> Always verify the SHA-256 hash against the checksums below to ensure the binary has not been tampered with:

```bash
# Windows (PowerShell)
Get-FileHash Qubic.Net.Toolkit-win-x64.zip -Algorithm SHA256

# macOS / Linux
sha256sum Qubic.Net.Toolkit-*.zip
```

| File | SHA-256 |
|------|---------|
| `Qubic.Net.Toolkit-win-x64.zip` | `67aae642ac48aac74d146bfe9cd81fa31175e4d0eff8a568575c654162d641dc` |
| `Qubic.Net.Toolkit-osx-arm64.zip` | `77c1bb66155948328cf87250bfca0551e7cb673816e8647dbcc98f07e2190953` |
| `Qubic.Net.Toolkit-osx-x64.zip` | `c064e5a0abbe2c10b58d9b4edd60134264a3f4416e3bcdff6f4b12f910fd41fb` |
| `Qubic.Net.Toolkit-linux-x64.zip` | `116288ed157821e6ce83b0219dcd2cfcc66b0b2ed270896f42d7c92b6bf31d92` |

### Running

Each zip extracts into a `Qubic.Net.Toolkit-{platform}` folder.

**Windows:** Extract `Qubic.Net.Toolkit-win-x64.zip`, open the folder, and run `Qubic.Net.Toolkit.exe`

**macOS**:

> [!NOTE]
> Pre-built macOS binaries only support **server mode** (`--server`). For native desktop window mode, [compile from source](https://github.com/qubic/Qubic.Net/tree/main/tools/Qubic.Toolkit#running-from-source).

Download `osx-arm64` for Apple Silicon (M1/M2/M3/M4) or `osx-x64` for Intel Macs.

```bash
unzip Qubic.Net.Toolkit-osx-arm64.zip
cd Qubic.Net.Toolkit-osx-arm64
chmod +x Qubic.Net.Toolkit
codesign --force --deep -s - Qubic.Net.Toolkit
xattr -d com.apple.quarantine Qubic.Net.Toolkit
./Qubic.Net.Toolkit --server
```

**Linux:**

Desktop mode requires **GLIBC 2.38+** and **WebKitGTK** (`libwebkit2gtk-4.1-0`).

| Distribution | Version | Desktop Mode | Server Mode |
|---|---|---|---|
| Ubuntu | 24.04+ (Noble) | Yes | Yes |
| Debian | 13+ (Trixie) | Yes | Yes |
| Fedora | 39+ | Yes | Yes |
| Arch Linux | Rolling | Yes | Yes |
| Ubuntu | 22.04 (Jammy) | No | Yes |
| Debian | 12 (Bookworm) | No | Yes |

```bash
# Install WebKitGTK (Ubuntu/Debian)
sudo apt install libwebkit2gtk-4.1-0

unzip Qubic.Net.Toolkit-linux-x64.zip
cd Qubic.Net.Toolkit-linux-x64
chmod +x Qubic.Net.Toolkit
./Qubic.Net.Toolkit
```

If desktop mode is not supported on your system, the app automatically falls back to server mode.

**Server mode** (all platforms — opens in browser, no GLIBC 2.38 or WebKitGTK required):
```
./Qubic.Net.Toolkit --server
```
