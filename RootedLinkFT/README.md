# RootedLinkFT — VRCFaceTracking module for a rooted Galaxy XR

`RootedLinkFT` reads the on-headset perception stack directly (over the "GXRX"
uplink) and adds the parameters that never reach OpenXR, while still accepting
the Steam Link OSC stream as a fallback.

## What it provides over OpenXR / the dedicated SteamLink module

| Unified Expression | Source |
|---|---|
| `v2/EyeLeftX/Y`, `v2/EyeRightX/Y` | **true per-eye** gaze directions from the host DNN tensor (fixes the Android XR combined-gaze duplication) |
| `v2/PupilDilation`, `v2/PupilDiameterLeft/Right/Diameter` | 2D pupil ellipse from the eye host plugin |
| `TongueRoll`, `TongueArchY`, `TongueShape`, `TongueTwistLeft/Right` | 11-D `predicted_local_tongue_expression` |
| the 68 face blendshapes | the face host plugin (same set OpenXR exposes) — so face works **without Steam Link** |

Face, lids and the five Galaxy XR tongue directions still arrive from Steam Link
OSC on port 9015 and are used as a **fallback** when no fresh GXRX frame has
arrived, so the module works with Steam Link, Virtual Desktop, ALXR, WiVRn, or no
streamer at all.

> **Only one VRCFT module can be active at a time.** That is why face/tongue are
> folded in here instead of running alongside the dedicated module.

## Data flow

```
gxr_relayd (root, read-only uprobes)  --GXRX UDP 9026--> [GxrxReceiver] --+
                                                                          |
Steam Link (headset) --OSC 9015--> [OSCHandler] (fallback) --------------+--> UnifiedTracking
```

The module is the receiver: VRCFT loads `RootedLinkFT.dll` in-process and the
module opens its own UDP socket (port 9026, all interfaces). There is no separate
PC receiver application. The per-eye gaze comes from a uprobe on the host DNN
tensor, so there is no vendor-client process and no mount-namespace dance — the
headset side is a single root process.

## Build

Needs the .NET 7 SDK (the module targets `net7.0`), matching VRCFaceTracking's
`VRCFaceTracking.Core` submodule. The submodule is already checked out in this
fork.

```powershell
dotnet build RootedLinkFT\RootedLinkFT.sln -c Release
```

To drop it straight into VRCFaceTracking:

```powershell
dotnet build RootedLinkFT\RootedLinkFT.sln -c Release -p:InstallVRCFTModule=true
```

which copies `RootedLinkFT.dll` + `module.json` into
`%APPDATA%\VRCFaceTracking\CustomLibs\b7c2f9e4-3a61-4d58-9c0e-7f2a1d6b4e90\`.
Otherwise build the ZIP by packaging that DLL + `module.json`.

## Requirements

* Rooted Galaxy XR (GhostLock is enough; no Magisk needed on the headset side).
* `gxr_relayd` running as root, sending GXRX to this PC on UDP 9026 (Wi-Fi, or
  `adb reverse tcp:9026 tcp:9026`).
* Steam Link OSC on port 9015 is **optional** (fallback only).

## Optional overrides — `rootedlinkft.json`

Place a JSON file **next to this DLL** (in the installed module folder) to tune:

```json
{ "pupilMmScale": 24, "pupilDilationGain": 0.75 }
```

| key | meaning |
|---|---|
| `pupilMmScale` | ellipse radius → mm for `PupilDiameter*` (default 24) |
| `pupilDilationGain` | scales the normalized `PupilDilation` amplitude; 0.75 = 25% smaller (default 0.75) |

## Known limitations / TODO

* **Pupil detection -> eye mapping is unknown.** All four detections react to
  gaze, so both eyes currently average all four (`LeftPupilIndices` /
  `RightPupilIndices`). `PupilDilation` is therefore correct today; the per-eye
  `PupilDiameterLeft/Right` split needs a calibration pass.
* **mm scale is provisional.** Relative dilation is unaffected (VRCFT normalizes
  it).
* Face/tongue falls back to the Steam Link OSC values when no fresh GXRX frame
  arrives within 500 ms; gaze falls back per-eye to the combined OSC gaze.

## Protocol

See `GxrxProtocol.cs` for the GXRX v1 wire format:
type 2 pupil, type 3 face (68 blendshapes), type 4 tongue (11-D),
type 5 per-eye gaze directions.
