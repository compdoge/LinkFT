# SteamLink Module for VRCFT

The Implementation is **EXPERIMENTAL** and may change without notice causing app to fail.

## Dedicated Galaxy XR fork

This checkout is paired with the
`dedicated-gxr-facebridge-requiresmodule` Galaxy XR Steam Link APK. It maps
the five transported Galaxy XR tongue values to the VRCFT unified
expressions `TongueOut`, `TongueLeft`, `TongueRight`, `TongueUp`, and
`TongueDown`.

## Gaze fix

Eyes could twitch when looking toward the edge. `GazeFix.cs` converts the
Steam Link gaze vector (`/sl/eyeTrackedGazePoint`) the way the
[Galaxy XR Virtual Desktop fix](https://github.com/SinonVT/GalaxyXR-EyeTracking-Fix-Virtual-Desktop)
does:

- Pitch is the elevation `atan2(y, sqrt(x² + z²))` instead of `atan2(y, -z)`.
  The old pitch grew as `1/cos(yaw)`, so near the left/right edge vertical
  noise was amplified (5° up read as 10° at 60° yaw). Yaw is unchanged.
- Zero, NaN, exactly centred (identity pose) and backwards vectors are
  rejected and the last good gaze is held. A zero vector used to produce
  `atan2(0, -0) = π` on both axes, for example before the first packet.
- Output is clamped to ±1.0. VRCFT's binary eye parameters
  (`EyeLeftX1/X2/X4`...) do not clamp, so values past 1.0 wrapped and flicked
  the eyes between the edge and the centre.

Head roll does not leak into gaze on this path: Steam Link's PC driver forwards
the headset's gaze vector unchanged, so the quaternion problem fixed in the
Virtual Desktop module does not apply here.

Once a second, if anything was rejected, clamped or jumped by more than
0.5 rad, the module logs a `[GazeFix]` warning with the counts and the raw
vector. Check VRCFT's Output page after reproducing a glitch.

## Install

- Build or use the dedicated module ZIP packaged beside this source tree.
- In VRCFT, go to Modules and install it from the ZIP.
- Do not install the stock LinkFT release over this dedicated module.


## Setup

- **DO NOT CHANGE VRCFT NETWORK SETTINGS**

- In VR you will need to change a few settings in SteamLink, you will need to have "Advanced Settings" to show
  - Enable OSC - on
  - Share eye tracking data... - on
  - Share Face tracking data... - on
  - OSC Output Port - 9015 (Custom: VRCFT,Etc.)

<img src="docs/steamlinksettings.png" width="600" alt="Root Page">
