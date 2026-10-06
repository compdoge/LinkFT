// RootedLinkFTModule.cs - VRCFaceTracking module for a rooted Galaxy XR.
//
// This module is a superset of the dedicated Galaxy XR SteamLink module:
//
//   * It keeps reading Steam Link's OSC stream on UDP 9015 for the 68 face
//     blendshapes, eyelids and combined gaze (unchanged behaviour, so face and
//     the five Galaxy XR tongue directions keep working exactly as before).
//
//   * It ALSO listens for the on-headset "GXRX" uplink on UDP 9016, which
//     carries the raw perception results that never reach OpenXR:
//       - true per-eye gaze  (fixes the Android XR combined-gaze duplication)
//       - the 2D pupil ellipse -> PupilDilation / PupilDiameterLeft/Right
//
// Only one VRCFT module can be active at a time, which is why the face path is
// folded in here rather than run as a second module.
//
// Pair this with:
//   - the dedicated Galaxy XR FaceBridge Steam Link APK (as before), and
//   - gxr-relayd (root) + eyecap (shell) on the headset, sending GXRX v1.

using System.Reflection;
using Microsoft.Extensions.Logging;
using VRCFaceTracking;
using VRCFaceTracking.Core.Params.Expressions;
using static VRCFaceTracking.Core.Params.Expressions.UnifiedExpressions;

namespace RootedLinkFT
{
    public class RootedLinkFTModule : ExtTrackingModule
    {
        // Steam Link OSC (same default as the dedicated module).
        private const int OSC_PORT = 9015;

        // GXRX uplink from the headset (Wi-Fi UDP or adb reverse).
        private const int GXRX_PORT = 9016;

        // Treat a frame as live if it arrived within this window.
        private const long FRESH_MS = 500;

        // Ellipse radius (0..1 of the camera image) -> millimetres. Provisional:
        // the true scale needs a calibration pass (see LIVE-PC-LINK-PLAN.md §6).
        // Diameter = 2 * max(axisA, axisB); VRCFT normalizes PupilDilation itself.
        private const float PupilMmScale = 32.0f;

        // Which pupil detections belong to which eye. UNKNOWN today (the four
        // detections all react to gaze), so both eyes average all detections and
        // PupilDilation works while the per-eye mapping is calibrated.
        private static readonly int[] LeftPupilIndices = { 0, 1, 2, 3 };
        private static readonly int[] RightPupilIndices = { 0, 1, 2, 3 };

        private OSCHandler OSCHandler = null!;
        private GxrxReceiver Gxrx = null!;

        private bool _hadGxrxGaze;
        private bool _hadGxrxPupil;

        public override (bool SupportsEye, bool SupportsExpression) Supported => (true, true);

        public override (bool eyeSuccess, bool expressionSuccess) Initialize(bool eyeAvailable, bool expressionAvailable)
        {
            ModuleInformation.Name = "Galaxy XR RootedLinkFT Module";

            var stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("RootedLinkFT.Assets.galaxy-xr.png");
            if (stream != null)
            {
                ModuleInformation.StaticImages = new List<Stream> { stream };
            }

            OSCHandler = new OSCHandler(Logger, OSC_PORT);

            try
            {
                Gxrx = new GxrxReceiver(Logger, GXRX_PORT);
            }
            catch (Exception e)
            {
                Logger.LogError("[RootedLinkFT] could not bind GXRX port {Port}: {Message}", GXRX_PORT, e.Message);
            }

            return (true, true);
        }

        private static float CalculateEyeOpenness(float fEyeClosedWeight, float fEyeTightener)
        {
            return 1.0f - Math.Clamp(fEyeClosedWeight + fEyeClosedWeight * fEyeTightener, 0.0f, 1.0f);
        }

        private static float GazeAngleX(float x, float z) => MathF.Atan2(x, -z);
        private static float GazeAngleY(float y, float z) => MathF.Atan2(y, -z);

