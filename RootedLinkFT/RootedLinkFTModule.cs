// RootedLinkFTModule.cs - VRCFaceTracking module for a rooted Galaxy XR.
//
// This module is a superset of the dedicated Galaxy XR SteamLink module:
//
//   * It keeps reading Steam Link's OSC stream on UDP 9015 for the 68 face
//     blendshapes, eyelids and combined gaze (unchanged behaviour, so face and
//     the five Galaxy XR tongue directions keep working exactly as before).
//
//   * It ALSO listens for the on-headset "GXRX" uplink on UDP 9026, which
//     carries the raw perception results that never reach OpenXR:
//       - true per-eye gaze  (fixes the Android XR combined-gaze duplication)
//       - the 2D pupil ellipse -> PupilDilation / PupilDiameterLeft/Right
//
// Only one VRCFT module can be active at a time, which is why the face path is
// folded in here rather than run as a second module.
//
// Pair this with:
//   - the dedicated Galaxy XR FaceBridge Steam Link APK (as before), and
//   - gxr_relayd (root) on the headset, sending GXRX v1. The true per-eye gaze
//     comes from a read-only uprobe on the host DNN tensor, so there is no
//     vendor-client process and no mount-namespace dance.

using System.Reflection;
using System.Text.Json;
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
        private const int GXRX_PORT = 9026;

        // Treat a frame as live if it arrived within this window.
        private const long FRESH_MS = 500;

        // Ellipse radius (0..1 of the camera image) -> millimetres. Provisional
        // scale; default 24 is the tuned value (see LIVE-PC-LINK-PLAN.md §6).
        private float _pupilMmScale = 24.0f;

        // VRCFT derives v2/PupilDilation by normalizing between the min/max it
        // observes, so uniform mm scaling cancels out. To shrink the visible
        // dilation amplitude we widen that min/max window by 1/gain. 0.75 => 25%
        // smaller swings. (v2/PupilDiameter* are absolute mm, so _pupilMmScale
        // directly sets their magnitude.)
        private float _pupilDilationGain = 0.75f;
        private float _pupilMinMm = float.MaxValue;
        private float _pupilMaxMm = float.MinValue;

        // Gaze comes straight from the host DNN's true per-eye directions (GXRX
        // type 5). No synthesis: each eye's x/y is atan2 of its own direction
        // vector, so convergence is real and needs no per-user calibration.
        private const bool GazeDebug = true;         // 1 Hz gaze-source log
        private long _lastGazeLogMs;

        // Which pupil detections belong to which eye. UNKNOWN today (the four
        // detections all react to gaze), so both eyes average all detections and
        // PupilDilation works while the per-eye mapping is calibrated.
        private static readonly int[] LeftPupilIndices = { 0, 1, 2, 3 };
        private static readonly int[] RightPupilIndices = { 0, 1, 2, 3 };

        private OSCHandler OSCHandler = null!;
        private GxrxReceiver Gxrx = null!;
        private TongueMap? _tongueMap;

        // Tongue frames older than this fall back to the Steam Link values.
        private const long TONGUE_FRESH_MS = 500;
        // Flip if the avatar's roll/twist come out mirrored.
        private static readonly bool TwistPositiveIsRight = true;

        private bool _hadGxrxGaze;
        private bool _hadGxrxPupil;
        private bool _hadFace;
        private bool _hadTongue;
        private readonly float[] _faceClosed = new float[2];
        private bool _faceEyelidsValid;

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

            try
            {
                using var ts = Assembly.GetExecutingAssembly()
                    .GetManifestResourceStream("RootedLinkFT.tongue-map.json");
                if (ts != null)
                {
                    _tongueMap = new TongueMap(ts);
                    Logger.LogInformation("[RootedLinkFT] tongue map loaded");
                }
                else
                {
                    Logger.LogWarning("[RootedLinkFT] embedded tongue-map.json not found");
                }
            }
            catch (Exception e)
            {
                Logger.LogError("[RootedLinkFT] tongue map load failed: {Message}", e.Message);
            }

            LoadConfig();

            return (true, true);
        }

        private static float CalculateEyeOpenness(float fEyeClosedWeight, float fEyeTightener)
        {
            return 1.0f - Math.Clamp(fEyeClosedWeight + fEyeClosedWeight * fEyeTightener, 0.0f, 1.0f);
        }

        // Steam Link OSC hands us a gaze *point* in the usual -z-forward frame.
        private static float GazeAngleX(float x, float z) => MathF.Atan2(x, -z);
        private static float GazeAngleY(float y, float z) => MathF.Atan2(y, -z);

        // The GXRX per-eye direction is in the device frame: +z forward, +x right,
        // +y DOWN (see perception-hook/eyecapture/results/results.md). Using atan2
        // against -z here would sit on the branch cut (z>0) and snap; use +z, and
        // negate y so "up" is positive like the OSC path.
        private static float DirYaw(float x, float z) => MathF.Atan2(x, z);
        private static float DirPitch(float yDown, float z) => MathF.Atan2(-yDown, z);

        private void UpdateEyeTracking()
        {
            long now = Environment.TickCount64;

            // ---- gaze: true per-eye directions from the host DNN (GXRX type 5) ----
            // No synthesis: each eye's yaw/pitch is atan2 of its own raw
            // direction vector, so convergence is whatever the DNN actually saw.
            var ed = Gxrx?.LatestEyeDir();
            bool edFresh = ed != null && (now - Gxrx!.EyeDirLastLocalMs) <= FRESH_MS;
            bool hasL = edFresh && ed!.HasLeft;
            bool hasR = edFresh && ed.HasRight;

            if ((hasL || hasR) && !_hadGxrxGaze)
            {
                _hadGxrxGaze = true;
                Logger.LogInformation("[RootedLinkFT] true per-eye gaze streaming (GXRX eyedir)");
            }

            // Combined Steam Link gaze, used per-eye as the fallback when the
            // host uprobe has no fresh frame for that eye.
            float fAngleX = GazeAngleX(OSCHandler.eyeTrackData[0], OSCHandler.eyeTrackData[2]);
            float fAngleY = GazeAngleY(OSCHandler.eyeTrackData[1], OSCHandler.eyeTrackData[2]);

            if (hasL)
            {
                UnifiedTracking.Data.Eye.Left.Gaze.x = DirYaw(ed!.LDir[0], ed.LDir[2]);
                UnifiedTracking.Data.Eye.Left.Gaze.y = DirPitch(ed.LDir[1], ed.LDir[2]);
            }
            else
            {
                UnifiedTracking.Data.Eye.Left.Gaze.x = fAngleX;
                UnifiedTracking.Data.Eye.Left.Gaze.y = fAngleY;
            }

            if (hasR)
            {
                UnifiedTracking.Data.Eye.Right.Gaze.x = DirYaw(ed!.RDir[0], ed.RDir[2]);
                UnifiedTracking.Data.Eye.Right.Gaze.y = DirPitch(ed.RDir[1], ed.RDir[2]);
            }
            else
            {
                UnifiedTracking.Data.Eye.Right.Gaze.x = fAngleX;
                UnifiedTracking.Data.Eye.Right.Gaze.y = fAngleY;
            }

            bool gazeValid = hasL || hasR;

            if (GazeDebug && now - _lastGazeLogMs > 1000)
            {
                _lastGazeLogMs = now;
                Logger.LogInformation(
                    "[RootedLinkFT] gaze src={Src} L=({Lx:F3},{Ly:F3}) R=({Rx:F3},{Ry:F3}) osc=({O0:F3},{O1:F3},{O2:F3})",
                    gazeValid ? "GXRX" : "OSC",
                    UnifiedTracking.Data.Eye.Left.Gaze.x, UnifiedTracking.Data.Eye.Left.Gaze.y,
                    UnifiedTracking.Data.Eye.Right.Gaze.x, UnifiedTracking.Data.Eye.Right.Gaze.y,
                    OSCHandler.eyeTrackData[0], OSCHandler.eyeTrackData[1], OSCHandler.eyeTrackData[2]);
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

                // Compress the normalized PupilDilation by widening VRCFT's
                // observed min/max window (it only ever widens, so our wider
                // window sticks). Track our own range so the window follows this
                // wearer's lighting/adaptation rather than using fixed numbers.
                float obs = 0.5f * (left + right);
                if (obs > 0f)
                {
                    if (obs < _pupilMinMm) _pupilMinMm = obs;
                    if (obs > _pupilMaxMm) _pupilMaxMm = obs;
                    float center = 0.5f * (_pupilMinMm + _pupilMaxMm);
                    float half = 0.5f * MathF.Max(_pupilMaxMm - _pupilMinMm, 0.05f);
                    float wideHalf = half / MathF.Max(_pupilDilationGain, 0.05f);
                    UnifiedTracking.Data.Eye._minDilation = center - wideHalf;
                    UnifiedTracking.Data.Eye._maxDilation = center + wideHalf;
                }
            }

            // ---- lids / openness: prefer the GXRX face eyelids, else OSC ----
            float closedL = _faceEyelidsValid ? _faceClosed[0] : OSCHandler.eyelids[0];
            float closedR = _faceEyelidsValid ? _faceClosed[1] : OSCHandler.eyelids[1];
            float squintL = UnifiedTracking.Data.Shapes[(int)EyeSquintLeft].Weight;
            float squintR = UnifiedTracking.Data.Shapes[(int)EyeSquintRight].Weight;
            UnifiedTracking.Data.Eye.Left.Openness = CalculateEyeOpenness(closedL, squintL);
            UnifiedTracking.Data.Eye.Right.Openness = CalculateEyeOpenness(closedR, squintR);
        }

        private float AverageDiameterMm(GxrxPupilDetection[] detections, int[] indices)
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
            return n == 0 ? 0f : (sum / n) * _pupilMmScale;
        }

        private void UpdateFaceTracking()
        {
            // Prefer the on-headset 68-blendshape stream (works with any streamer
            // or none); fall back to the Steam Link OSC face weights.
            var face = Gxrx?.LatestFace();
            bool faceFresh = face != null &&
                (Environment.TickCount64 - Gxrx!.FaceLastLocalMs) <= FRESH_MS;

            if (faceFresh)
            {
                if (!_hadFace)
                {
                    _hadFace = true;
                    Logger.LogInformation("[RootedLinkFT] 68-blendshape face streaming (GXRX)");
                }
                ApplyFace68(face!.Values);
                _faceClosed[0] = face.Values[12];  // Eyes_Closed_L
                _faceClosed[1] = face.Values[13];  // Eyes_Closed_R
                _faceEyelidsValid = true;
            }
            else
            {
                _faceEyelidsValid = false;
                foreach (KeyValuePair<UnifiedExpressions, float> entry in OSCHandler.ueData)
                {
                    UnifiedTracking.Data.Shapes[(int)entry.Key].Weight = entry.Value;
                }
            }

            // Lip suck compensation (kept from the dedicated module).
            UnifiedTracking.Data.Shapes[(int)LipSuckUpperLeft].Weight =
                Math.Min(1.0f - (float)Math.Pow(UnifiedTracking.Data.Shapes[(int)MouthUpperLeft].Weight, 1f / 6f),
                         UnifiedTracking.Data.Shapes[(int)LipSuckUpperLeft].Weight);
            UnifiedTracking.Data.Shapes[(int)LipSuckUpperRight].Weight =
                Math.Min(1.0f - (float)Math.Pow(UnifiedTracking.Data.Shapes[(int)MouthUpperRight].Weight, 1f / 6f),
                         UnifiedTracking.Data.Shapes[(int)LipSuckUpperRight].Weight);
        }

        /// <summary>Host 68 blendshapes (Android XR enum order) -> Unified Expressions.</summary>
        private static void ApplyFace68(float[] f)
        {
            var shapes = UnifiedTracking.Data.Shapes;
            foreach (var (face, ue) in Face68Map)
                shapes[ue].Weight = f[face];
        }

        // (face index, Unified Expression). Mirrors OSCHandler's OSC name map.
        private static readonly (int Face, int Ue)[] Face68Map =
        {
            (0, (int)BrowPinchLeft), (0, (int)BrowLowererLeft),
            (1, (int)BrowPinchRight), (1, (int)BrowLowererRight),
            (2, (int)CheekPuffLeft), (3, (int)CheekPuffRight),
            (4, (int)CheekSquintLeft), (5, (int)CheekSquintRight),
            (6, (int)CheekSuckLeft), (7, (int)CheekSuckRight),
            (8, (int)MouthRaiserLower), (9, (int)MouthRaiserUpper),
            (10, (int)MouthDimpleLeft), (11, (int)MouthDimpleRight),
            (22, (int)BrowInnerUpLeft), (23, (int)BrowInnerUpRight),
            (24, (int)JawOpen), (25, (int)JawLeft), (26, (int)JawRight), (27, (int)JawForward),
            (28, (int)EyeSquintLeft), (29, (int)EyeSquintRight),
            (30, (int)MouthFrownLeft), (31, (int)MouthFrownRight),
            (32, (int)MouthCornerPullLeft), (32, (int)MouthCornerSlantLeft),
            (33, (int)MouthCornerPullRight), (33, (int)MouthCornerSlantRight),
            (34, (int)LipFunnelLowerLeft), (35, (int)LipFunnelUpperLeft),
            (36, (int)LipFunnelLowerRight), (37, (int)LipFunnelUpperRight),
            (38, (int)MouthPressLeft), (39, (int)MouthPressRight),
            (40, (int)LipPuckerLowerLeft), (40, (int)LipPuckerUpperLeft),
            (41, (int)LipPuckerLowerRight), (41, (int)LipPuckerUpperRight),
            (42, (int)MouthStretchLeft), (43, (int)MouthStretchRight),
            (44, (int)LipSuckLowerLeft), (45, (int)LipSuckUpperLeft),
            (46, (int)LipSuckLowerRight), (47, (int)LipSuckUpperRight),
            (48, (int)MouthTightenerLeft), (49, (int)MouthTightenerRight),
            (50, (int)MouthClosed),
            (51, (int)MouthLowerDownLeft), (52, (int)MouthLowerDownRight),
            (53, (int)MouthLowerLeft), (53, (int)MouthUpperLeft),
            (54, (int)MouthLowerRight), (54, (int)MouthUpperRight),
            (55, (int)NoseSneerLeft), (56, (int)NoseSneerRight),
            (57, (int)BrowOuterUpLeft), (58, (int)BrowOuterUpRight),
            (59, (int)EyeWideLeft), (60, (int)EyeWideRight),
            (61, (int)MouthUpperUpLeft), (62, (int)MouthUpperUpRight),
            (63, (int)TongueOut), (64, (int)TongueLeft), (65, (int)TongueRight),
            (66, (int)TongueUp), (67, (int)TongueDown),
        };

        private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);

        /// <summary>Optional per-user/per-fit overrides from rootedlinkft.json.</summary>
        private void LoadConfig()
        {
            try
            {
                string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? ".";
                string cfg = Path.Combine(dir, "rootedlinkft.json");
                if (!File.Exists(cfg)) return;

                using var doc = JsonDocument.Parse(File.ReadAllBytes(cfg));
                var r = doc.RootElement;
                if (r.TryGetProperty("pupilMmScale", out var ps)) _pupilMmScale = ps.GetSingle();
                if (r.TryGetProperty("pupilDilationGain", out var pg)) _pupilDilationGain = pg.GetSingle();

                Logger.LogInformation(
                    "[RootedLinkFT] config: pupilMmScale={S} pupilDilationGain={G}",
                    _pupilMmScale, _pupilDilationGain);
            }
            catch (Exception e)
            {
                Logger.LogError("[RootedLinkFT] config load failed: {Message}", e.Message);
            }
        }

        /// <summary>
        /// 11-D tongue -> UE tongue from the GXRX stream. Runs after the Steam
        /// Link face update so it is authoritative when fresh; otherwise the
        /// Steam Link DOFs remain.
        /// </summary>
        private void UpdateTongue()
        {
            if (_tongueMap == null) return;
            var tongue = Gxrx?.LatestTongue();
            if (tongue == null) return;
            if (Environment.TickCount64 - Gxrx!.TongueLastLocalMs > TONGUE_FRESH_MS) return;

            if (!_hadTongue)
            {
                _hadTongue = true;
                Logger.LogInformation("[RootedLinkFT] tongue 11-D streaming (GXRX)");
            }

            _tongueMap.Evaluate(tongue.Values, out float[] dofs, out float roll, out float twist);

            var shapes = UnifiedTracking.Data.Shapes;
            shapes[(int)TongueOut].Weight = Clamp01(dofs[0]);
            shapes[(int)TongueLeft].Weight = Clamp01(dofs[1]);
            shapes[(int)TongueRight].Weight = Clamp01(dofs[2]);
            shapes[(int)TongueUp].Weight = Clamp01(dofs[3]);
            shapes[(int)TongueDown].Weight = Clamp01(dofs[4]);
            shapes[(int)TongueRoll].Weight = Clamp01(roll);
            if (TwistPositiveIsRight)
            {
                if (twist >= 0f) shapes[(int)TongueTwistRight].Weight = Clamp01(twist);
                else shapes[(int)TongueTwistLeft].Weight = Clamp01(-twist);
            }
            else
            {
                if (twist >= 0f) shapes[(int)TongueTwistLeft].Weight = Clamp01(twist);
                else shapes[(int)TongueTwistRight].Weight = Clamp01(-twist);
            }
        }

        public override void Update()
        {
            Thread.Sleep(10);
            UpdateEyeTracking();
            UpdateFaceTracking();
            UpdateTongue(); // after face: the GXRX tongue overrides the Steam Link DOFs
        }

        public override void Teardown()
        {
            OSCHandler?.Teardown();
            Gxrx?.Dispose();
        }
    }
}
