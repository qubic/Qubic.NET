# Qubic.Core

Core domain models, identity handling, transaction building, and signing abstractions for the Qubic network.

> **Note:** This is an early version. APIs may change in future releases.

## Install

```
dotnet add package Qubic.Core
```

## Features

- `QubicIdentity` value type (from seed, public key, or 60-character identity string)
- Transaction building and signing via `TransactionBuilder`
- Domain entities: `QubicTransaction`, `QubicBalance`, `QubicTick`, `QubicTransfer`, `QubicAsset`
- Protocol constants and well-known contract definitions
- Transfer, SendMany, and AssetTransfer payloads
- Contract metadata at runtime: built-in contract catalog, C++ header import (`CppHeaderParser`), MSVC-layout encoding/decoding of input/output structs (`ContractCodec`)
- Sign-In with Qubic messages (`Qubic.Core.Auth.SignInMessage`) and message signing helpers (`QubicMessageSigning`: raw, and the WalletConnect `qubic_sign` convention)
- `qubic:` payment request URIs (`Qubic.Core.Payments.QubicPaymentRequest`)

## Usage

### Identity

```csharp
using Qubic.Core.Entities;

// From a 60-character identity string
var id = QubicIdentity.FromIdentity("BAAAAAAAA...");

// From a 55-character seed
var id = QubicIdentity.FromSeed("abcdefghijklmnopqrstuvwxyzabcdefghijklmnopqrstuvwxyzabc");

// From a 32-byte public key
var id = QubicIdentity.FromPublicKey(publicKeyBytes);
```

### Build and Sign a Transaction

```csharp
using Qubic.Core;
using Qubic.Core.Entities;

var builder = new TransactionBuilder();

var tx = builder.CreateTransfer(
    source: QubicIdentity.FromSeed(seed),
    destination: QubicIdentity.FromIdentity("DEST..."),
    amount: 1000,
    tick: currentTick + 5
);

builder.Sign(tx, seed);
```

### Contract metadata

```csharp
using Qubic.Core.Contracts.Metadata;

var catalog = new ContractCatalog();                       // built-in contracts are always included
var def = catalog.ImportHeader(File.ReadAllText("MyContract.h"), contractIndex: 30);

var mint = def.Procedures.First(p => p.Name == "Mint");
byte[] input = ContractCodec.Encode(mint.Input, new Dictionary<string, string>
{
    ["to"] = "BAAAAAAAA...", ["amount"] = "5000",
});

// Decode a transaction payload for display (works for built-in and imported contracts)
DecodedCall? call = catalog.DecodeProcedureCall(30, mint.InputType, input);
```

### Sign-In with Qubic

```csharp
using Qubic.Core.Auth;

var msg = new SignInMessage
{
    Domain = "example.com", Identity = identity, Uri = "https://example.com/login",
    Network = "qubic:mainnet", Nonce = SignInMessage.NewNonce(),
    IssuedAt = DateTimeOffset.UtcNow, ExpirationTime = DateTimeOffset.UtcNow.AddMinutes(10),
    Statement = "Sign in to Example.",
};
string text = msg.ToString();                              // give this to the wallet

// server side, after the wallet returned a signature
var parsed = SignInMessage.Parse(text);
bool ok = parsed.Verify(signature) && parsed.Validate(DateTimeOffset.UtcNow, "example.com", "qubic:mainnet").Count == 0;
```

### Payment requests

```csharp
using Qubic.Core.Payments;

var uri = new QubicPaymentRequest { Destination = identity, Amount = 5000, Message = "Invoice 42" }.ToUri();
// qubic:<IDENTITY>?amount=5000&message=Invoice%2042

if (QubicPaymentRequest.TryParse(pasted, out var req, out var error)) { /* req.Destination, req.Amount, req.ChainId … */ }
```

## Dependencies

- [Qubic.Crypto](https://www.nuget.org/packages/Qubic.Crypto)
