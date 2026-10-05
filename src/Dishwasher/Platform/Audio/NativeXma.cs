// NativeXma.cs -- PORT (public build): P/Invoke into libdishaudio.so, the small
// native bridge that decodes Xbox 360 XMA/XMA2 using a minimal dynamically
// linked FFmpeg (libavcodec/libavutil, LGPL-2.1-or-later) and, for long tracks,
// encodes OGG Vorbis via statically linked libvorbis/libogg (BSD).
//
// The library is packaged only for arm64-v8a.  Every entry point is wrapped so
// a missing library (desktop harness, other ABI) degrades to "audio not
// derived" instead of crashing.
using System;
using System.Runtime.InteropServices;

namespace Dishwasher.Import
{
    internal static class NativeXma
    {
        private const string Lib = "dishaudio";

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        private static extern int dw_xma_to_file(byte[] data, int size, int channels, int rate,
                                                 long numSamples, string outPath, int outFmt);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        private static extern int dw_pcm16be_to_wav(byte[] data, int size, int channels, int rate,
                                                    string outPath);

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
        private static extern int dw_version();

        /// <summary>True if libdishaudio.so can be loaded on this device.</summary>
        public static bool Available { get; private set; }
        private static bool _probed;
        private static string _probeError;

        public static string ProbeError => _probeError;

        public static int Version { get; private set; }

        public static bool Probe()
        {
            if (_probed) return Available;
            _probed = true;
            try
            {
                Version = dw_version();
                Available = true;
            }
            catch (Exception ex)
            {
                _probeError = ex.GetType().Name + ": " + ex.Message;
                Available = false;
            }
            return Available;
        }

        /// <summary>outFmt: 0 = PCM16 WAV, 1 = OGG Vorbis.  Returns 0 on success.</summary>
        public static int XmaToFile(byte[] data, int channels, int rate, long numSamples,
                                    string outPath, int outFmt)
        {
            try
            {
                return dw_xma_to_file(data, data.Length, channels, rate, numSamples, outPath, outFmt);
            }
            catch (Exception ex)
            {
                _probeError = ex.GetType().Name + ": " + ex.Message;
                return -100;
            }
        }

        public static int Pcm16BeToWav(byte[] data, int channels, int rate, string outPath)
        {
            try
            {
                return dw_pcm16be_to_wav(data, data.Length, channels, rate, outPath);
            }
            catch (Exception ex)
            {
                _probeError = ex.GetType().Name + ": " + ex.Message;
                return -100;
            }
        }
    }
}
