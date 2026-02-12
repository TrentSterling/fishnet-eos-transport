# FishNet EOS Transport

Thin FishNet transport layer using Epic Online Services P2P. Depends on [eos-sdk](https://github.com/TrentSterling/eos-sdk) for EOS access — zero duplicated code.

## Features

- FishNet Transport implementation over EOS P2P
- Reliable and unreliable channels
- Packet fragmentation (handles >1170 byte EOS limit)
- Host migration with SyncVar save/restore
- Offline fallback mode (singleplayer when EOS unavailable)
- Auto-reconnect
- Dedicated server support
- Security validation (host authority, rate limiting)
- Zero-latency client-host IPC

## Install

Unity Package Manager > Add package from git URL (install eos-sdk first):

```
https://github.com/TrentSterling/eos-sdk.git
https://github.com/TrentSterling/fishnet-eos-transport.git
```

Also requires [FishNet](https://github.com/FirstGearGames/FishNet) installed in your project.

## Quick Start

1. Install eos-sdk and FishNet
2. Install this package
3. Add `NetworkManager` to your scene (FishNet)
4. Set the Transport to `EOSNativeTransport`
5. Add `EOSManager` component (from eos-sdk)
6. Hit Play

## Requirements

- Unity 6000.0+
- [com.tront.eos-sdk](https://github.com/TrentSterling/eos-sdk) 1.0.0+
- [FishNet](https://github.com/FirstGearGames/FishNet)

## Author

Trent Sterling — [tront.xyz](https://tront.xyz)
