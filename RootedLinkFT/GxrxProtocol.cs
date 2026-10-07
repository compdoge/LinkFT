// GxrxProtocol.cs - receiver + decoder for the "GXRX" headset uplink.
//
// The Galaxy XR on-headset relay (gxr-relayd, root) and the gaze driver
// (eyecap, shell) send small UDP datagrams to the PC on this port. This is the
// live link that carries the parameters only the raw perception stack exposes:
// the 2D pupil ellipse (-> PupilDilation / PupilDiameter*) and true per-eye
// gaze.
//
// Wire format v1, all little-endian:
//
//   offset  field     type   notes
//   0       magic     u32    0x58525847 ('GXRX')
//   4       version   u8     1
//   5       type      u8     1 = gaze, 2 = pupil, 3 = face(optional), 4 = tongue
//   6       flags     u16    bit0 = valid / calibrated
//   8       seq       u32    per-type counter
//   12      t_us      u64    sender CLOCK_MONOTONIC, microseconds
//   20      len       u16    payload byte count
//   22      payload   ...
//
//   type 1 gaze  : 15 * float32 = 60 bytes
//                  Lpos[3], Ldir[3], Rpos[3], Rdir[3], binoPt[3]
//   type 2 pupil : u8 nd, then nd * 6 * float32
//                  per detection: x, y, a, b, conf, angle
//                  (a/b are ellipse radii, normalized to the camera image)

using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace RootedLinkFT
{
    public enum GxrxType : byte
    {
        Gaze = 1,
        Pupil = 2,
        Face = 3,
        Tongue = 4,
    }

    public sealed class GxrxGaze
    {
        public long TUs;
        public readonly float[] LPos = new float[3];
        public readonly float[] LDir = new float[3];
        public readonly float[] RPos = new float[3];
        public readonly float[] RDir = new float[3];
        public readonly float[] BinoPt = new float[3];
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

    /// <summary>
    /// Background UDP listener for GXRX v1 packets. Keeps the latest gaze and
    /// pupil frames; the module polls them on its own update tick.
    /// </summary>
    public sealed class GxrxReceiver : IDisposable
    {
        public const uint Magic = 0x58525847; // 'GXRX'
        public const byte Version = 1;
        public const int HeaderSize = 22;

        private const int GazePayloadSize = 15 * 4;
        private const int TonguePayloadSize = GxrxTongue.Count * 4;
        private const int MaxDatagram = 4096;

        private readonly ILogger _logger;
        private readonly UdpClient _udp;
        private readonly Thread _thread;
        private volatile bool _loop = true;

        private readonly object _gate = new();
        private GxrxGaze? _gaze;
        private GxrxPupil? _pupil;
        private GxrxTongue? _tongue;

        public int Port { get; }
        public long GazePackets { get; private set; }
        public long PupilPackets { get; private set; }
        public long TonguePackets { get; private set; }

        // Local (PC) receive times. The sender's t_us is CLOCK_MONOTONIC on the
        // headset and is not comparable to PC time, so freshness is measured here.
        public long GazeLastLocalMs { get; private set; }
        public long PupilLastLocalMs { get; private set; }
        public long TongueLastLocalMs { get; private set; }

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
                case GxrxType.Gaze:
                    if (payloadLen < GazePayloadSize) return;
                    var g = new GxrxGaze { TUs = tUs };
                    int o = HeaderSize;
                    for (int i = 0; i < 3; i++) g.LPos[i] = BitConverter.ToSingle(d, o + i * 4);
                    for (int i = 0; i < 3; i++) g.LDir[i] = BitConverter.ToSingle(d, o + 12 + i * 4);
                    for (int i = 0; i < 3; i++) g.RPos[i] = BitConverter.ToSingle(d, o + 24 + i * 4);
                    for (int i = 0; i < 3; i++) g.RDir[i] = BitConverter.ToSingle(d, o + 36 + i * 4);
                    for (int i = 0; i < 3; i++) g.BinoPt[i] = BitConverter.ToSingle(d, o + 48 + i * 4);
                    lock (_gate) { _gaze = g; }
                    GazeLastLocalMs = Environment.TickCount64;
                    GazePackets++;
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

        /// <summary>Latest gaze frame, or null if none received.</summary>
        public GxrxGaze? LatestGaze()
        {
            lock (_gate) return _gaze;
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
