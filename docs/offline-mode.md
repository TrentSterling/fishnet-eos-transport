# Offline Mode

Offline mode provides a local networking fallback when EOS is unavailable. This lets your game run in singleplayer without any code changes.

## When Does It Activate?

Offline mode activates automatically when:

- There is no internet connection
- EOS SDK initialization fails
- EOS login fails
- EOS P2P is otherwise unavailable

The transport detects the failure and seamlessly routes all packets through local memory instead of the network.

## Enabling Offline Fallback

On the `EOSNativeTransport` component, enable **Offline Fallback**:

| Setting | Default | Description |
|---|---|---|
| **Offline Fallback** | false | When enabled, falls back to offline mode if EOS initialization or login fails |

When disabled, EOS failures will prevent networking from starting at all.

## How It Works

In offline mode, the transport creates a local server and client pair (`EOSOfflineServer` and `EOSOfflineClient`) that pass packets directly through memory queues. From FishNet's perspective, it looks like a normal host connection:

- `InstanceFinder.IsServerStarted` returns `true`
- `InstanceFinder.IsClientStarted` returns `true`
- NetworkObjects spawn and sync normally
- SyncVars, RPCs, and all FishNet features work

The only difference is that there is only one player and no actual network traffic.

## Game Code Compatibility

Your game code does not need to change for offline mode. Everything works through FishNet's standard APIs:

```csharp
// This works identically in online and offline mode
[ServerRpc]
private void CmdDoSomething()
{
    // Runs on server (which is local in offline mode)
}

[ObserversRpc]
private void RpcNotifyAll()
{
    // Runs on all clients (just the local player in offline mode)
}
```

## Detecting Offline Mode

If you need to show different UI or disable multiplayer features:

```csharp
var transport = FindAnyObjectByType<EOSNativeTransport>();

// Check if we're running in offline mode
// (EOS is not available, using local fallback)
bool isOffline = !EOSManager.Instance.IsLoggedIn;
```

## Use Cases

- **Development** -- Test gameplay without EOS credentials or internet
- **Singleplayer campaigns** -- Ship a game that works offline with the same networking code
- **Graceful degradation** -- If EOS goes down, players can still play solo
- **CI/CD testing** -- Run automated tests in headless mode without network dependencies
