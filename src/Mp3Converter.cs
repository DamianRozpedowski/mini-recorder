// Mp3Converter.cs - turns the recorded WAV into an MP3 with Windows' built-in Media Foundation
// MP3 encoder (Windows 8 and later). No extra DLLs needed.
using System;
using System.IO;
using System.Runtime.InteropServices;

namespace MiniRecorder
{
    internal static class Mp3Converter
    {
        /// <summary>Converts a 16-bit stereo WAV written by WavWriter to MP3. Throws on failure.</summary>
        public static void Convert(string wavPath, string mp3Path, int kbps)
        {
            int sampleRate, channels;
            using (FileStream fs = new FileStream(wavPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                BinaryReader r = new BinaryReader(fs);
                fs.Seek(22, SeekOrigin.Begin);
                channels = r.ReadInt16();
                sampleRate = r.ReadInt32();
            }
            if (sampleRate != 32000 && sampleRate != 44100 && sampleRate != 48000)
                throw new Exception("MP3 supports 32, 44.1 or 48 kHz audio; your device runs at " + sampleRate +
                                    " Hz. Kept the WAV file instead.");

            MF.MFStartup(MF.MF_VERSION, 0);
            IMFSinkWriter writer = null;
            IMFMediaType outType = null, inType = null;
            try
            {
                outType = PickMp3Type(sampleRate, channels, kbps);
                if (File.Exists(mp3Path)) File.Delete(mp3Path);
                MF.MFCreateSinkWriterFromURL(mp3Path, IntPtr.Zero, IntPtr.Zero, out writer);
                int stream;
                writer.AddStream(outType, out stream);

                MF.MFCreateMediaType(out inType);
                SetGuid(inType, MF.MF_MT_MAJOR_TYPE, MF.MFMediaType_Audio);
                SetGuid(inType, MF.MF_MT_SUBTYPE, MF.MFAudioFormat_PCM);
                SetInt(inType, MF.MF_MT_AUDIO_BITS_PER_SAMPLE, 16);
                SetInt(inType, MF.MF_MT_AUDIO_SAMPLES_PER_SECOND, sampleRate);
                SetInt(inType, MF.MF_MT_AUDIO_NUM_CHANNELS, channels);
                SetInt(inType, MF.MF_MT_AUDIO_BLOCK_ALIGNMENT, channels * 2);
                SetInt(inType, MF.MF_MT_AUDIO_AVG_BYTES_PER_SECOND, sampleRate * channels * 2);
                SetInt(inType, MF.MF_MT_ALL_SAMPLES_INDEPENDENT, 1);
                writer.SetInputMediaType(stream, inType, IntPtr.Zero);
                writer.BeginWriting();

                int bytesPerSec = sampleRate * channels * 2;
                byte[] chunk = new byte[bytesPerSec]; // 1 second per sample
                long position = 0; // in 100-ns units
                using (FileStream fs = new FileStream(wavPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    fs.Seek(44, SeekOrigin.Begin);
                    int read;
                    while ((read = ReadFull(fs, chunk)) > 0)
                    {
                        read -= read % (channels * 2);
                        if (read == 0) break;
                        long duration = (long)read * 10000000L / bytesPerSec;
                        WriteChunk(writer, stream, chunk, read, position, duration);
                        position += duration;
                    }
                }
                writer.DoFinalize();
            }
            finally
            {
                if (writer != null) Marshal.ReleaseComObject(writer);
                if (inType != null) Marshal.ReleaseComObject(inType);
                if (outType != null) Marshal.ReleaseComObject(outType);
                try { MF.MFShutdown(); } catch { }
            }
        }

        private static IMFMediaType PickMp3Type(int sampleRate, int channels, int kbps)
        {
            Guid mp3 = MF.MFAudioFormat_MP3;
            IMFCollection types;
            try
            {
                MF.MFTranscodeGetAudioOutputAvailableTypes(ref mp3, MF.MFT_ENUM_FLAGS, IntPtr.Zero, out types);
            }
            catch (Exception)
            {
                throw new Exception("This copy of Windows has no MP3 encoder (N editions need the Media Feature Pack). Kept the WAV file instead.");
            }

            IMFMediaType best = null;
            int bestDiff = int.MaxValue;
            int target = kbps * 1000 / 8;
            try
            {
                int count;
                types.GetElementCount(out count);
                for (int i = 0; i < count; i++)
                {
                    object o;
                    types.GetElement(i, out o);
                    IMFMediaType t = o as IMFMediaType;
                    if (t == null) { Marshal.ReleaseComObject(o); continue; }
                    int rate = GetInt(t, MF.MF_MT_AUDIO_SAMPLES_PER_SECOND);
                    int ch = GetInt(t, MF.MF_MT_AUDIO_NUM_CHANNELS);
                    int avg = GetInt(t, MF.MF_MT_AUDIO_AVG_BYTES_PER_SECOND);
                    int diff = Math.Abs(avg - target);
                    if (rate == sampleRate && ch == channels && diff < bestDiff)
                    {
                        if (best != null) Marshal.ReleaseComObject(best);
                        best = t;
                        bestDiff = diff;
                    }
                    else
                    {
                        Marshal.ReleaseComObject(t);
                    }
                }
            }
            finally
            {
                Marshal.ReleaseComObject(types);
            }
            if (best == null) throw new Exception("No MP3 setting matches " + sampleRate + " Hz stereo. Kept the WAV file instead.");
            return best;
        }

        private static void WriteChunk(IMFSinkWriter writer, int stream, byte[] data, int length, long time, long duration)
        {
            IMFMediaBuffer buffer = null;
            IMFSample sample = null;
            try
            {
                MF.MFCreateMemoryBuffer(length, out buffer);
                IntPtr ptr;
                int max, cur;
                buffer.Lock(out ptr, out max, out cur);
                Marshal.Copy(data, 0, ptr, length);
                buffer.Unlock();
                buffer.SetCurrentLength(length);

                MF.MFCreateSample(out sample);
                sample.AddBuffer(buffer);
                sample.SetSampleTime(time);
                sample.SetSampleDuration(duration);
                writer.WriteSample(stream, sample);
            }
            finally
            {
                if (sample != null) Marshal.ReleaseComObject(sample);
                if (buffer != null) Marshal.ReleaseComObject(buffer);
            }
        }

        private static int ReadFull(Stream s, byte[] buf)
        {
            int total = 0;
            while (total < buf.Length)
            {
                int n = s.Read(buf, total, buf.Length - total);
                if (n <= 0) break;
                total += n;
            }
            return total;
        }

        private static void SetGuid(IMFMediaType t, Guid key, Guid value) { t.SetGUID(ref key, ref value); }
        private static void SetInt(IMFMediaType t, Guid key, int value) { t.SetUINT32(ref key, value); }
        private static int GetInt(IMFMediaType t, Guid key)
        {
            int v;
            return t.GetUINT32(ref key, out v) >= 0 ? v : -1;
        }
    }
}
