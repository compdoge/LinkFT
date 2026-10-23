using VRCFaceTracking.Core.Types;

namespace SteamLinkVRCFTModule
{
    /// <summary>
    /// Turns Steam Link gaze vectors into a gaze that keeps moving through
    /// Steam Link's bad runs.
    ///
    /// On Galaxy XR, Steam Link sends ~100 ms runs of wrong gaze whenever the
    /// gaze points near yaw 180 of the headset's tracking space: the vector is
    /// turned about 180 degrees about world up, so it points behind the head
    /// and GazeFix rejects it. Holding the last good gaze through a run froze
    /// the eyes and then snapped them to the new position.
    ///
    /// During a run the face tracker's EyesLook weights still arrive through a
    /// separate path, so the gaze is moved from the last good value by how much
    /// those weights changed. Whenever the source switches between real and
    /// filled gaze, the gap between the old output and the new source fades out
    /// over BlendMs, so neither entering nor leaving a run snaps the eyes.
    /// Without bad runs the output is exactly GazeFix's.
    /// </summary>
    internal sealed class GazeFilter
    {
        // Radians of gaze per unit of EyesLook (right - left) and (up - down),
        // fitted on Galaxy XR Steam Link captures (r = 0.85 and 0.73).
        private const float FaceYawGain = 0.670f;
        private const float FacePitchGain = 0.559f;

        private const double BlendMs = 60.0;

        private bool _hasGood;
        private Vector2 _lastGood;
        private float _faceYawAtGood, _facePitchAtGood;
        private Vector2 _output;
        private Vector2 _offset;
        private double _offsetStartMs;

        public GazeReject LastReject { get; private set; }
        public bool Filling { get; private set; }

        /// <param name="faceYaw">EyesLook (right - left), averaged over both eyes.</param>
        /// <param name="facePitch">EyesLook (up - down), averaged over both eyes.</param>
        public Vector2 Update(float x, float y, float z, float faceYaw, float facePitch, double nowMs)
        {
            LastReject = GazeFix.TryFromDirection(x, y, z, out Vector2 gaze);
            bool filling;
            Vector2 target;
            if (LastReject == GazeReject.None)
            {
                filling = false;
                target = gaze;
            }
            else if (_hasGood)
            {
                filling = true;
                target = new Vector2(
                    Math.Clamp(_lastGood.x + FaceYawGain * (faceYaw - _faceYawAtGood), -GazeFix.MaxGaze, GazeFix.MaxGaze),
                    Math.Clamp(_lastGood.y + FacePitchGain * (facePitch - _facePitchAtGood), -GazeFix.MaxGaze, GazeFix.MaxGaze));
            }
            else
            {
                return _output;
            }

            if (filling != Filling && _hasGood)
            {
                _offset = _output - target;
                _offsetStartMs = nowMs;
            }
            Filling = filling;

            if (!filling)
            {
                _hasGood = true;
                _lastGood = gaze;
                _faceYawAtGood = faceYaw;
                _facePitchAtGood = facePitch;
            }

            float fade = 1f - (float)((nowMs - _offsetStartMs) / BlendMs);
            _output = fade > 0f ? target + _offset * fade : target;
            return _output;
        }
    }
}
