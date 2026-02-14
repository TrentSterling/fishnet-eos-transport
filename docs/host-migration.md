# Host Migration

Host migration allows the game to continue seamlessly when the host disconnects. A new host is automatically selected and all networked object state is preserved.

## Setup

### 1. Add HostMigrationManager

On your **NetworkManager** GameObject, add the `HostMigrationManager` component.

Configure it in the Inspector:

| Setting | Description |
|---|---|
| **Prefab Collection** | A FishNet `PrefabObjects` asset containing all prefabs that should survive migration. The player prefab is auto-registered from the spawner. |
| **Enable Debug Logs** | Toggle verbose migration logging in the Console |

### 2. Add HostMigrationPlayerSpawner

On the same GameObject, add `HostMigrationPlayerSpawner` and assign your player prefab:

| Setting | Default | Description |
|---|---|---|
| **Player Prefab** | -- | The player prefab to spawn. Should have `NetworkObject` at minimum. |
| **Add To Default Scene** | true | Add spawned players to the default FishNet scene |
| **Spawn Position** | (0, 5, 0) | Default spawn position for new players |
| **Use Random Spawn Offset** | true | Randomize spawn position within a radius |
| **Random Spawn Radius** | 5 | Radius for random spawn offset |

### 3. Use HostMigratable for SyncVar Preservation

For objects that need their SyncVar data preserved across migration, extend `HostMigratable` instead of `NetworkBehaviour`:

```csharp
using FishNet.Object.Synchronizing;
using FishNet.Transport.EOSNative.Migration;

public class MyGameState : HostMigratable
{
    public readonly SyncVar<int> Score = new();
    public readonly SyncVar<string> TeamName = new();
    public readonly SyncVar<float> Health = new(new SyncTypeSettings(100f));
}
```

All `SyncVar` fields on a `HostMigratable` object (and its children) are automatically saved and restored during migration. No manual serialization code needed.

### 4. Exclude Objects from Migration

Add the `DoNotMigrate` component to any NetworkObject that should NOT be preserved during migration (e.g., temporary projectiles, VFX):

```csharp
// In the editor: Add Component > DoNotMigrate
// Or in code:
gameObject.AddComponent<DoNotMigrate>();
```

## How It Works

When the host disconnects:

1. **Detection** -- EOS detects the host left the lobby
2. **New Owner** -- EOS promotes a new lobby owner (automatic)
3. **State Save** -- All tracked NetworkObjects have their state saved (position, rotation, SyncVars)
4. **Server Start** -- The new host starts a FishNet server
5. **Object Restoration** -- Saved objects are re-spawned with their preserved state
6. **Client Reconnect** -- Other clients automatically reconnect to the new host (with retry logic)
7. **Repossession** -- When a player reconnects, their objects are returned to them

### Migration Flow (New Host)

```
Host disconnects
  -> Lobby owner changes to us
  -> Save all tracked object states
  -> Stop client connection
  -> Stop old server (if any)
  -> Reset scene NetworkObjects
  -> Start new server
  -> Restore saved objects (spawn + load SyncVars)
  -> Start client (host mode)
  -> Migration complete
```

### Migration Flow (Clients)

```
Host disconnects
  -> Lobby owner changes
  -> Save states preemptively
  -> Stop client connection
  -> Wait for new host to be ready (~1.5s)
  -> Reconnect to new host (up to 5 retries with backoff)
  -> Migration complete
```

## Auto-Tracking vs HostMigratable

The migration system supports two modes:

### Auto-Tracking (Default)
All spawned `NetworkObject` instances are automatically tracked for migration -- no components needed. Basic state (position, rotation, SyncVars) is saved and restored via reflection. Objects with `DoNotMigrate` are excluded.

### HostMigratable (Advanced)
For more control, add the `HostMigratable` component. It provides:
- Continuous state caching (captures SyncVars in `Update`, so data is preserved even if FishNet clears SyncVars before `OnDisable`)
- Owner PUID tracking via a dedicated SyncVar
- Repossession support (deactivated objects are returned to reconnecting players)

Both modes work together -- auto-tracked objects and `HostMigratable` objects can coexist.

## Events

```csharp
var migrationManager = HostMigrationManager.Instance;

// Fired when migration starts (old host left, we're handling it)
migrationManager.OnMigrationStarted += () =>
{
    Debug.Log("Migration in progress...");
};

// Fired when migration completes (objects restored, connections ready)
migrationManager.OnMigrationCompleted += () =>
{
    Debug.Log("Migration complete!");
};
```

## Testing with ParrelSync

[ParrelSync](https://github.com/VeriorPies/ParrelSync) lets you run multiple Unity editor instances for testing:

1. Open **ParrelSync > Clones Manager**
2. Create a clone
3. Open the clone
4. In the main editor: create a lobby
5. In the clone: join using the lobby code
6. Close the main editor (or stop play mode) to trigger migration
7. The clone should become the new host with all state preserved

## Debugging

Enable **Enable Debug Logs** on the `HostMigrationManager` to see detailed migration flow in the Console.

In Play Mode, the Inspector shows live stats:
- **Auto-Tracked Objects** -- Currently tracked spawned objects
- **Legacy (HostMigratable)** -- Objects using the HostMigratable component
- **Saved States** -- Number of states saved for restoration
- **Registered Prefabs** -- Prefabs available for re-spawning
- **Pending Auto-Repossess** -- Objects waiting for their owner to reconnect

Use the **Save States** and **Finish Migration** buttons in the Inspector to manually test the migration flow without actually disconnecting.
