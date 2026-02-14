# Scene Setup

## Quick Setup (Recommended)

The fastest way to get started:

**Tools > FishNet EOS > Setup Scene**

This menu item creates a ready-to-use scene with:
- A **NetworkManager** GameObject with `EOSNativeTransport` as the transport
- An **EOSManager** GameObject with EOS initialization components

All you need to do is assign your **EOSConfig** asset (from the eos-sdk package) to the transport component.

## Manual Setup

If you prefer to set things up by hand:

### 1. Create the NetworkManager

1. Create an empty GameObject named `NetworkManager`
2. Add the **NetworkManager** component (from FishNet)
3. Add the **EOSNativeTransport** component (under FishNet > Transport)
4. On the NetworkManager component, set the **Transport** field to your EOSNativeTransport

### 2. Create the EOSManager

1. Create an empty GameObject named `EOSManager`
2. Add the **EOSManager** component (from eos-sdk)
3. Create or assign an **EOSConfig** asset with your EOS application credentials

### 3. (Optional) Add Migration Support

If you want host migration:

1. On the NetworkManager GameObject, add **HostMigrationManager**
2. Add **HostMigrationPlayerSpawner** and assign your player prefab

See [Host Migration](host-migration.md) for details.

## Transport Settings

Configure the `EOSNativeTransport` component in the Inspector:

| Setting | Default | Description |
|---|---|---|
| **EOS Config** | -- | Your EOSConfig asset with application credentials |
| **Socket Name** | `FishNetEOS` | EOS P2P socket identifier. Must match between host and clients. |
| **Max Clients** | 64 | Maximum number of clients that can connect to the server |
| **Timeout** | 25s | Connection timeout before a client is considered disconnected |
| **Relay Control** | Force Relays | `ForceRelays` protects IP addresses (recommended). `AllowRelays` tries direct first. `NoRelays` exposes IPs. |
| **Auto Initialize** | true | Automatically initialize EOS and login on Start |
| **Default Max Players** | 4 | Default max players for created lobbies |
| **Lobby Bucket** | `v1` | Matchmaking version bucket. Different buckets can't see each other. |
| **Default Room Code** | (empty) | If empty, a random 4-digit join code is generated when hosting |
| **Heartbeat Timeout** | 5s | Seconds without packets before disconnecting a client |
| **Check Sanctions** | false | Check EOS Sanctions before accepting connections. Banned players are rejected. |
| **Offline Fallback** | false | Fall back to local offline mode if EOS initialization fails |
| **Auto Start On Lobby Join** | true | Automatically start FishNet when a lobby is joined (host or client) |

## Hosting a Game

Create a lobby and let the transport auto-start FishNet:

```csharp
using EOSNative.Lobbies;

// Get the lobby manager
var lobbyManager = EOSLobbyManager.Instance;

// Create a lobby (transport auto-starts as host if Auto Start is enabled)
await lobbyManager.CreateLobbyAsync("My Game", maxPlayers: 4);

// Share the join code with other players
string joinCode = lobbyManager.CurrentLobbyJoinCode;
Debug.Log($"Join code: {joinCode}");
```

## Joining a Game

Join using a lobby code:

```csharp
using EOSNative.Lobbies;

var lobbyManager = EOSLobbyManager.Instance;

// Join by code (transport auto-starts as client if Auto Start is enabled)
await lobbyManager.JoinLobbyByCodeAsync("ABC123");
```

## Player Identity

Add the `EOSNetworkPlayer` component to your player prefab to automatically track player identity:

```csharp
using FishNet.Transport.EOSNative;

// Get the local player
EOSNetworkPlayer local = EOSNetworkPlayer.LocalPlayer;
Debug.Log($"PUID: {local.Puid}");
Debug.Log($"Name: {local.DisplayName}");

// Iterate all players
foreach (var player in EOSNetworkPlayer.AllPlayers)
{
    Debug.Log($"{player.DisplayName} ({player.Puid})");
}
```

`EOSNetworkPlayer` syncs each player's ProductUserId (PUID) and display name to all clients via SyncVars.

## Network Events

Standard FishNet events work as expected:

```csharp
using FishNet;

// Server events
InstanceFinder.ServerManager.OnServerConnectionState += OnServerState;
InstanceFinder.ServerManager.OnRemoteConnectionState += OnClientConnected;

// Client events
InstanceFinder.ClientManager.OnClientConnectionState += OnClientState;
```

The transport also provides lobby-level events through `EOSLobbyManager`:

```csharp
var lobbyManager = EOSLobbyManager.Instance;
lobbyManager.OnLobbyCreated += (lobbyData) => { };
lobbyManager.OnLobbyJoined += (lobbyData) => { };
lobbyManager.OnMemberJoined += (puid) => { };
lobbyManager.OnMemberLeft += (puid) => { };
lobbyManager.OnOwnerChanged += (newOwnerPuid) => { };
```