        private void UpdateEyeTracking()
        {
            long now = Environment.TickCount64;

            // ---- gaze: prefer the true per-eye GXRX stream ----
            var gaze = Gxrx?.LatestGaze();
            bool gazeFresh = gaze != null && (now - Gxrx!.GazeLastLocalMs) <= FRESH_MS;

            if (gazeFresh)
            {
                if (!_hadGxrxGaze)
                {
                    _hadGxrxGaze = true;
                    Logger.LogInformation("[RootedLinkFT] true per-eye gaze streaming (GXRX)");
                }
                UnifiedTracking.Data.Eye.Left.Gaze.x = GazeAngleX(gaze!.LDir[0], gaze.LDir[2]);
                UnifiedTracking.Data.Eye.Left.Gaze.y = GazeAngleY(gaze.LDir[1], gaze.LDir[2]);
                UnifiedTracking.Data.Eye.Right.Gaze.x = GazeAngleX(gaze.RDir[0], gaze.RDir[2]);
                UnifiedTracking.Data.Eye.Right.Gaze.y = GazeAngleY(gaze.RDir[1], gaze.RDir[2]);
            }
            else
            {
                // Fall back to the combined Steam Link gaze (as before).
                float fAngleX = GazeAngleX(OSCHandler.eyeTrackData[0], OSCHandler.eyeTrackData[2]);
                float fAngleY = GazeAngleY(OSCHandler.eyeTrackData[1], OSCHandler.eyeTrackData[2]);
                UnifiedTracking.Data.Eye.Left.Gaze.x = fAngleX;
                UnifiedTracking.Data.Eye.Left.Gaze.y = fAngleY;
                UnifiedTracking.Data.Eye.Right.Gaze.x = fAngleX;
                UnifiedTracking.Data.Eye.Right.Gaze.y = fAngleY;
            }

            // ---- pupil diameter / dilation from the GXRX ellipse ----
            var pupil = Gxrx?.LatestPupil();
            bool pupilFresh = pupil != null && (now - Gxrx!.PupilLastLocalMs) <= FRESH_MS;
            if (pupilFresh)
            {
                if (!_hadGxrxPupil)
                {
                    _hadGxrxPupil = true;
                    Logger.LogInformation("[RootedLinkFT] pupil ellipse streaming (GXRX)");
                }

                float left = AverageDiameterMm(pupil!.Detections, LeftPupilIndices);
                float right = AverageDiameterMm(pupil.Detections, RightPupilIndices);
                UnifiedTracking.Data.Eye.Left.PupilDiameter_MM = left;
                UnifiedTracking.Data.Eye.Right.PupilDiameter_MM = right;
            }

            // ---- lids / openness from the Steam Link face stream (unchanged) ----
            float fLeftOpenness = CalculateEyeOpenness(OSCHandler.eyelids[0], OSCHandler.ueData[EyeSquintLeft]);
            float fRightOpenness = CalculateEyeOpenness(OSCHandler.eyelids[1], OSCHandler.ueData[EyeSquintRight]);
            UnifiedTracking.Data.Eye.Left.Openness = fLeftOpenness;
            UnifiedTracking.Data.Eye.Right.Openness = fRightOpenness;
        }

        private static float AverageDiameterMm(GxrxPupilDetection[] detections, int[] indices)
        {
            if (detections.Length == 0 || indices.Length == 0) return 0f;
            float sum = 0f;
            int n = 0;
            foreach (int i in indices)
            {
                if (i < 0 || i >= detections.Length) continue;
                var d = detections[i];
                sum += 2.0f * MathF.Max(d.A, d.B);
                n++;
            }
            return n == 0 ? 0f : (sum / n) * PupilMmScale;
        }

        private void UpdateFaceTracking()
        {
            foreach (KeyValuePair<UnifiedExpressions, float> entry in OSCHandler.ueData)
            {
                UnifiedTracking.Data.Shapes[(int)entry.Key].Weight = entry.Value;
            }

            // Lip suck compensation (kept from the dedicated module).
            UnifiedTracking.Data.Shapes[(int)LipSuckUpperLeft].Weight =
                Math.Min(1.0f - (float)Math.Pow(UnifiedTracking.Data.Shapes[(int)MouthUpperLeft].Weight, 1f / 6f),
                         UnifiedTracking.Data.Shapes[(int)LipSuckUpperLeft].Weight);
            UnifiedTracking.Data.Shapes[(int)LipSuckUpperRight].Weight =
                Math.Min(1.0f - (float)Math.Pow(UnifiedTracking.Data.Shapes[(int)MouthUpperRight].Weight, 1f / 6f),
                         UnifiedTracking.Data.Shapes[(int)LipSuckUpperRight].Weight);
        }

        public override void Update()
        {
            Thread.Sleep(10);
            UpdateEyeTracking();
            UpdateFaceTracking();
        }

        public override void Teardown()
        {
            OSCHandler?.Teardown();
            Gxrx?.Dispose();
        }
    }
}
