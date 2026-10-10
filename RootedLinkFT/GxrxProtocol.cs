// GxrxProtocol.cs - receiver + decoder for the "GXRX" headset uplink.
//
// The Galaxy XR on-headset relay (gxr_relayd, root) sends small UDP datagrams
// to the PC on this port. This is the live link that carries the parameters only
// the raw perception stack exposes: true per-eye gaze directions, the 2D pupil
// ellipse (-> PupilDilation / PupilDiameter*) and the 11-D tongue. The true
// per-eye gaze comes from a read-only uprobe on the host DNN tensor, so there is
// no vendor-client ("eyecap") process and no mount-namespace dance.
//
// Wire format v1, all little-endian:
//
//   offset  field     type   notes
//   0       magic     u32    0x58525847 ('GXRX')
//   4       version   u8     1
//   5       type      u8     2 = pupil, 3 = face(optional), 4 = tongue,
//                            5 = eyedir
//   6       flags     u16    bit0 = valid / calibrated
//   8       seq       u32    per-type counter
//   12      t_us      u64    sender CLOCK_MONOTONIC, microseconds
//   20      len       u16    payload byte count
//   22      payload   ...
//
//   type 2 pupil : u8 nd, then nd * 6 * float32
//                  per detection: x, y, a, b, conf, angle
//                  (a/b are ellipse radii, normalized to the camera image)
//   type 4 tongue: 11 * float32 (predicted_local_tongue_expression)
//   type 5 eyedir: u8 mask (bit0 = L valid, bit1 = R valid), then
//                  Ldir[3], Rdir[3] as raw float32 gaze directions.

