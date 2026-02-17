# Changelog

## v1.13.0

- **Ghost lobby defense at transport level** -- new `ValidateHostBeforeConnect` method checks for ghost lobbies (0 members) and stale hosts (owner PUID not in member list) before connecting FishNet. Auto-leaves invalid lobbies and returns `NotFound` instead of connecting to dead hosts.
- **ValidateHostBeforeConnect refactor** -- replaces 7 inline `string.IsNullOrEmpty(lobby.OwnerPuid)` checks across `JoinByCode`, `JoinByLobbyName`, `QuickMatch`, `QuickMatchOrHost`, `JoinByGameMode`, and `AutoStartOnLobbyJoin` with a single validation method.
- **Better join logging** -- all join paths now log member counts alongside owner PUIDs for easier debugging.

## v1.12.0

- **Fix double-start race** -- prevent FishNet from starting twice when lobby join and auto-start overlap
- **Connection timeout** -- configurable timeout for client connections (default 25s)
- **Always-on logging** -- transport logs are no longer gated behind debug toggle for critical paths

## v1.11.0

- **Robust host migration** -- migration no longer gets stuck in limbo when reconnect fails
- **Watchdog timer** (45s default, configurable) -- forces migration completion if any step hangs, preventing `IsMigrating` from staying true forever
- **Failure cleanup** -- on migration failure, stops FishNet and leaves EOS lobby automatically (configurable via `_leaveLobbyOnFailure` / `_stopFishNetOnFailure` inspector toggles)
- **Expanded reconnect retries** -- 10 attempts over ~36s (was 5 over ~15s), delays: `[1.5, 2, 2.5, 3, 3.5, 4, 4.5, 5, 5, 5]`
- **Lobby validation** -- checks `IsInLobby` before each reconnect attempt; fails fast with `LobbyMembershipLost` if kicked
- **Server start failure handling** -- host-side migration now handles `OnServerConnectionState(Stopped)` gracefully instead of hanging
- **New `OnMigrationFinished(MigrationFinishedArgs)` event** -- detailed result with `MigrationResult` enum (`Success`, `ReconnectFailed`, `ServerStartFailed`, `WatchdogTimeout`, `LobbyMembershipLost`, `Cancelled`), elapsed time, attempt count, and whether we were the new host
- **`OnMigrationCompleted` preserved** -- still fires for backward compatibility
- **Public `CancelMigration()` method** + inspector button
- **`MigrationResult.cs`** -- new file with `MigrationResult` enum and `MigrationFinishedArgs` struct
- **EOSAutoReconnect migration guard** -- skips auto-reconnect when `HostMigrationManager.IsMigrating` is true, preventing conflicting reconnect attempts

## v1.10.1

- **Install-order support** -- Transport compiles cleanly without FishNet installed
- `FISHNET_V4` defineConstraint on all assembly definitions (Runtime, Editor, Tests)
- `Bootstrap/FishNetDetector` auto-detects `FishNet.Runtime` assembly and manages scripting defines
- Zero compile errors when installing transport before FishNet

## v1.10.0

- **Auto-enable spatial voice** when `EOSVoiceManager` is present in the scene
- `EOSVoicePlayer` auto-added to networked players in `Awake`
- Transport auto-sets `UseManualAudioOutput = true` on voice manager
- Health check verifies voice connection and player wiring

## v1.9.9

- Fix FishNet not auto-starting after host leaves and rejoins lobby

## v1.9.8

- Fix host-leave not stopping client FishNet or triggering migration
- Extract `PuidUtils` shared utility for PUID normalization
- Comprehensive PUID normalization tests

## v1.9.7

- Wire `EOSVoicePlayer` PUID from `EOSNetworkPlayer` SyncVar callback
- Improved voice player identity matching
