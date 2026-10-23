using Microsoft.Extensions.Logging;
using SteamLinkVRCFTModule;
using System.Net.Sockets;
using VRCFaceTracking;
using VRCFaceTracking.Core.Params.Expressions;
using VRCFaceTracking.Core.Types;
using static VRCFaceTracking.Core.Params.Expressions.UnifiedExpressions;

namespace SteamLinkVRCFTModule
{
    public class SteamLinkVRCFTModule : ExtTrackingModule
    {
        private OSCHandler OSCHandler;
        private const int DEFAULT_PORT = 9015;

        public override (bool SupportsEye, bool SupportsExpression) Supported => (true, true);

        public override (bool eyeSuccess, bool expressionSuccess) Initialize(bool eyeAvailable, bool expressionAvailable)
        {
            ModuleInformation.Name = "Galaxy XR SteamLink Module";

            var stream = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("SteamLinkVRCFTModule.Assets.galaxy-xr.png");
            ModuleInformation.StaticImages = stream != null ? new List<Stream> { stream } : ModuleInformation.StaticImages;

            //TODO better error handling on fail? isInit for OSC Handler?
            OSCHandler = new OSCHandler(Logger, DEFAULT_PORT);

            return (true, true);
        }

        private static float CalculateEyeOpenness(float fEyeClosedWeight, float fEyeTightener)
        {
            return 1.0f - Math.Clamp(fEyeClosedWeight + fEyeClosedWeight * fEyeTightener, 0.0f, 1.0f);
        }

        // GazeFix plus filling Steam Link's bad runs from the face tracker's eye look weights.
        private readonly GazeFilter _gazeFilter = new();
        private readonly System.Diagnostics.Stopwatch _gazeClock = System.Diagnostics.Stopwatch.StartNew();
        private Vector2 _lastGaze = Vector2.zero;
        private float _lastRawX, _lastRawY, _lastRawZ;

        // Once-per-second summary of rejected vectors and large jumps, so a glitch
        // seen on the avatar can be matched against the raw Steam Link data.
        private const float GAZE_JUMP_RAD = 0.5f;
        private readonly int[] _gazeRejectCounts = new int[Enum.GetValues<GazeReject>().Length];
        private int _gazeJumpCount, _gazeClampCount, _gazeFillCount;
        private string _gazeLastAnomaly = "";
        private long _gazeLogMs;

        private void UpdateEyeTracking()
        {
            {
                float fRawX = OSCHandler.eyeTrackData[0];
                float fRawY = OSCHandler.eyeTrackData[1];
                float fRawZ = OSCHandler.eyeTrackData[2];
                bool bNewSample = fRawX != _lastRawX || fRawY != _lastRawY || fRawZ != _lastRawZ;
                (_lastRawX, _lastRawY, _lastRawZ) = (fRawX, fRawY, fRawZ);

                float[] look = OSCHandler.eyeLook;
                float fFaceYaw = (look[OSCHandler.EyeLookRightL] + look[OSCHandler.EyeLookRightR] - look[OSCHandler.EyeLookLeftL] - look[OSCHandler.EyeLookLeftR]) / 2.0f;
                float fFacePitch = (look[OSCHandler.EyeLookUpL] + look[OSCHandler.EyeLookUpR] - look[OSCHandler.EyeLookDownL] - look[OSCHandler.EyeLookDownR]) / 2.0f;

                Vector2 gaze = _gazeFilter.Update(fRawX, fRawY, fRawZ, fFaceYaw, fFacePitch, _gazeClock.Elapsed.TotalMilliseconds);
                GazeReject reject = _gazeFilter.LastReject;
                if (bNewSample && reject == GazeReject.None)
                {
                    if (MathF.Abs(gaze.x) >= GazeFix.MaxGaze || MathF.Abs(gaze.y) >= GazeFix.MaxGaze)
                    {
                        _gazeClampCount++;
                        _gazeLastAnomaly = $"clamped raw=({fRawX:F3},{fRawY:F3},{fRawZ:F3})";
                    }
                    if (MathF.Abs(gaze.x - _lastGaze.x) > GAZE_JUMP_RAD || MathF.Abs(gaze.y - _lastGaze.y) > GAZE_JUMP_RAD)
                    {
                        _gazeJumpCount++;
                        _gazeLastAnomaly = $"jump ({_lastGaze.x:F2},{_lastGaze.y:F2})->({gaze.x:F2},{gaze.y:F2}) raw=({fRawX:F3},{fRawY:F3},{fRawZ:F3})";
                    }
                }
                else if (bNewSample)
                {
                    _gazeRejectCounts[(int)reject]++;
                    if (_gazeFilter.Filling)
                    {
                        _gazeFillCount++;
                    }
                    _gazeLastAnomaly = $"{reject} raw=({fRawX:F3},{fRawY:F3},{fRawZ:F3})";
                }
                _lastGaze = gaze;
                LogGazeAnomalies();

                UnifiedTracking.Data.Eye.Left.Gaze = gaze;
                UnifiedTracking.Data.Eye.Right.Gaze = gaze;


                //Pupil Dilation, This is not supported, but if we don't set it can cause issues
                UnifiedTracking.Data.Eye.Left.PupilDiameter_MM = 5f;
                UnifiedTracking.Data.Eye.Right.PupilDiameter_MM = 5f;
                UnifiedTracking.Data.Eye._maxDilation = 10;
                UnifiedTracking.Data.Eye._minDilation = 0;

            }

            {
                float fLeftOpenness = CalculateEyeOpenness(OSCHandler.eyelids[0], OSCHandler.ueData[UnifiedExpressions.EyeSquintLeft]);
                float fRightOpenness = CalculateEyeOpenness(OSCHandler.eyelids[1], OSCHandler.ueData[UnifiedExpressions.EyeSquintRight]);

                UnifiedTracking.Data.Eye.Left.Openness = fLeftOpenness;// fLeftOpenness;
                UnifiedTracking.Data.Eye.Right.Openness = fRightOpenness;//fRightOpenness;
            }

        }

