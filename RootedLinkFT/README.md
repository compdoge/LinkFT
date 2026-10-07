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
gxr-relayd (root)  --GXRX UDP 9026--> [GxrxReceiver] ------+--> UnifiedTracking
eyecap (shell)     --GXRX UDP 9026-->                       |
```

The module is the receiver: VRCFT loads `RootedLinkFT.dll` in-process and the
module opens its own UDP socket (port 9026, all interfaces). There is no separate
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
  to this PC on UDP 9026 (Wi-Fi, or `adb reverse tcp:9026 tcp:9026`).
* Steam Link OSC on port 9015, as with the dedicated module.

## Per-user convergence (IPD) calibration

The eye service does not expose vergence in the per-eye gaze *directions*; it
shows up in the reported **eye-position separation (IPD)**. The module turns
that into per-eye inward rotation, normalized so it works across different
wearers/headset fits:

```
neutral = running max IPD, seeded from the first plausible frame   (per wearer)
rel     = (neutral - ipd) / neutral                # fractional IPD loss
verg    = clamp(rel / fullScaleFrac, 0, 1) * maxVergenceRad
EyeLeftX = commonX + verg ;  EyeRightX = commonX - verg
```

Because the response is a *fraction* of the wearer's own baseline, a 58 mm and a
72 mm user get the same rotation for the same relative convergence. The baseline
never decays, so it can't sink to the working distance.

### Optional overrides — `rootedlinkft.json`

Place a JSON file **next to this DLL** (in the installed module folder) to force
values, e.g. for an unusual fit or a hand calibration:

```json
{
  "neutralIpdMm": 64.5,
  "fullScaleFrac": 0.24,
  "maxVergenceRad": 0.33,
  "deadzoneMm": 0.5
}
```

| key | meaning |
|---|---|
| `neutralIpdMm` | force the baseline (0 / omit = auto) |
| `fullScaleFrac` | fractional IPD loss treated as a full cross (default 0.24) |
| `maxVergenceRad` | per-eye rotation at full cross (default 0.33 rad) |
| `deadzoneMm` | ignore losses below this (default 0.5 mm) |

To calibrate by hand: have the wearer look far, read `neutral=…mm` from the
1 Hz log line, then have them cross hard and read the working `verg`. Set
`neutralIpdMm` to the far value and tune `fullScaleFrac`/`maxVergenceRad` if the
cross is too strong/weak.

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
