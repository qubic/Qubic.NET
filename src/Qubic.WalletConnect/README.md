# Qubic.WalletConnect

WalletConnect v2 **wallet side** for Qubic, built on [Reown WalletKit](https://docs.reown.com/) and the
signing review of `Qubic.Services`.

- Namespace `qubic`, chains `qubic:mainnet` (Mainnet profiles) and `qubic:testnet` (test/dev profiles).
- Methods, as specified by the official Qubic wallet
  ([walletconnect.md](https://github.com/qubic/wallet-app/blob/main/walletconnect.md)):
  `qubic_requestAccounts`, `qubic_sendQubic`, `qubic_signTransaction`, `qubic_sendTransaction`,
  `qubic_sign`, `qubic_sendAsset`.
- Every request is shown in the wallet's signing review before anything is signed.
- Requires a WalletConnect Cloud (Reown) project id.

The service only runs when the user enabled the WalletConnect feature.
