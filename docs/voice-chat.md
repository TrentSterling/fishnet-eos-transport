# Voice Chat

FishNet EOS Transport integrates with the EOS Voice system to provide spatial voice chat that is automatically wired to networked players.

## Setup

### 1. Add EOSVoiceManager

On your **EOSManager** GameObject, add the `EOSVoiceManager` component (from the eos-sdk package).

That is the only required setup step. The transport handles the rest automatically.

### 2. Automatic Wiring

When `EOSVoiceManager` is present in the scene, the transport automatically:

1. Sets `UseManualAudioOutput = true` on the voice manager (required for spatial audio)
2. Adds an `EOSVoicePlayer` component to each networked player object in `Awake`
3. Wires each voice player to the correct EOS participant using the player's PUID

No manual `AudioSource` setup or voice player assignment is needed.

### How Spatial Voice Works

Each `EOSVoicePlayer` component receives audio from its corresponding EOS voice participant and plays it through an `AudioSource` positioned at the player's transform. Unity's spatial audio system handles volume falloff based on distance from the listener.

The PUID (ProductUserId) matching ensures that each player's voice comes from their correct game object, so voice direction and distance are accurate.

## Player Prefab Requirements

Your player prefab needs:

- `NetworkObject` (FishNet) -- required for any networked object
- `EOSNetworkPlayer` -- syncs the player's PUID to all clients

The `EOSVoicePlayer` component is added automatically at runtime. You do not need to add it to the prefab.

## Muting

Players can mute/unmute their microphone through the `EOSVoiceManager`:

```csharp
using EOSNative.Voice;

var voiceManager = EOSVoiceManager.Instance;

// Mute local microphone
voiceManager.SetLocalMuted(true);

// Unmute
voiceManager.SetLocalMuted(false);

// Check mute state
bool isMuted = voiceManager.IsLocalMuted;
```

## Checking Voice State

You can check the current voice chat status at runtime:

```csharp
using EOSNative.Voice;

var voiceManager = EOSVoiceManager.Instance;

// Is voice connected?
bool connected = voiceManager.IsVoiceConnected;

// Get participant count
int participants = voiceManager.ParticipantCount;
```

## Troubleshooting

**Voice not working at all:**
- Verify `EOSVoiceManager` is in the scene and active
- Check that EOS is initialized and logged in before voice connects
- Check microphone permissions (especially on macOS/mobile)

**Voice works but is not spatial:**
- Verify `UseManualAudioOutput` is `true` on `EOSVoiceManager` (auto-set by transport)
- Check that `EOSVoicePlayer` was added to player objects (check Console for wiring logs)
- Verify `AudioListener` is on the local player's camera

**Voice only works for some players:**
- Check that all players have `EOSNetworkPlayer` with valid PUIDs
- Look for PUID mismatch warnings in the Console
