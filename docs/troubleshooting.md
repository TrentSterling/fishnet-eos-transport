# Troubleshooting

## Installation Issues

### Compile errors after installing the transport

**FishNet is not installed yet.** The transport requires FishNet to compile its main assemblies. Install FishNet from the Asset Store or via UPM, and the transport will auto-activate.

If you are on a version older than v1.10.1, update the transport package. Older versions did not support install-order independence.

### "FISHNET_V4 not defined"

The `FishNetDetector` bootstrap script should add this automatically. If it did not:

1. Check **Edit > Project Settings > Player > Scripting Define Symbols** for your build target
2. Manually add `FISHNET_V4` if missing
3. Try **Assets > Reimport All** to trigger the detector
4. Restart Unity if needed

### Transport not showing in Add Component menu

- Verify FishNet is installed and compiling
- Check that `FISHNET_V4` is in your scripting defines
- Look for compile errors in the Console that might be blocking assembly loading

## Connection Issues

### Ghost lobby / connecting to dead host

**Symptoms**: Client joins a lobby but FishNet never connects, or connects to a host that's already gone.

**Cause**: EOS lobbies can linger in search results after all players leave ("ghost lobbies"). The eos-sdk package filters ghosts during search, but in rare edge cases a client could join one directly.

**Solution**: Update to v1.13.0+. The transport now validates every lobby before connecting FishNet — checking for 0 members, empty owner PUID, and host not in member list. Invalid lobbies are auto-left.

### Players cannot connect to the host

1. **Check EOS login** -- Both host and client must be logged into EOS. Look for login success logs in the Console.
2. **Verify lobby creation** -- The host must have an active lobby. Check `EOSLobbyManager.Instance.IsInLobby`.
3. **Check the join code** -- Make sure the client is using the correct join code from the host.
4. **Relay settings** -- If using `NoRelays`, firewalls or NAT may block direct connections. Use `ForceRelays` (default) for best compatibility.
5. **Lobby bucket mismatch** -- Host and client must use the same `Lobby Bucket` value on the transport.

### Connection drops or timeouts

- **Heartbeat timeout** -- If set too low, brief network hiccups will disconnect clients. Default is 5 seconds.
- **Connection timeout** -- Increase the `Timeout` value on the transport if clients need more time to connect.
- **EOS rate limits** -- EOS has per-user connection limits. Avoid rapid connect/disconnect cycles.

### "EOS SDK is in a corrupted state"

The EOS native DLL retains state across Unity play mode cycles. If the SDK enters a corrupted state:

1. **Restart Unity** -- This is the only reliable fix. The native DLL state is cleared on process restart.
2. To avoid this, exit play mode cleanly (do not force-stop or crash).

## Host Migration Issues

### Migration does not trigger when host disconnects

- Verify `HostMigrationManager` is on the NetworkManager GameObject
- Check that the lobby has `AllowHostMigration` enabled
- EOS must detect the host leaving -- this may take a few seconds

### Objects lose their state after migration

- For SyncVar preservation, use `HostMigratable` as the base class instead of `NetworkBehaviour`
- Ensure prefabs are registered in the `HostMigrationManager`'s Prefab Collection or on the spawner
- Check Console for "[HostMigratable] Prefab not found" warnings -- this means the prefab name doesn't match

### Players don't get their objects back after reconnecting

- The `HostMigrationPlayerSpawner` handles repossession automatically
- Check that the player's PUID matches (it should, since EOS identity is persistent)
- Enable debug logs on both HostMigrationManager and HostMigrationPlayerSpawner for detailed flow

## Voice Chat Issues

### Voice not working at all

- `EOSVoiceManager` must be in the scene and active
- EOS must be initialized and logged in before voice can connect
- Check microphone permissions (System Settings on macOS, app permissions on mobile)
- Look for voice initialization errors in the Console

### Voice works but is not spatial

- The transport auto-sets `UseManualAudioOutput = true` on EOSVoiceManager -- verify this is set
- Check that `EOSVoicePlayer` components were added to player objects (visible in Inspector during play)
- Ensure an `AudioListener` exists on the local player's camera

### Only some players have voice

- All players must have `EOSNetworkPlayer` with valid PUIDs
- Check Console for PUID mismatch warnings
- Verify all players are in the same EOS voice room (logs show join/leave)

## General Tips

### Enable debug logging

Most components have an **Enable Debug Logs** toggle in the Inspector. Enable these to see detailed flow information in the Console:

- `EOSNativeTransport` -- Connection and packet flow
- `HostMigrationManager` -- Migration state machine
- `HostMigrationPlayerSpawner` -- Spawn and repossession
- `HostMigratable` -- SyncVar save/restore

### Check the health check

If you are using TrontMCP, run the health check for a comprehensive system status:

```
run_health_check(testMode: "Solo")  // Single editor
run_health_check(testMode: "Duo")   // Two editors
```

## Version Compatibility

| Transport | EOS SDK | FishNet | Unity |
|---|---|---|---|
| v1.13.0 | v1.6.1 | v4.6+ | 2022.3+ |
| v1.12.0 | v1.6.0 | v4.6+ | 2022.3+ |
| v1.11.0 | v1.5.0+ | v4.6+ | 2022.3+ |
| v1.10.1 | v1.4.6 | v4.6+ | 6000.0+ |
| v1.10.0 | v1.4.4+ | v4.6+ | 6000.0+ |

Always use matching versions of the transport and eos-sdk packages for best compatibility. The transport's `package.json` declares its minimum eos-sdk dependency.
