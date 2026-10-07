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
//   - gxr-relayd (root) + eyecap (shell) on the headset, sending GXRX v1.

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

        // Ellipse radius (0..1 of the camera image) -> millimetres. Provisional:
        // the true scale needs a calibration pass (see LIVE-PC-LINK-PLAN.md §6).
        // Diameter = 2 * max(axisA, axisB); VRCFT normalizes PupilDilation itself.
        private const float PupilMmScale = 32.0f;

        // Convergence. The client per-eye *directions* are essentially parallel
        // (constant yaw offset ~0.03 rad even when crossing hard), but the eye
        // *positions* carry the real vergence: IPD drops ~25% when focusing near.
        //
        // Everything is normalized by the wearer's own baseline IPD, so a
        // different user / headset fit needs no retuning:
        //   neutral = running max IPD (auto), or neutralIpdMm from config
        //   rel     = (neutral - ipd) / neutral        (fractional IPD loss)
        //   verg    = clamp(rel / fullScaleFrac, 0, 1) * maxVergenceRad
        // A larger-IPD wearer has a larger absolute IPD drop for the same
        // convergence angle, which this fraction cancels out.
        //
        // Optional overrides live in `rootedlinkft.json` beside this DLL:
        //   { "neutralIpdMm": 64.5, "fullScaleFrac": 0.24,
        //     "maxVergenceRad": 0.33, "deadzoneMm": 0.5 }
        private const float IpdMin = 0.02f;          // plausibility window (m)
        private const float IpdMax = 0.12f;
        private const bool VergenceDebug = false;    // 1 Hz ipd/neutral/verg log
        private float _ipdNeutral = 0.0645f;         // baseline (auto or fixed)
        private bool _neutralInit;                   // first plausible frame seen
        private bool _neutralAuto = true;
        private float _neutralFixed = 0.0645f;
        private float _fullScaleFrac = 0.24f;        // fractional IPD loss = full cross
        private float _maxVergence = 0.33f;          // rad per eye at full cross
        private float _deadzone = 0.0005f;           // m
        private long _lastVergLogMs;

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
        private bool _hadTongue;

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
                // Common (conjugate) gaze from the two directions.
                float yawL = DirYaw(gaze!.LDir[0], gaze.LDir[2]);
                float yawR = DirYaw(gaze.RDir[0], gaze.RDir[2]);
                float commonX = 0.5f * (yawL + yawR);

                // Per-wearer baseline: seed from the first plausible frame, then
                // track the running max (far). Never decay -- the eye-position
                // separation sits at the working distance for long stretches and
                // a decaying baseline would erase the vergence signal.
                float ipd = gaze.RPos[0] - gaze.LPos[0];
                if (_neutralAuto)
                {
                    if (ipd > IpdMin && ipd < IpdMax)
                    {
                        if (!_neutralInit) { _ipdNeutral = ipd; _neutralInit = true; }
                        else if (ipd > _ipdNeutral) _ipdNeutral = ipd;
                    }
                }
                else
                {
                    _ipdNeutral = _neutralFixed;
                }

                // Fractional IPD loss -> per-eye inward rotation. Normalized by
                // the baseline, so the same relative convergence gives the same
                // rotation regardless of the wearer's IPD.
                float rel = (_ipdNeutral - ipd - _deadzone) / MathF.Max(_ipdNeutral, 0.03f);
                if (rel < 0f) rel = 0f;
                float verg = Math.Clamp(rel / _fullScaleFrac, 0f, 1f) * _maxVergence;

                float leftX = commonX + verg;   // left eye turns in (toward +x)
                float rightX = commonX - verg;  // right eye turns in (toward -x)

                if (VergenceDebug && now - _lastVergLogMs > 1000)
                {
                    _lastVergLogMs = now;
                    Logger.LogInformation(
                        "[RootedLinkFT] ipd={Ipd:F1}mm neutral={Neutral:F1}mm common={C:F3} verg={V:F3} -> EyeLeftX={L:F3} EyeRightX={R:F3}",
                        ipd * 1000f, _ipdNeutral * 1000f, commonX, verg, leftX, rightX);
                }

                UnifiedTracking.Data.Eye.Left.Gaze.x = leftX;
                UnifiedTracking.Data.Eye.Right.Gaze.x = rightX;
                UnifiedTracking.Data.Eye.Left.Gaze.y = DirPitch(gaze.LDir[1], gaze.LDir[2]);
                UnifiedTracking.Data.Eye.Right.Gaze.y = DirPitch(gaze.RDir[1], gaze.RDir[2]);
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
                if (r.TryGetProperty("neutralIpdMm", out var n) && n.GetSingle() > 0f)
                {
                    _neutralAuto = false;
                    _neutralFixed = n.GetSingle() / 1000f;
                    _ipdNeutral = _neutralFixed;
                }
                if (r.TryGetProperty("fullScaleFrac", out var f)) _fullScaleFrac = f.GetSingle();
                if (r.TryGetProperty("maxVergenceRad", out var m)) _maxVergence = m.GetSingle();
                if (r.TryGetProperty("deadzoneMm", out var d)) _deadzone = d.GetSingle() / 1000f;

                Logger.LogInformation(
                    "[RootedLinkFT] config: auto={Auto} neutral={N:F1}mm fullScale={F} maxVerg={M}rad",
                    _neutralAuto, _ipdNeutral * 1000f, _fullScaleFrac, _maxVergence);
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
