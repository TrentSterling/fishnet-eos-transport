# Demo Scene

The transport package includes menu items to quickly build a complete demo scene for testing multiplayer gameplay.

## Menu Items

All demo tools are under **Tools > FishNet EOS** in the Unity menu bar.

### Build Demo Scene

**Tools > FishNet EOS > Build Demo Scene**

Creates a complete test scene with:

- **Ground plane** -- Large flat surface for gameplay
- **Walls** -- Boundary walls around the play area
- **Camera** -- A simple follow camera
- **NetworkManager** -- Configured with `EOSNativeTransport`
- **EOSManager** -- EOS initialization and login
- **HostMigrationManager** -- Host migration support
- **HostMigrationPlayerSpawner** -- Automatic player spawning
- **DemoCrateSpawner** -- Spawns physics crates for testing networked interactions

### Build Demo Prefabs

**Tools > FishNet EOS > Build Demo Prefabs**

Creates player and object prefabs in `Assets/EOSDemo/Prefabs/`:

- **PlayerBall** -- A spherical player with physics-based movement (WASD/arrow keys), `EOSNetworkPlayer` for identity tracking, and `HostMigratable` for migration support
- **Crate** -- A physics crate with `NetworkPhysicsObject` for synced rigidbody state

### Setup Scene

**Tools > FishNet EOS > Setup Scene**

Creates a minimal scene with just the essentials:

- **NetworkManager** with `EOSNativeTransport`
- **EOSManager**

Use this when you want to build your own scene but need the networking foundation.

### Delete Demo Assets

**Tools > FishNet EOS > Delete Demo Assets**

Removes the `Assets/EOSDemo/` folder and all generated demo content. Use this to clean up after testing.

## Using the Demo

### Single Player Test

1. Run **Tools > FishNet EOS > Build Demo Scene**
2. Assign your **EOSConfig** asset to the transport (if not already set)
3. Enter Play Mode
4. A lobby is created automatically
5. Your player ball spawns -- move with WASD or arrow keys
6. Crates spawn periodically -- push them around

### Multiplayer Test with ParrelSync

1. Build the demo scene in the main editor
2. Open **ParrelSync > Clones Manager** and create/open a clone
3. In the **main editor**: Enter Play Mode (creates a lobby automatically)
4. Note the join code in the Console
5. In the **clone editor**: Enter Play Mode and join using the code
6. Both players should see each other and can interact with crates

### Host Migration Test

1. Set up a two-player session (see above)
2. Stop Play Mode in the main editor (simulates host disconnect)
3. The clone should automatically become the new host
4. Player positions, scores, and crate positions are preserved
5. Re-enter Play Mode in the main editor and rejoin -- your objects are returned

## Demo Components

### PlayerBall

A physics-based player controller:

- `HostMigratable` base class for migration support
- `EOSNetworkPlayer` for PUID and display name
- SyncVars for score and other game state
- WASD movement with physics forces

### NetworkPhysicsObject

A synced physics object (used by crates):

- Rigidbody state synced from server to clients
- Works with host migration (position/rotation preserved)

### PhysicsNetworkTransform

Lightweight physics transform sync:

- Syncs position and rotation for rigidbody objects
- Server-authoritative

### SimpleCamera

A basic follow camera:

- Attaches to the local player
- Smooth follow with offset

### DemoCrateSpawner

Periodically spawns crate prefabs:

- Server-only spawning
- Configurable spawn interval and max count
