// AudioEngine.cs - records whatever is playing on the default output device (WASAPI loopback)
// into a 16-bit stereo WAV file.
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace MiniRecorder
{
    internal sealed class AudioEngine
    {
        public delegate void FinishedHandler(string wavPath, long framesWritten, int sampleRate, string error);

        /// <summary>Raised on the capture thread when recording ends (normally or because of an error).</summary>
        public event FinishedHandler Finished;

        private Thread _thread;
        private volatile bool _stopRequested;
        private volatile bool _paused;
        private long _framesWritten;
        private int _sampleRate = 48000;
        private float _peak;
        private readonly object _peakLock = new object();
        private string _path;

        public bool IsRecording { get { return _thread != null; } }
        public bool IsPaused { get { return _paused; } set { _paused = value; } }
        public int SampleRate { get { return _sampleRate; } }
        public long FramesWritten { get { return Interlocked.Read(ref _framesWritten); } }

        /// <summary>Largest sample level (0..1) since the last call.</summary>
        public float TakePeak()
        {
            lock (_peakLock)
            {
                float p = _peak;
                _peak = 0;
                return p;
            }
        }

        public void Start(string wavPath)
        {
            if (_thread != null) throw new InvalidOperationException("Already recording.");
            _path = wavPath;
            _stopRequested = false;
            _paused = false;
            Interlocked.Exchange(ref _framesWritten, 0);
            _thread = new Thread(CaptureLoop);
            _thread.IsBackground = true;
            _thread.Name = "Loopback capture";
            _thread.SetApartmentState(ApartmentState.MTA);
            _thread.Start();
        }

        /// <summary>Asks the capture thread to finish. Finished fires once the file is closed.</summary>
        public void Stop()
        {
            _stopRequested = true;
        }

        /// <summary>Stops and blocks until the file is closed (used on exit / shutdown).</summary>
        public void StopAndWait(int timeoutMs)
        {
            Thread t = _thread;
            _stopRequested = true;
            if (t != null) t.Join(timeoutMs);
        }

        private void CaptureLoop()
        {
            IMMDeviceEnumerator enumerator = null;
            IMMDevice device = null;
            IAudioClient captureClient = null;
            IAudioCaptureClient capture = null;
            IAudioClient silenceClient = null;
            IAudioRenderClient silenceRender = null;
            IntPtr mixFormat = IntPtr.Zero;
            WavWriter writer = null;
            string error = null;
            long frames = 0;

            try
            {
                enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
                try
                {
                    enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Console, out device);
                }
                catch (COMException)
                {
                    throw new Exception("No playback device was found. Plug in or enable speakers/headphones and try again.");
                }

                object o;
                Guid iid = CoreAudio.IID_IAudioClient;
                device.Activate(ref iid, CoreAudio.CLSCTX_ALL, IntPtr.Zero, out o);
                captureClient = (IAudioClient)o;
                captureClient.GetMixFormat(out mixFormat);

                SampleFormat fmt = SampleFormat.Read(mixFormat);
                _sampleRate = fmt.SampleRate;

                // 1-second shared-mode loopback buffer, polled every 10 ms.
                captureClient.Initialize(CoreAudio.AUDCLNT_SHAREMODE_SHARED, CoreAudio.AUDCLNT_STREAMFLAGS_LOOPBACK,
                                         10000000, 0, mixFormat, IntPtr.Zero);
                Guid capIid = CoreAudio.IID_IAudioCaptureClient;
                captureClient.GetService(ref capIid, out o);
                capture = (IAudioCaptureClient)o;

                // Loopback delivers nothing while the device is silent, which would cut quiet
                // stretches out of the recording. Playing inaudible silence keeps the clock running.
                int silenceFrames = 0;
                try
                {
                    Guid iid2 = CoreAudio.IID_IAudioClient;
                    device.Activate(ref iid2, CoreAudio.CLSCTX_ALL, IntPtr.Zero, out o);
                    silenceClient = (IAudioClient)o;
                    silenceClient.Initialize(CoreAudio.AUDCLNT_SHAREMODE_SHARED, 0, 10000000, 0, mixFormat, IntPtr.Zero);
                    silenceClient.GetBufferSize(out silenceFrames);
                    Guid rIid = CoreAudio.IID_IAudioRenderClient;
                    silenceClient.GetService(ref rIid, out o);
                    silenceRender = (IAudioRenderClient)o;
                    IntPtr dummy;
                    silenceRender.GetBuffer(silenceFrames, out dummy);
                    silenceRender.ReleaseBuffer(silenceFrames, CoreAudio.AUDCLNT_BUFFERFLAGS_SILENT);
                    silenceClient.Start();
                }
                catch
                {
                    silenceRender = null; // not fatal - capture still works, only gaps may be skipped
                }

                writer = new WavWriter(_path, fmt.SampleRate, 2, 16);
                captureClient.Start();

                byte[] raw = new byte[0];
                byte[] pcm = new byte[0];

                while (!_stopRequested)
                {
                    Thread.Sleep(10);

                    if (silenceRender != null)
                    {
                        int padding;
                        silenceClient.GetCurrentPadding(out padding);
                        int free = silenceFrames - padding;
                        if (free > 0)
                        {
                            IntPtr p;
                            silenceRender.GetBuffer(free, out p);
                            silenceRender.ReleaseBuffer(free, CoreAudio.AUDCLNT_BUFFERFLAGS_SILENT);
                        }
                    }

                    int packet;
                    capture.GetNextPacketSize(out packet);
                    while (packet > 0)
                    {
                        IntPtr data;
                        int numFrames, flags;
                        long devPos, qpcPos;
                        int hr = capture.GetBuffer(out data, out numFrames, out flags, out devPos, out qpcPos);
                        if (hr < 0) Marshal.ThrowExceptionForHR(hr);

                        int rawBytes = numFrames * fmt.BlockAlign;
                        int pcmBytes = numFrames * 4;
                        if (raw.Length < rawBytes) raw = new byte[rawBytes];
                        if (pcm.Length < pcmBytes) pcm = new byte[pcmBytes];

                        float peak = 0;
                        if ((flags & CoreAudio.AUDCLNT_BUFFERFLAGS_SILENT) != 0 || data == IntPtr.Zero)
                        {
                            Array.Clear(pcm, 0, pcmBytes);
                        }
                        else
                        {
                            Marshal.Copy(data, raw, 0, rawBytes);
                            peak = fmt.ToStereo16(raw, numFrames, pcm);
                        }
                        capture.ReleaseBuffer(numFrames);

                        lock (_peakLock) { if (peak > _peak) _peak = peak; }

                        if (!_paused)
                        {
                            writer.Write(pcm, pcmBytes);
                            frames += numFrames;
                            Interlocked.Exchange(ref _framesWritten, frames);
                        }
                        capture.GetNextPacketSize(out packet);
                    }
                }
            }
            catch (Exception ex)
            {
                error = Friendly(ex);
            }
            finally
            {
                try { if (captureClient != null) captureClient.Stop(); } catch { }
                try { if (silenceClient != null) silenceClient.Stop(); } catch { }
                try { if (writer != null) writer.Close(); } catch (Exception ex) { if (error == null) error = ex.Message; }
                if (mixFormat != IntPtr.Zero) Marshal.FreeCoTaskMem(mixFormat);
                Release(silenceRender); Release(silenceClient); Release(capture);
                Release(captureClient); Release(device); Release(enumerator);
            }

            string path = _path;
            _thread = null;
            if (writer == null) path = null; // nothing was written
            FinishedHandler h = Finished;
            if (h != null) h(path, frames, _sampleRate, error);
        }

        private static string Friendly(Exception ex)
        {
            COMException com = ex as COMException;
            if (com != null)
            {
                uint code = (uint)com.ErrorCode;
                if (code == 0x88890004) return "The playback device was disconnected or changed. The recording up to that point was saved.";
                if (code == 0x8889000A) return "The audio device is in use by another app in exclusive mode.";
                return "Audio error 0x" + code.ToString("X8") + ": " + ex.Message;
            }
            return ex.Message;
        }

        private static void Release(object com)
        {
            if (com != null && Marshal.IsComObject(com))
            {
                try { Marshal.ReleaseComObject(com); } catch { }
            }
        }
    }

    /// <summary>Describes the device mix format and converts it to 16-bit stereo.</summary>
    internal sealed class SampleFormat
    {
        public int SampleRate, Channels, BitsPerSample, BlockAlign;
        public bool IsFloat;

        private static readonly Guid SubtypeFloat = new Guid("00000003-0000-0010-8000-00aa00389b71");
        private static readonly Guid SubtypePcm = new Guid("00000001-0000-0010-8000-00aa00389b71");

        public static SampleFormat Read(IntPtr wfx)
        {
            SampleFormat f = new SampleFormat();
            int tag = (ushort)Marshal.ReadInt16(wfx, 0);
            f.Channels = Marshal.ReadInt16(wfx, 2);
            f.SampleRate = Marshal.ReadInt32(wfx, 4);
            f.BlockAlign = Marshal.ReadInt16(wfx, 12);
            f.BitsPerSample = Marshal.ReadInt16(wfx, 14);

            if (tag == 0xFFFE) // WAVE_FORMAT_EXTENSIBLE
            {
                byte[] g = new byte[16];
                Marshal.Copy(new IntPtr(wfx.ToInt64() + 24), g, 0, 16);
                Guid sub = new Guid(g);
                if (sub == SubtypeFloat) f.IsFloat = true;
                else if (sub != SubtypePcm) throw new Exception("Unsupported audio device format.");
            }
            else if (tag == 3) f.IsFloat = true;
            else if (tag != 1) throw new Exception("Unsupported audio device format (tag " + tag + ").");

            if (f.IsFloat && f.BitsPerSample != 32) throw new Exception("Unsupported float format (" + f.BitsPerSample + "-bit).");
            if (!f.IsFloat && f.BitsPerSample != 16 && f.BitsPerSample != 24 && f.BitsPerSample != 32)
                throw new Exception("Unsupported PCM format (" + f.BitsPerSample + "-bit).");
            return f;
        }

        /// <summary>Converts frames to interleaved 16-bit stereo (first two channels). Returns peak 0..1.</summary>
        public float ToStereo16(byte[] src, int frames, byte[] dst)
        {
            int bytesPerSample = BitsPerSample / 8;
            float peak = 0;
            int o = 0;
            for (int i = 0; i < frames; i++)
            {
                int baseIdx = i * BlockAlign;
                for (int c = 0; c < 2; c++)
                {
                    int ch = Channels == 1 ? 0 : c;
                    float v = ReadSample(src, baseIdx + ch * bytesPerSample);
                    if (v > 1f) v = 1f; else if (v < -1f) v = -1f;
                    float a = v < 0 ? -v : v;
                    if (a > peak) peak = a;
                    short s = (short)(v * 32767f);
                    dst[o++] = (byte)(s & 0xFF);
                    dst[o++] = (byte)((s >> 8) & 0xFF);
                }
            }
            return peak;
        }

        private float ReadSample(byte[] b, int i)
        {
            if (IsFloat) return BitConverter.ToSingle(b, i);
            switch (BitsPerSample)
            {
                case 16: return BitConverter.ToInt16(b, i) / 32768f;
                case 24: return ((b[i] << 8) | (b[i + 1] << 16) | (b[i + 2] << 24)) / 2147483648f;
                default: return BitConverter.ToInt32(b, i) / 2147483648f;
            }
        }
    }

    /// <summary>Minimal streaming WAV (RIFF) writer.</summary>
    internal sealed class WavWriter
    {
        private readonly FileStream _fs;
        private long _dataBytes;

        public WavWriter(string path, int sampleRate, int channels, int bits)
        {
            _fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16);
            BinaryWriter w = new BinaryWriter(_fs);
            int blockAlign = channels * bits / 8;
            w.Write(new[] { 'R', 'I', 'F', 'F' });
            w.Write(0);                       // patched on close
            w.Write(new[] { 'W', 'A', 'V', 'E' });
            w.Write(new[] { 'f', 'm', 't', ' ' });
            w.Write(16);
            w.Write((short)1);                // PCM
            w.Write((short)channels);
            w.Write(sampleRate);
            w.Write(sampleRate * blockAlign);
            w.Write((short)blockAlign);
            w.Write((short)bits);
            w.Write(new[] { 'd', 'a', 't', 'a' });
            w.Write(0);                       // patched on close
            w.Flush();
        }

        public void Write(byte[] buffer, int count)
        {
            _fs.Write(buffer, 0, count);
            _dataBytes += count;
        }

        public void Close()
        {
            uint data = _dataBytes > uint.MaxValue - 36 ? uint.MaxValue - 36 : (uint)_dataBytes;
            _fs.Seek(4, SeekOrigin.Begin);
            _fs.Write(BitConverter.GetBytes(data + 36), 0, 4);
            _fs.Seek(40, SeekOrigin.Begin);
            _fs.Write(BitConverter.GetBytes(data), 0, 4);
            _fs.Close();
        }
    }
}
