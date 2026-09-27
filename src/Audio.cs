using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace TheCloser
{
    #region WASAPI COM interop

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    internal class MMDeviceEnumeratorCom { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        [PreserveSig] int OpenPropertyStore(int access, out IntPtr properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out int state);
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr audioSessionGuid);
        [PreserveSig] int GetBufferSize(out uint numBufferFrames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint numPaddingFrames);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);
        [PreserveSig] int GetMixFormat(out IntPtr deviceFormat);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr eventHandle);
        [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint numFramesToRead, out uint flags, out ulong devicePosition, out ulong qpcPosition);
        [PreserveSig] int ReleaseBuffer(uint numFramesRead);
        [PreserveSig] int GetNextPacketSize(out uint numFramesInNextPacket);
    }

    #endregion

    /// <summary>
    /// Captures audio with WASAPI in shared mode: either what the PC plays (loopback = the other people on the call)
    /// or the default microphone. Output is mono float at 16 kHz.
    /// Loopback listens to both default outputs - the everyday one and the one Windows gives call apps
    /// ("communications"). They're usually the same device; with a Bluetooth headset the call often plays on its
    /// Hands-Free device instead, which the everyday one would miss. When both play, they're mixed.
    /// </summary>
    internal sealed class WasapiCapture : IDisposable
    {
        public const int OutRate = 16000;

        private static readonly Guid IID_IAudioClient = new Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
        private static readonly Guid IID_IAudioCaptureClient = new Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317");
        private static readonly Guid SubtypeFloat = new Guid("00000003-0000-0010-8000-00aa00389b71");
        private const int AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
        private const int CLSCTX_ALL = 23;
        private const uint AUDCLNT_BUFFERFLAGS_SILENT = 0x2;
        private const int RoleConsole = 0, RoleCommunications = 2;

        public event Action<float[], int> Samples;
        public event Action<string> Error;
        public event Action<string> Started;

        private readonly bool _loopback;
        private Thread _thread;
        private volatile bool _running;

        /// <summary>One opened device, with its format and 16 kHz conversion state.</summary>
        private sealed class Endpoint
        {
            public string Id;
            public IMMDevice Device;
            public IAudioClient Client;
            public IAudioCaptureClient Capture;
            public int Channels, BlockAlign, Bits, Rate;
            public bool IsFloat;
            public double Step, Phase, Acc;   // decimation (box filter), kept across packets
            public int AccN;
            public byte[] Buffer = new byte[0];
            public float[] Mono = new float[4096], Out = new float[4096];
            public readonly List<float> Pending = new List<float>(); // converted, not yet mixed
            public long LastDataMs = -10000;
        }

        public WasapiCapture(bool loopback)
        {
            _loopback = loopback;
        }

        public string Kind { get { return _loopback ? "speaker audio" : "microphone"; } }

        public void Start()
        {
            if (_running) return;
            _running = true;
            _thread = new Thread(Run) { IsBackground = true, Name = "wasapi-" + (_loopback ? "loopback" : "mic") };
            _thread.SetApartmentState(ApartmentState.MTA);
            _thread.Start();
        }

        public void Stop()
        {
            _running = false;
            if (_thread != null && _thread != Thread.CurrentThread) _thread.Join(2000);
            _thread = null;
        }

        public void Dispose() { Stop(); }

        private void Run()
        {
            int failures = 0;
            while (_running)
            {
                try
                {
                    CaptureSession();
                    failures = 0;
                }
                catch (Exception ex)
                {
                    failures++;
                    var h = Error;
                    if (h != null && (failures == 1 || failures % 10 == 0))
                        h("Can't capture " + Kind + ": " + ex.Message);
                }
                for (int i = 0; i < 10 && _running; i++) Thread.Sleep(100);
            }
        }

        private static void Check(int hr, string what)
        {
            if (hr < 0) throw new COMException(what + " failed (0x" + hr.ToString("X8") + ")", hr);
        }

        private void CaptureSession()
        {
            IMMDeviceEnumerator enumerator = null;
            var endpoints = new List<Endpoint>();
            try
            {
                enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
                int flow = _loopback ? 0 : 1; // eRender : eCapture
                var roles = _loopback ? new[] { RoleConsole, RoleCommunications } : new[] { RoleConsole };
                var roleIds = new string[roles.Length];
                for (int r = 0; r < roles.Length; r++)
                {
                    IMMDevice device;
                    int hr = enumerator.GetDefaultAudioEndpoint(flow, roles[r], out device);
                    if (hr < 0 || device == null)
                    {
                        if (roles[r] == RoleConsole) Check(hr < 0 ? hr : -1, "No default " + Kind + " device; GetDefaultAudioEndpoint");
                        continue;
                    }
                    string id;
                    device.GetId(out id);
                    roleIds[r] = id;
                    if (endpoints.Any(e => e.Id == id)) { Marshal.ReleaseComObject(device); continue; }
                    var ep = new Endpoint { Id = id, Device = device };
                    endpoints.Add(ep);
                    try { Open(ep); }
                    catch
                    {
                        if (roles[r] == RoleConsole) throw;
                        endpoints.Remove(ep); // the call device failing shouldn't stop the main one
                        Release(ep);
                    }
                }

                var main = endpoints[0];
                var started = Started;
                if (started != null)
                    started(Kind + " @ " + main.Rate + " Hz, " + main.Channels + " ch" + (main.IsFloat ? " float" : " " + main.Bits + "-bit") +
                            (endpoints.Count > 1 ? " + call device" : ""));

                var sw = Stopwatch.StartNew();
                long nextDeviceCheck = 3000;
                while (_running)
                {
                    Thread.Sleep(12);
                    long now = sw.ElapsedMilliseconds;
                    foreach (var ep in endpoints) Drain(ep, now);
                    Mix(endpoints, now);

                    // Re-open when a default device changes (e.g. headphones plugged in, a headset switching to Hands-Free).
                    if (now > nextDeviceCheck)
                    {
                        nextDeviceCheck = now + 3000;
                        bool changed = false;
                        for (int r = 0; r < roles.Length && !changed; r++)
                        {
                            IMMDevice current;
                            if (enumerator.GetDefaultAudioEndpoint(flow, roles[r], out current) < 0 || current == null) continue;
                            string id;
                            current.GetId(out id);
                            Marshal.ReleaseComObject(current);
                            changed = id != roleIds[r];
                        }
                        if (changed) break;
                    }
                }
                foreach (var ep in endpoints) ep.Client.Stop();
            }
            finally
            {
                foreach (var ep in endpoints) Release(ep);
                if (enumerator != null) Marshal.ReleaseComObject(enumerator);
            }
        }

        private void Open(Endpoint ep)
        {
            IntPtr fmt = IntPtr.Zero;
            try
            {
                object o;
                Guid iid = IID_IAudioClient;
                Check(ep.Device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out o), "IMMDevice.Activate");
                ep.Client = (IAudioClient)o;

                Check(ep.Client.GetMixFormat(out fmt), "GetMixFormat");
                int tag = (ushort)Marshal.ReadInt16(fmt, 0);
                ep.Channels = Marshal.ReadInt16(fmt, 2);
                ep.Rate = Marshal.ReadInt32(fmt, 4);
                ep.BlockAlign = Marshal.ReadInt16(fmt, 12);
                ep.Bits = Marshal.ReadInt16(fmt, 14);
                ep.IsFloat = tag == 3;
                if (tag == 0xFFFE)
                {
                    var g = new byte[16];
                    Marshal.Copy(IntPtr.Add(fmt, 24), g, 0, 16);
                    ep.IsFloat = new Guid(g) == SubtypeFloat;
                }

                Check(ep.Client.Initialize(0, _loopback ? AUDCLNT_STREAMFLAGS_LOOPBACK : 0, 10000000, 0, fmt, IntPtr.Zero), "IAudioClient.Initialize");
                Guid ciid = IID_IAudioCaptureClient;
                Check(ep.Client.GetService(ref ciid, out o), "GetService(IAudioCaptureClient)");
                ep.Capture = (IAudioCaptureClient)o;
                ep.Step = ep.Rate / (double)OutRate;
                Check(ep.Client.Start(), "IAudioClient.Start");
            }
            finally
            {
                if (fmt != IntPtr.Zero) Marshal.FreeCoTaskMem(fmt);
            }
        }

        private static void Release(Endpoint ep)
        {
            if (ep.Capture != null) Marshal.ReleaseComObject(ep.Capture);
            if (ep.Client != null) Marshal.ReleaseComObject(ep.Client);
            if (ep.Device != null) Marshal.ReleaseComObject(ep.Device);
            ep.Capture = null;
            ep.Client = null;
            ep.Device = null;
        }

        /// <summary>Reads every waiting packet from one device, converted to 16 kHz mono, into its Pending buffer.</summary>
        private void Drain(Endpoint ep, long now)
        {
            uint packet;
            Check(ep.Capture.GetNextPacketSize(out packet), "GetNextPacketSize");
            while (packet > 0 && _running)
            {
                IntPtr data;
                uint frames, flags;
                ulong devPos, qpc;
                Check(ep.Capture.GetBuffer(out data, out frames, out flags, out devPos, out qpc), "GetBuffer");
                int bytes = (int)frames * ep.BlockAlign;
                if (ep.Buffer.Length < bytes) ep.Buffer = new byte[bytes];
                if ((flags & AUDCLNT_BUFFERFLAGS_SILENT) != 0) Array.Clear(ep.Buffer, 0, bytes);
                else Marshal.Copy(data, ep.Buffer, 0, bytes);
                Check(ep.Capture.ReleaseBuffer(frames), "ReleaseBuffer");

                int n = (int)frames;
                if (ep.Mono.Length < n) { ep.Mono = new float[n]; ep.Out = new float[n + 16]; }
                ToMono(ep.Buffer, n, ep.Channels, ep.BlockAlign, ep.Bits, ep.IsFloat, ep.Mono);
                int outCount = Decimate(ep, ep.Mono, n, ref ep.Out);
                for (int i = 0; i < outCount; i++) ep.Pending.Add(ep.Out[i]);
                ep.LastDataMs = now;

                Check(ep.Capture.GetNextPacketSize(out packet), "GetNextPacketSize");
            }
        }

        /// <summary>
        /// Sums the devices that are playing and raises Samples. A device with nothing playing delivers no packets, so it
        /// drops out after 150 ms; if one stalls, the other isn't held back more than 300 ms.
        /// </summary>
        private void Mix(List<Endpoint> endpoints, long now)
        {
            var active = endpoints.Where(e => e.Pending.Count > 0 || now - e.LastDataMs < 150).ToList();
            if (active.Count == 0) return;
            int min = active.Min(e => e.Pending.Count), max = active.Max(e => e.Pending.Count);
            int count = max > OutRate * 3 / 10 ? max : min;
            if (count == 0) return;
            var mixed = new float[count];
            foreach (var e in active)
            {
                int take = Math.Min(count, e.Pending.Count);
                for (int i = 0; i < take; i++) mixed[i] += e.Pending[i];
                e.Pending.RemoveRange(0, take);
            }
            if (active.Count > 1)
                for (int i = 0; i < count; i++) mixed[i] = Math.Max(-1f, Math.Min(1f, mixed[i]));
            var h = Samples;
            if (h != null) h(mixed, count);
        }

        private static void ToMono(byte[] buf, int frames, int channels, int blockAlign, int bits, bool isFloat, float[] mono)
        {
            int bytesPerSample = bits / 8;
            for (int f = 0; f < frames; f++)
            {
                int off = f * blockAlign;
                float sum = 0;
                for (int c = 0; c < channels; c++)
                {
                    int p = off + c * bytesPerSample;
                    float v;
                    if (isFloat && bits == 32) v = BitConverter.ToSingle(buf, p);
                    else if (isFloat && bits == 64) v = (float)BitConverter.ToDouble(buf, p);
                    else if (bits == 16) v = BitConverter.ToInt16(buf, p) / 32768f;
                    else if (bits == 24) v = ((buf[p] << 8) | (buf[p + 1] << 16) | (buf[p + 2] << 24)) / 2147483648f;
                    else if (bits == 32) v = BitConverter.ToInt32(buf, p) / 2147483648f;
                    else v = 0;
                    sum += v;
                }
                mono[f] = sum / channels;
            }
        }

        /// <summary>Box-filter decimation to 16 kHz (sample-and-hold if the device runs slower).</summary>
        private static int Decimate(Endpoint ep, float[] input, int n, ref float[] output)
        {
            if (ep.Step <= 1.0001)
            {
                int reps = (int)Math.Round(1.0 / Math.Max(ep.Step, 0.01));
                int need = n * Math.Max(1, reps);
                if (output.Length < need) output = new float[need];
                int k = 0;
                for (int i = 0; i < n; i++)
                    for (int r = 0; r < Math.Max(1, reps); r++) output[k++] = input[i];
                return k;
            }
            int count = 0;
            int cap = (int)(n / ep.Step) + 4;
            if (output.Length < cap) output = new float[cap];
            for (int i = 0; i < n; i++)
            {
                ep.Acc += input[i];
                ep.AccN++;
                ep.Phase += 1.0;
                if (ep.Phase >= ep.Step)
                {
                    output[count++] = (float)(ep.Acc / ep.AccN);
                    ep.Acc = 0; ep.AccN = 0;
                    ep.Phase -= ep.Step;
                }
            }
            return count;
        }
    }

    /// <summary>Energy-based voice activity detector that cuts the audio stream into phrases.</summary>
    internal sealed class Segmenter
    {
        private const int FrameLen = 480; // 30 ms at 16 kHz
        private const int FrameMs = 30;
        private const int PreRollFrames = 10;

        public event Action<short[]> Segment;
        public event Action<bool> SpeechChanged;

        public int SilenceMs = 700;
        public int SoftMaxMs = 9000;
        public int HardMaxMs = 15000;
        public float MinThreshold = 0.006f;

        private readonly float[] _frame = new float[FrameLen];
        private int _fill;
        private readonly Queue<float[]> _preRoll = new Queue<float[]>();
        private readonly List<float[]> _seg = new List<float[]>();
        private bool _inSpeech;
        private int _silent, _voiced, _onset;
        private float _noise = 0.002f;
        private long _lastFeedMs;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly object _lock = new object();

        public float LastRms { get; private set; }

        public void Feed(float[] samples, int count)
        {
            lock (_lock)
            {
                _lastFeedMs = _clock.ElapsedMilliseconds;
                for (int i = 0; i < count; i++)
                {
                    _frame[_fill++] = samples[i];
                    if (_fill == FrameLen)
                    {
                        ProcessFrame((float[])_frame.Clone());
                        _fill = 0;
                    }
                }
            }
        }

        /// <summary>Call periodically: loopback capture delivers no packets during silence, so time out open phrases.</summary>
        public void Tick()
        {
            lock (_lock)
            {
                if (_inSpeech && _clock.ElapsedMilliseconds - _lastFeedMs > SilenceMs) Flush();
            }
        }

        private void ProcessFrame(float[] f)
        {
            double sum = 0;
            for (int i = 0; i < f.Length; i++) sum += f[i] * f[i];
            float rms = (float)Math.Sqrt(sum / f.Length);
            LastRms = rms;

            if (!_inSpeech)
            {
                _noise = _noise * 0.96f + Math.Min(rms, _noise * 4f + 0.0005f) * 0.04f;
                _noise = Math.Max(0.0003f, Math.Min(0.05f, _noise));
            }
            float threshold = Math.Max(MinThreshold, _noise * 3.5f);
            bool voiced = rms > threshold;

            if (!_inSpeech)
            {
                _preRoll.Enqueue(f);
                while (_preRoll.Count > PreRollFrames) _preRoll.Dequeue();
                _onset = voiced ? _onset + 1 : 0;
                if (_onset >= 3)
                {
                    _inSpeech = true;
                    _seg.Clear();
                    _seg.AddRange(_preRoll);
                    _preRoll.Clear();
                    _silent = 0;
                    _voiced = _onset;
                    var h = SpeechChanged;
                    if (h != null) h(true);
                }
                return;
            }

            _seg.Add(f);
            if (voiced) { _voiced++; _silent = 0; }
            else _silent++;

            int ms = _seg.Count * FrameMs;
            int silentMs = _silent * FrameMs;
            if (silentMs >= SilenceMs || ms >= HardMaxMs || (ms >= SoftMaxMs && silentMs >= 240))
                Flush();
        }

        private void Flush()
        {
            bool enoughSpeech = _voiced * FrameMs >= 400;
            if (enoughSpeech)
            {
                // Keep ~200 ms of trailing silence.
                int keepFrames = Math.Max(1, _seg.Count - Math.Max(0, _silent - 7));
                var pcm = new short[keepFrames * FrameLen];
                int k = 0;
                for (int i = 0; i < keepFrames; i++)
                {
                    foreach (var v in _seg[i])
                    {
                        float c = Math.Max(-1f, Math.Min(1f, v));
                        pcm[k++] = (short)(c * 32767);
                    }
                }
                var h = Segment;
                if (h != null) h(pcm);
            }
            _seg.Clear();
            _inSpeech = false;
            _silent = 0; _voiced = 0; _onset = 0;
            var sc = SpeechChanged;
            if (sc != null) sc(false);
        }
    }

    internal static class Wav
    {
        public static byte[] Encode(short[] pcm, int rate)
        {
            using (var ms = new MemoryStream(44 + pcm.Length * 2))
            using (var w = new BinaryWriter(ms))
            {
                int dataLen = pcm.Length * 2;
                w.Write(new[] { 'R', 'I', 'F', 'F' });
                w.Write(36 + dataLen);
                w.Write(new[] { 'W', 'A', 'V', 'E', 'f', 'm', 't', ' ' });
                w.Write(16);
                w.Write((short)1);
                w.Write((short)1);
                w.Write(rate);
                w.Write(rate * 2);
                w.Write((short)2);
                w.Write((short)16);
                w.Write(new[] { 'd', 'a', 't', 'a' });
                w.Write(dataLen);
                foreach (var s in pcm) w.Write(s);
                w.Flush();
                return ms.ToArray();
            }
        }
    }
}
