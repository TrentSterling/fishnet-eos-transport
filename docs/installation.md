# Installation

## Package Dependencies

FishNet EOS Transport requires two other packages:

1. **[eos-sdk](https://github.com/TrentSterling/eos-sdk)** -- EOS SDK wrapper for Unity
2. **[FishNet](https://github.com/FirstGearGames/FishNet)** -- Networking framework (Asset Store or UPM)

## Install via Unity Package Manager

Open your project's `Packages/manifest.json` and add both packages:

```json
{
  "dependencies": {
    "com.tront.eos-sdk": "https://github.com/TrentSterling/eos-sdk.git",
    "com.tront.fishnet-eos-transport": "https://github.com/TrentSterling/fishnet-eos-transport.git"
  }
}
```

Or add them one at a time through the Unity Editor:

1. Open **Window > Package Manager**
2. Click **+** > **Add package from git URL...**
3. Enter `https://github.com/TrentSterling/eos-sdk.git` and click **Add**
4. Repeat with `https://github.com/TrentSterling/fishnet-eos-transport.git`

## Install FishNet

FishNet can be installed from the **Unity Asset Store** (most common) or via UPM. The transport auto-detects FishNet regardless of how it was installed.

## Install-Order Support (v1.10.1+)

As of v1.10.1, the transport compiles cleanly even if FishNet is not yet installed. You can install packages in any order without encountering compile errors.

### How It Works

The transport uses `FISHNET_V4` as a `defineConstraint` on all three assembly definitions (Runtime, Editor, Tests). This means the assemblies simply skip compilation when the define is absent.

A bootstrap script (`Editor/Bootstrap/FishNetDetector.cs`) runs automatically via `[InitializeOnLoad]`. It:

1. Scans loaded assemblies for `FishNet.Runtime`
2. If found, adds the `FISHNET_V4` scripting define to Player Settings
3. If FishNet is removed, it cleans up the define

This means:

- **Install transport first, FishNet second** -- zero compile errors, transport activates automatically
- **Install FishNet first, transport second** -- also works, detector fires on next domain reload

## Verify Installation

After installing all three packages (eos-sdk, transport, FishNet), check the Unity Console for:

```
[FishNet EOS Transport] FishNet detected — added FISHNET_V4 scripting define. Transport will compile on next refresh.
```

You should also see `EOS Native Transport` available in the **Add Component** menu on any GameObject, under **FishNet > Transport > EOS Native Transport**.

## Updating

To update to the latest version, remove and re-add the git URL, or use the **Update** button in the Package Manager window.

For a specific version, append a tag to the git URL:

```
https://github.com/TrentSterling/fishnet-eos-transport.git#v1.10.1
```
