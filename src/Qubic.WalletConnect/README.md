# Qubic.WalletConnect

WalletConnect v2 **wallet side** for Qubic, built on [Reown WalletKit](https://docs.reown.com/) and the
signing review of `Qubic.Services`.

- Namespace `qubic`, chains `qubic:mainnet` (Mainnet profiles) and `qubic:testnet` (test/dev profiles).
- Methods, as specified by the official Qubic wallet
  ([walletconnect.md](https://github.com/qubic/wallet-app/blob/main/walletconnect.md)):
  `qubic_requestAccounts`, `qubic_sendQubic`, `qubic_signTransaction`, `qubic_sendTransaction`,
  `qubic_sign`, `qubic_sendAsset`.
- Every request is shown in the wallet's signing review before anything is signed.
- Requires a WalletConnect Cloud (Reown) project id, supplied by the app through `WalletConnectOptions`.
- A session is bound to the chain of the active network profile; proposals for another chain are refused.

```csharp
services.AddSingleton(new WalletConnectOptions
{
    ProjectId = "<your Reown project id>",
    Name = "My Wallet", Url = "https://example.org", Description = "…", Icon = "https://example.org/icon.png",
});
services.AddSingleton<QubicWalletConnectService>();

// once the WalletConnect feature is enabled:
await walletConnect.StartAsync();
await walletConnect.PairAsync("wc:…");   // URI from the dApp's QR code
```

The service only runs when the user enabled the WalletConnect feature. Only one process per data folder
(`QubicSettingsService.StorageDirectory`) runs the client: it holds `walletconnect.lock`. Another process
reports `WalletConnectState.OtherInstance` and takes over when the lock is released.
