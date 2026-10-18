using System;
using System.IO;

namespace GodotOnnx.Inference
{
    /// <summary>16bit / mono の WAV を書き出す。</summary>
    public static class WaveWriter
    {
        /// <summary>int16 PCM を header + data として書き込む。</summary>
        /// <param name="path">保存先パス</param>
        /// <param name="samples">int16 PCM サンプル</param>
        /// <param name="sampleRate">サンプリングレート (Hz)</param>
        public static void Write16BitMono(string path, short[] samples, int sampleRate)
        {
            using var fs = File.Create(path);
            using var w = new BinaryWriter(fs);

            const short channels = 1;
            const short bitsPerSample = 16;
            const short blockAlign = channels * (short)(bitsPerSample / 8);
            var byteRate = sampleRate * blockAlign;
            var dataSize = samples.Length * blockAlign;

            w.Write("RIFF"u8);
            w.Write(36 + dataSize);
            w.Write("WAVE"u8);

            w.Write("fmt "u8);
            w.Write(16);
            w.Write((short)1);          // PCM
            w.Write(channels);
            w.Write(sampleRate);
            w.Write(byteRate);
            w.Write(blockAlign);
            w.Write(bitsPerSample);

            w.Write("data"u8);
            w.Write(dataSize);
            foreach (var s in samples)
            {
                w.Write(s);
            }
        }
    }
}
