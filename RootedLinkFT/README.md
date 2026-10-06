# RootedLinkFT — VRCFaceTracking module for a rooted Galaxy XR

`RootedLinkFT` is a superset of the dedicated Galaxy XR SteamLink VRCFT module.
It keeps the whole Steam Link face/eye path intact and adds the parameters that
only the rooted perception stack can provide.

## What it adds over the dedicated SteamLink module

| Unified Expression | Source |
|---|---|
| `v2/PupilDilation` | 2D pupil ellipse from the eye host plugin, via the GXRX uplink |
| `v2/PupilDiameterLeft` / `PupilDiameterRight` / `PupilDiameter` | same (mm scale is provisional) |
| `v2/EyeLeftX/Y`, `v2/EyeRightX/Y` | true per-eye gaze from `eyecap` (fixes the Android XR combined-gaze duplication) |

Face, lids and the five Galaxy XR tongue directions continue to arrive from
Steam Link OSC on port 9015 and are unchanged.

> **Only one VRCFT module can be active at a time.** That is why face/tongue are
> folded in here instead of running alongside the dedicated module.

## Data flow

```
Steam Link (headset) --OSC 9015--> [OSCHandler] -----------+
                                                            |
gxr-relayd (root)  --GXRX UDP 9016--> [GxrxReceiver] ------+--> UnifiedTracking
eyecap (shell)     --GXRX UDP 9016-->                       |
```

The module is the receiver: VRCFT loads `RootedLinkFT.dll` in-process and the
module opens its own UDP socket (port 9016, all interfaces). There is no separate
PC receiver application.

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
* `gxr-relayd` running as root, `eyecap` running as `shell`, both sending GXRX
  to this PC on UDP 9016 (Wi-Fi, or `adb reverse tcp:9016 tcp:9016`).
* Steam Link OSC on port 9015, as with the dedicated module.

## Known limitations / TODO

* **Pupil detection -> eye mapping is unknown.** All four detections react to
  gaze, so both eyes currently average all four (`LeftPupilIndices` /
  `RightPupilIndices`). `PupilDilation` is therefore correct today; the per-eye
  `PupilDiameterLeft/Right` split needs the calibration pass in
  `LIVE-PC-LINK-PLAN.md`.
* **mm scale is provisional** (`PupilMmScale = 32`). Relative dilation is
  unaffected (VRCFT normalizes it).
* If no GXRX gaze/pupil arrives within 500 ms the module falls back to the
  Steam Link values (gaze) or leaves the last pupil value.

## Protocol

See `GxrxProtocol.cs` for the GXRX v1 wire format (header + gaze/pupil payloads).
