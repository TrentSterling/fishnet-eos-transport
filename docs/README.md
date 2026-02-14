# FishNet EOS Transport

> Thin FishNet transport layer over Epic Online Services P2P. v1.10.1

FishNet EOS Transport is a production-ready transport layer for [FishNet](https://github.com/FirstGearGames/FishNet) that uses [Epic Online Services](https://dev.epicgames.com/en-US/services) (EOS) P2P for networking. It depends on [eos-sdk](https://github.com/TrentSterling/eos-sdk) for all EOS access -- zero duplicated code.

## Features

- **Reliable + Unreliable Channels** -- Full FishNet channel support over EOS P2P
- **Packet Fragmentation** -- Automatically handles messages larger than the 1170-byte EOS P2P limit
- **Host Migration** -- Seamless host transfer with SyncVar save/restore when the host disconnects
- **Offline Fallback** -- Singleplayer mode when EOS is unavailable (no internet, SDK init failed)
- **Auto-Reconnect** -- Clients automatically reconnect when connection drops
- **Dedicated Server Support** -- Run headless servers with EOS authentication
- **Security Validation** -- Host authority checks and rate limiting
- **Zero-Latency Client-Host IPC** -- When the host is also a client, packets bypass the network entirely
- **Spatial Voice Integration** -- Automatic voice chat wiring when EOSVoiceManager is present
- **Lobby System** -- Create, join, search, and manage EOS lobbies with join codes
- **Install-Order Support** -- Compiles cleanly even without FishNet installed, auto-activates when FishNet is detected

## Quick Install

Install packages via Unity Package Manager (git URLs):

1. `https://github.com/TrentSterling/eos-sdk.git`
2. `https://github.com/TrentSterling/fishnet-eos-transport.git`
3. Install [FishNet](https://github.com/FirstGearGames/FishNet) (Asset Store or UPM)

As of v1.10.1, install order does not matter -- the transport compiles cleanly without FishNet and auto-activates when FishNet is detected.

See the [Installation Guide](installation.md) for details.

## Quick Start

1. **Tools > FishNet EOS > Setup Scene** -- creates a NetworkManager with the transport and an EOSManager
2. Enter Play Mode
3. Create a lobby and share the join code

See [Scene Setup](setup.md) for manual configuration.

## Requirements

| Dependency | Version |
|---|---|
| Unity | 6000.0+ |
| [com.tront.eos-sdk](https://github.com/TrentSterling/eos-sdk) | 1.0.0+ |
| [FishNet](https://github.com/FirstGearGames/FishNet) | v4.6+ |

## Author

Trent Sterling -- [tront.xyz](https://tront.xyz)
