# SteamLink Module for VRCFT

The Implementation is **EXPERIMENTAL** and may change without notice causing app to fail.

## Dedicated Galaxy XR fork

This checkout is paired with the
`dedicated-gxr-facebridge-requiresmodule` Galaxy XR Steam Link APK. It maps
the five transported Galaxy XR tongue values to the VRCFT unified
expressions `TongueOut`, `TongueLeft`, `TongueRight`, `TongueUp`, and
`TongueDown`.

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
