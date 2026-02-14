# Changelog

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