        private void LogGazeAnomalies()
        {
            long now = Environment.TickCount64;
            if (now - _gazeLogMs < 1000)
            {
                return;
            }
            _gazeLogMs = now;

            int rejected = _gazeRejectCounts.Sum();
            if (rejected == 0 && _gazeJumpCount == 0 && _gazeClampCount == 0)
            {
                return;
            }

            Logger.LogWarning("[GazeFix] last 1s: rejected={Rejected} (nonfinite={NonFinite} zero={Zero} center={Center} notforward={NotForward}) filled={Filled} jumps={Jumps} clamped={Clamped}; last: {Last}",
                rejected,
                _gazeRejectCounts[(int)GazeReject.NonFinite], _gazeRejectCounts[(int)GazeReject.Zero],
                _gazeRejectCounts[(int)GazeReject.ExactCenter], _gazeRejectCounts[(int)GazeReject.NotForward],
                _gazeFillCount, _gazeJumpCount, _gazeClampCount, _gazeLastAnomaly);

            Array.Clear(_gazeRejectCounts);
            _gazeJumpCount = 0;
            _gazeClampCount = 0;
            _gazeFillCount = 0;
        }

        private void UpdateFaceTracking()
        {
            foreach (KeyValuePair<UnifiedExpressions, float> entry in OSCHandler.ueData)
            {
                UnifiedTracking.Data.Shapes[(int)entry.Key].Weight = entry.Value;
            }
            //?? fix some weird ft things need to check (Its not needed anymore, this in fact causes upper lip to not move properly, its more notorious when you try to set an "eww" face)
            //UnifiedTracking.Data.Shapes[(int)MouthUpperUpLeft].Weight = Math.Max(0, UnifiedTracking.Data.Shapes[(int)MouthUpperUpLeft].Weight - UnifiedTracking.Data.Shapes[(int)NoseSneerLeft].Weight);
            //UnifiedTracking.Data.Shapes[(int)MouthUpperUpRight].Weight = Math.Max(0, UnifiedTracking.Data.Shapes[(int)MouthUpperUpRight].Weight - UnifiedTracking.Data.Shapes[(int)NoseSneerRight].Weight);
            //UnifiedTracking.Data.Shapes[(int)MouthUpperDeepenLeft].Weight = Math.Max(0, UnifiedTracking.Data.Shapes[(int)MouthUpperUpLeft].Weight - UnifiedTracking.Data.Shapes[(int)NoseSneerLeft].Weight);
            //UnifiedTracking.Data.Shapes[(int)MouthUpperDeepenRight].Weight = Math.Max(0, UnifiedTracking.Data.Shapes[(int)MouthUpperUpRight].Weight - UnifiedTracking.Data.Shapes[(int)NoseSneerRight].Weight);

            //lip Suck
            UnifiedTracking.Data.Shapes[(int)LipSuckUpperLeft].Weight = Math.Min(1.0f - (float)Math.Pow(UnifiedTracking.Data.Shapes[(int)MouthUpperLeft].Weight, 1f / 6f), UnifiedTracking.Data.Shapes[(int)LipSuckUpperLeft].Weight);
            UnifiedTracking.Data.Shapes[(int)LipSuckUpperRight].Weight = Math.Min(1.0f - (float)Math.Pow(UnifiedTracking.Data.Shapes[(int)MouthUpperRight].Weight, 1f / 6f), UnifiedTracking.Data.Shapes[(int)LipSuckUpperRight].Weight);
        }

        public override void Update()
        {
            Thread.Sleep(10);
            UpdateEyeTracking();
            UpdateFaceTracking();
        }
        public override void Teardown()
        {
            OSCHandler.Teardown();
        }
    }
}
