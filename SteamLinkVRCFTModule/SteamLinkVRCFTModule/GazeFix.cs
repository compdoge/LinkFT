using VRCFaceTracking.Core.Types;

namespace SteamLinkVRCFTModule
{
    public enum GazeReject
    {
        None,
        NonFinite,
        Zero,
        ExactCenter,
        NotForward,
    }

    /// <summary>
    /// Builds the (yaw, pitch) gaze pair from Steam Link's gaze vector
    /// (/sl/eyeTrackedGazePoint, +x right, +y up, -z forward).
    ///
    /// Adapted from the Galaxy XR Virtual Desktop fix
    /// (https://github.com/SinonVT/GalaxyXR-EyeTracking-Fix-Virtual-Desktop, GazeFix.cs).
    /// Steam Link already sends a direction, so head roll cannot leak in here the
    /// way it did through VD's quaternion conversion. What carries over is the
    /// angle split and the guards:
    ///
    ///   - yaw = atan2(x, -z), pitch = atan2(y, sqrt(x^2 + z^2)), the same split
    ///     as the VD fix (exact inverse of yaw applied after pitch). The old pitch,
    ///     atan2(y, -z), grows as 1/cos(yaw), so toward the left/right edge small
    ///     vertical noise became large vertical jumps.
    ///   - no normalisation (atan2 is scale-invariant).
    ///   - zero, non-finite, exactly-centred (identity pose) or not-forward vectors
    ///     are rejected so the caller can hold the last good gaze. A zero vector
    ///     used to give atan2(0, -0) = PI on both axes.
    ///   - output is clamped to [-MaxGaze, MaxGaze]. VRCFT's binary eye parameters
    ///     (EyeLeftX1/X2/X4...) do not clamp and wrap past 1.0, which flicks the
    ///     eyes between the edge and the centre.
    /// </summary>
    internal static class GazeFix
    {
        public const float MaxGaze = 1.0f;

        // Forward component as a fraction of the vector length. Below this the
        // vector points more than ~84 degrees away from straight ahead.
        private const float MinForward = 0.1f;

        public static GazeReject TryFromDirection(float x, float y, float z, out Vector2 gaze)
        {
            gaze = Vector2.zero;

            if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z))
                return GazeReject.NonFinite;

            float lengthSq = x * x + y * y + z * z;
            if (!(lengthSq > 1e-12f))
                return GazeReject.Zero;

            // An identity eye pose rotates (0,0,-1) to exactly (0,0,-d). Real
            // tracking never lands on exactly 0.0f on both axes.
            if (x == 0.0f && y == 0.0f)
                return GazeReject.ExactCenter;

            float forward = -z;
            if (forward < MinForward * MathF.Sqrt(lengthSq))
                return GazeReject.NotForward;

            float yaw = MathF.Atan2(x, forward);
            float pitch = MathF.Atan2(y, MathF.Sqrt(x * x + z * z));

            gaze = new Vector2(
                Math.Clamp(yaw, -MaxGaze, MaxGaze),
                Math.Clamp(pitch, -MaxGaze, MaxGaze));
            return GazeReject.None;
        }
    }
}
