// TongueMap.cs - evaluates the Galaxy XR 11-D tongue vector.
//
// The 11 floats are u2[63..73] of the face host result (the copy at hook
// 0x358a34) == predicted_local_tongue_expression. Two parts:
//   * an exact MLP 11->128->128->64->5 (recovered from the embedded TFLite model
//     @0x220cf0e0) that reproduces the device's own 5 tongue DOFs;
//   * two linear axes for roll / twist (best-effort fit; partly confounded).
//
// Weights are embedded from results/tongue-map.json (see perception-hook).
// Output DOF order: OUT, LEFT, RIGHT, UP, DOWN.

using System.Text.Json;

namespace RootedLinkFT
{
    public sealed class TongueMap
    {
        private readonly float[][] _w1, _w2, _w3, _w4;
        private readonly float[] _b1, _b2, _b3, _b4;
        private readonly float[] _rollAxis, _twistAxis;

        public TongueMap(Stream json)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var m = root.GetProperty("mapper");
            _w1 = Mat(m.GetProperty("W1")); _b1 = Vec(m.GetProperty("b1"));
            _w2 = Mat(m.GetProperty("W2")); _b2 = Vec(m.GetProperty("b2"));
            _w3 = Mat(m.GetProperty("W3")); _b3 = Vec(m.GetProperty("b3"));
            _w4 = Mat(m.GetProperty("W4")); _b4 = Vec(m.GetProperty("b4"));
            var ex = root.GetProperty("extra_axes");
            _rollAxis = Vec(ex.GetProperty("roll_axis"));
            _twistAxis = Vec(ex.GetProperty("twist_axis"));
        }

        private static float[] Vec(JsonElement e)
            => e.EnumerateArray().Select(v => v.GetSingle()).ToArray();

        private static float[][] Mat(JsonElement e)
            => e.EnumerateArray().Select(Vec).ToArray();

        /// <summary>Evaluate one 11-D frame. dofs = [OUT,LEFT,RIGHT,UP,DOWN].</summary>
        public void Evaluate(float[] x, out float[] dofs, out float roll, out float twist)
        {
            var h = MatVec(_w1, _b1, x); Relu(h);
            h = MatVec(_w2, _b2, h); Relu(h);
            h = MatVec(_w3, _b3, h); Relu(h);
            dofs = MatVec(_w4, _b4, h); Sigmoid(dofs);
            roll = Dot(x, _rollAxis);
            twist = Dot(x, _twistAxis);
        }

        private static float[] MatVec(float[][] w, float[] b, float[] x)
        {
            var r = new float[w.Length];
            for (int i = 0; i < w.Length; i++)
            {
                float s = b[i];
                float[] row = w[i];
                for (int j = 0; j < x.Length; j++) s += row[j] * x[j];
                r[i] = s;
            }
            return r;
        }

        private static void Relu(float[] v) { for (int i = 0; i < v.Length; i++) if (v[i] < 0f) v[i] = 0f; }
        private static void Sigmoid(float[] v) { for (int i = 0; i < v.Length; i++) v[i] = 1f / (1f + MathF.Exp(-v[i])); }
        private static float Dot(float[] a, float[] b)
        {
            float s = 0f;
            for (int i = 0; i < a.Length && i < b.Length; i++) s += a[i] * b[i];
            return s;
        }
    }
}