using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace RootedLinkFT
{
    public enum GxrxType : byte
    {
        Pupil = 2,
        Face = 3,
        Tongue = 4,
        EyeDir = 5,
        Jaw = 6,
    }

    /// <summary>
    /// True per-eye gaze directions from the host DNN tensor (type 5). Raw
    /// float32 vectors; the module converts them to yaw/pitch. Mask bit0 = left
    /// valid, bit1 = right valid.
    /// </summary>
    public sealed class GxrxEyeDir
    {
        public long TUs;
        public byte Mask;
        public readonly float[] LDir = new float[3];
        public readonly float[] RDir = new float[3];

        public bool HasLeft => (Mask & 1) != 0;
        public bool HasRight => (Mask & 2) != 0;
    }

    public struct GxrxPupilDetection
    {
        public float X, Y, A, B, Conf, Angle;
    }

    public sealed class GxrxPupil
    {
        public long TUs;
        public GxrxPupilDetection[] Detections = Array.Empty<GxrxPupilDetection>();
    }

    /// <summary>11-D predicted_local_tongue_expression (u2[63..73]).</summary>
    public sealed class GxrxTongue
    {
        public long TUs;
        public const int Count = 11;
        public readonly float[] Values = new float[Count];
    }

    /// <summary>68 Android-XR face blendshapes (host 0x3589f8), enum order.</summary>
    public sealed class GxrxFace
    {
        public long TUs;
        public const int Count = 68;
        public readonly float[] Values = new float[Count];
    }

    /// <summary>
    /// Pre-clamp jaw (host 0xf09aa4) plus the tongue flags that trigger the
    /// vendor's JawOpen clamp. jaw_raw tracks the real jaw even with the tongue
    /// out, where the shipped 68-vector (type 3) is pinned up.
    /// </summary>
    public sealed class GxrxJaw
    {
        public long TUs;
        public float JawRaw;
        public float TongueOut;
        public float TongueUp;
        public float TongueDown;
    }

    /// <summary>
    /// Background UDP listener for GXRX v1 packets. Keeps the latest gaze and
    /// pupil frames; the module polls them on its own update tick.
    /// </summary>
    public sealed class GxrxReceiver : IDisposable
    {
        public const uint Magic = 0x58525847; // 'GXRX'
        public const byte Version = 1;
        public const int HeaderSize = 22;

        private const int EyeDirPayloadSize = 1 + 6 * 4;
        private const int FacePayloadSize = GxrxFace.Count * 4;
        private const int TonguePayloadSize = GxrxTongue.Count * 4;
        private const int JawPayloadSize = 4 * 4;
        private const int MaxDatagram = 4096;

        private readonly ILogger _logger;
        private readonly UdpClient _udp;
        private readonly Thread _thread;
        private volatile bool _loop = true;

        private readonly object _gate = new();
        private GxrxEyeDir? _eyeDir;
        private GxrxPupil? _pupil;
        private GxrxFace? _face;
        private GxrxTongue? _tongue;
        private GxrxJaw? _jaw;

        public int Port { get; }
        public long EyeDirPackets { get; private set; }
        public long PupilPackets { get; private set; }
        public long FacePackets { get; private set; }
        public long TonguePackets { get; private set; }
        public long JawPackets { get; private set; }

        // Local (PC) receive times. The sender's t_us is CLOCK_MONOTONIC on the
        // headset and is not comparable to PC time, so freshness is measured here.
        public long EyeDirLastLocalMs { get; private set; }
        public long PupilLastLocalMs { get; private set; }
        public long FaceLastLocalMs { get; private set; }
        public long TongueLastLocalMs { get; private set; }
        public long JawLastLocalMs { get; private set; }

        public GxrxReceiver(ILogger logger, int port)
        {
            _logger = logger;
            Port = port;
            _udp = new UdpClient(AddressFamily.InterNetwork);
            // Bind on all interfaces so Wi-Fi traffic from the headset arrives.
            _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udp.Client.Bind(new IPEndPoint(IPAddress.Any, port));
            _udp.Client.ReceiveTimeout = 1000;

            _thread = new Thread(ListenLoop) { IsBackground = true, Name = "GXRX RX" };
            _thread.Start();
            _logger.LogInformation("[RootedLinkFT] GXRX receiver bound on 0.0.0.0:{Port}", port);
        }

        private void ListenLoop()
        {
            var remote = new IPEndPoint(IPAddress.Any, 0);
            while (_loop)
            {
                try
                {
                    byte[] data = _udp.Receive(ref remote);
                    Parse(data, data.Length);
                }
                catch (SocketException)
                {
                    // Receive timeout or transient error; keep the loop alive.
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (Exception e)
                {
                    _logger.LogDebug("[RootedLinkFT] GXRX receive error: {Message}", e.Message);
                }
            }
        }

        private void Parse(byte[] d, int len)
        {
            if (len < HeaderSize) return;
            if (BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(0)) != Magic) return;
            if (d[4] != Version) return;

            var type = (GxrxType)d[5];
            ushort payloadLen = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(20));
            if (HeaderSize + payloadLen > len) return;

            long tUs = unchecked((long)BinaryPrimitives.ReadUInt64LittleEndian(d.AsSpan(12)));

            switch (type)
            {
                case GxrxType.EyeDir:
                    if (payloadLen < EyeDirPayloadSize) return;
                    var ed = new GxrxEyeDir { TUs = tUs, Mask = d[HeaderSize] };
                    int eo = HeaderSize + 1;
                    for (int i = 0; i < 3; i++) ed.LDir[i] = BitConverter.ToSingle(d, eo + i * 4);
                    for (int i = 0; i < 3; i++) ed.RDir[i] = BitConverter.ToSingle(d, eo + 12 + i * 4);
                    lock (_gate) { _eyeDir = ed; }
                    EyeDirLastLocalMs = Environment.TickCount64;
                    EyeDirPackets++;
                    break;

                case GxrxType.Face:
                    if (payloadLen < FacePayloadSize) return;
                    var fc = new GxrxFace { TUs = tUs };
                    for (int i = 0; i < GxrxFace.Count; i++)
                        fc.Values[i] = BitConverter.ToSingle(d, HeaderSize + i * 4);
                    lock (_gate) { _face = fc; }
                    FaceLastLocalMs = Environment.TickCount64;
                    FacePackets++;
                    break;

                case GxrxType.Jaw:
                    if (payloadLen < JawPayloadSize) return;
                    var jw = new GxrxJaw { TUs = tUs };
                    jw.JawRaw = BitConverter.ToSingle(d, HeaderSize);
                    jw.TongueOut = BitConverter.ToSingle(d, HeaderSize + 4);
                    jw.TongueUp = BitConverter.ToSingle(d, HeaderSize + 8);
                    jw.TongueDown = BitConverter.ToSingle(d, HeaderSize + 12);
                    lock (_gate) { _jaw = jw; }
                    JawLastLocalMs = Environment.TickCount64;
                    JawPackets++;
                    break;

                case GxrxType.Pupil:
                    if (payloadLen < 1) return;
                    int nd = d[HeaderSize];
                    if (HeaderSize + 1 + nd * 24 > len) return;
                    var p = new GxrxPupil { TUs = tUs, Detections = new GxrxPupilDetection[nd] };
                    int po = HeaderSize + 1;
                    for (int i = 0; i < nd; i++)
                    {
                        int b = po + i * 24;
                        p.Detections[i] = new GxrxPupilDetection
                        {
                            X = BitConverter.ToSingle(d, b),
                            Y = BitConverter.ToSingle(d, b + 4),
                            A = BitConverter.ToSingle(d, b + 8),
                            B = BitConverter.ToSingle(d, b + 12),
                            Conf = BitConverter.ToSingle(d, b + 16),
                            Angle = BitConverter.ToSingle(d, b + 20),
                        };
                    }
                    lock (_gate) { _pupil = p; }
                    PupilLastLocalMs = Environment.TickCount64;
                    PupilPackets++;
                    break;

                case GxrxType.Tongue:
                    if (payloadLen < TonguePayloadSize) return;
                    var tg = new GxrxTongue { TUs = tUs };
                    for (int i = 0; i < GxrxTongue.Count; i++)
                        tg.Values[i] = BitConverter.ToSingle(d, HeaderSize + i * 4);
                    lock (_gate) { _tongue = tg; }
                    TongueLastLocalMs = Environment.TickCount64;
                    TonguePackets++;
                    break;

                default:
                    // Face is reserved; ignored for now.
                    break;
            }
        }

        /// <summary>Latest per-eye direction frame, or null if none received.</summary>
        public GxrxEyeDir? LatestEyeDir()
        {
            lock (_gate) return _eyeDir;
        }

        /// <summary>Latest 68-blendshape face frame, or null if none received.</summary>
        public GxrxFace? LatestFace()
        {
            lock (_gate) return _face;
        }

        /// <summary>Latest pre-clamp jaw frame, or null if none received.</summary>
        public GxrxJaw? LatestJaw()
        {
            lock (_gate) return _jaw;
        }

        /// <summary>Latest pupil frame, or null if none received.</summary>
        public GxrxPupil? LatestPupil()
        {
            lock (_gate) return _pupil;
        }

        /// <summary>Latest tongue frame, or null if none received.</summary>
        public GxrxTongue? LatestTongue()
        {
            lock (_gate) return _tongue;
        }

        public void Dispose()
        {
            _loop = false;
            try { _udp.Close(); } catch { }
            try { _udp.Dispose(); } catch { }
            if (_thread.IsAlive) _thread.Join(500);
        }
    }
}
