using System;
using System.Collections.Generic;
using System.IO;
using GodotOnnx.Model;
using GodotOnnx.Nlp;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using DotNetG2P;

namespace GodotOnnx.Inference
{
    /// <summary>合成オプション（Style-Bert-VITS2 の推論パラメータ）。</summary>
    /// <param name="StyleId">スタイル ID (0..num_styles-1)</param>
    /// <param name="StyleWeight">スタイル適用強さ (既定 1.0)</param>
    /// <param name="SpeedScale">話速スケール (既定 1.0)。length_scale = 1 / max(0.1, speed)</param>
    /// <param name="SdpRatio">Stochastic Duration Predictor の混合比 (既定 0.2)</param>
    /// <param name="NoiseScale">DP へのノイズ (既定 0.6)</param>
    /// <param name="NoiseScaleW">SDP へのノイズ (既定 0.8)</param>
    public sealed record SynthesisOptions(
        int StyleId = 0,
        float StyleWeight = 1.0f,
        float SpeedScale = 1.0f,
        float SdpRatio = 0.2f,
        float NoiseScale = 0.6f,
        float NoiseScaleW = 0.8f)
    {
        /// <summary>AivisSpeech / Style-Bert-VITS2 の既定値。</summary>
        public static SynthesisOptions Default => new();
    }

    /// <summary>合成結果。</summary>
    /// <param name="Audio">float32 PCM (-1..1)</param>
    /// <param name="SampleRate">サンプリングレート</param>
    /// <param name="NormText">正規化後テキスト</param>
    /// <param name="Phones">音素列（intersperse 済みモデル入力）</param>
    /// <param name="Tones">tone（0/1）列（intersperse 済みモデル入力）</param>
    /// <param name="StyleVec">適用したスタイルベクトル</param>
    public sealed record SynthesisResult(
        float[] Audio,
        int SampleRate,
        string NormText,
        string[] Phones,
        int[] Tones,
        float[] StyleVec)
    {
        /// <summary>Audio を int16 サンプルに変換する（clip → ×32768）。</summary>
        public short[] ToInt16()
        {
            var result = new short[Audio.Length];
            for (var i = 0; i < Audio.Length; i++)
            {
                var v = Audio[i];
                if (v > 1f) v = 1f;
                if (v < -1f) v = -1f;
                result[i] = (short)MathF.Round(v * 32768f);
            }
            return result;
        }

        /// <summary>Audio を 16bit / mono の WAV として保存する。</summary>
        public void SaveWave(string path) => WaveWriter.Write16BitMono(path, ToInt16(), SampleRate);
    }

    /// <summary>合成エラー。</summary>
    public class TtsException : Exception
    {
        public TtsException(string message) : base(message) { }
        public TtsException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>
    /// AIVMX (Style-Bert-VITS2 ONNX) を用いたローカル音声合成エンジン。
    /// </summary>
    /// <remarks>
    /// G2P (DotNetG2P) → BERT (DeBERTa-v2 JP char ONNX) → 音声モデル (AIVMX ONNX)
    /// の 3 モデルを ONNX Runtime で連結し、PCM を生成する。
    /// 参照実装の infer_onnx.py / bert_feature.py と同じ構成である。
    ///
    /// 注意: 音声モデル内の flow-decoder の z_p サンプリングはランダム正規分布
    /// (RandomNormalLike) を含むため、同一入力でも生成波形はサンプルレベルでは
    /// 実行ごとに変わる（AivisSpeech Engine と同様）。
    /// </remarks>
    public sealed class Sbv2Synthesizer : IDisposable
    {
        readonly AivmxPackage package;
        readonly InferenceSession voiceSession;
        readonly BertFeatureExtractor bert;
        readonly G2PEngine g2pEngine;
        readonly object inferenceLock = new();

        /// <summary>ハイパーパラメータ。</summary>
        public HyperParameters HyperParameters { get; }

        /// <summary>サンプリングレート。</summary>
        public int SampleRate => HyperParameters.SamplingRate;

        /// <summary>
        /// 合成エンジンを構築する。
        /// </summary>
        /// <param name="aivmxPath">.aivmx モデルパス</param>
        /// <param name="bertModelPath">BERT ONNX モデルパス (model_fp16.onnx)</param>
        /// <param name="tokenizerJsonPath">BERT tokenizer.json パス</param>
        /// <param name="naistJdicPath">naist-jdic 辞書ディレクトリ (MeCab)</param>
        /// <param name="intraOpThreads">intra-op スレッド数 (0 で既定)</param>
        public Sbv2Synthesizer(
            string aivmxPath,
            string bertModelPath,
            string tokenizerJsonPath,
            string naistJdicPath,
            int intraOpThreads = 0)
        {
            package = AivmxLoader.Load(aivmxPath);
            HyperParameters = package.HyperParameters;

            if (!HyperParameters.Version.EndsWith("JP-Extra", StringComparison.Ordinal))
            {
                // JP-Extra 外は BERT 入力構成が異なる (bert/ja_bert/en_bert が別入力) ため当面未対応。
                throw new TtsException($"JP-Extra 以外のモデル (version={HyperParameters.Version}) は未対応です");
            }

            g2pEngine = Sbv2JapaneseG2P.CreateEngine(naistJdicPath);
            bert = new BertFeatureExtractor(bertModelPath, tokenizerJsonPath, intraOpThreads);

            var options = new SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                EnableMemoryPattern = false,
            };
            if (intraOpThreads > 0)
            {
                options.IntraOpNumThreads = intraOpThreads;
            }
            options.AppendExecutionProvider_CPU(0);
            voiceSession = new InferenceSession(aivmxPath, options);
        }

        /// <summary>話者 ID (spk2id) 辞書。</summary>
        public IReadOnlyDictionary<string, int> Spk2Id => HyperParameters.Data.Spk2Id;

        /// <summary>スタイル ID (style2id) 辞書。</summary>
        public IReadOnlyDictionary<string, int> Style2Id => HyperParameters.Style2Id;

        /// <summary>
        /// 正規化済みテキストに発音可能な文字 (句読点・空白以外) が 1 文字でもあるか。
        /// 許可文字集合は <see cref="JapaneseTextNormalizer.ReplacePunctuation"/> の
        /// クリーンアップ正規表現と同一範囲。
        /// </summary>
        static bool HasSpeechCharacters(string normText)
        {
            foreach (var c in normText)
            {
                if (c is '!' or '?' or '…' or ',' or '.' or '\'' or '-' || char.IsWhiteSpace(c))
                {
                    continue;
                }
                return true;
            }
            return false;
        }

        /// <summary>
        /// テキストを音声合成する（同期・排他）。
        /// </summary>
        /// <param name="text">日本語テキスト</param>
        /// <param name="options">合成オプション</param>
        /// <param name="speakerId">話者 ID (既定 0)</param>
        public SynthesisResult Synthesize(string text, SynthesisOptions? options = null, int speakerId = 0)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                throw new TtsException("テキストが空です");
            }
            options ??= SynthesisOptions.Default;

            lock (inferenceLock)
            {
                // 1) 正規化 + G2P
                var normText = JapaneseTextNormalizer.Normalize(text);

                // 句読点のみ (発音可能な文字がない) 場合は短い無音を返す。
                // 参照実装 (style_bert_vits2 __fix_phone_tone) は音素 0 個で ValueError になるため、
                // ライブラリとしては上位で握りつぶす代わりに無音 0.25 秒を返す。
                if (!HasSpeechCharacters(normText))
                {
                    var silence = new float[SampleRate / 4];
                    return new SynthesisResult(
                        Audio: silence,
                        SampleRate: SampleRate,
                        NormText: normText,
                        Phones: [],
                        Tones: [],
                        StyleVec: ComputeStyleVector(options.StyleId, options.StyleWeight));
                }

                var g2p = Sbv2JapaneseG2P.G2p(normText, g2pEngine, useJpExtra: true);

                // 2) 音素 ID 列化 (SYMBOLS 表 / tone+6 / lang=1)
                var phoneIds = new long[g2p.Phones.Length];
                var toneIds = new long[g2p.Phones.Length];
                var langIds = new long[g2p.Phones.Length];
                for (var i = 0; i < g2p.Phones.Length; i++)
                {
                    var phone = g2p.Phones[i];
                    var id = SymbolTable.GetId(phone);
                    if (id < 0)
                    {
                        throw new TtsException($"未知の音素記号: {phone}");
                    }
                    phoneIds[i] = id;
                    toneIds[i] = g2p.Tones[i] + SymbolTable.ToneOffsetJp;
                    langIds[i] = SymbolTable.LanguageJp;
                }

                // 3) add_blank: 補助記号の intersperse（参照 infer_onnx.get_text_onnx と同一）
                var word2ph = new int[g2p.Word2Ph.Length];
                Array.Copy(g2p.Word2Ph, word2ph, word2ph.Length);
                if (HyperParameters.AddBlank)
                {
                    phoneIds = Intersperse(phoneIds, 0);
                    toneIds = Intersperse(toneIds, 0);
                    langIds = Intersperse(langIds, 0);
                    for (var i = 0; i < word2ph.Length; i++)
                    {
                        word2ph[i] *= 2;
                    }
                    word2ph[0] += 1;
                }

                // 4) BERT 特徴量 (1024, L)
                var bertFeature = bert.Extract(g2p.BertText, word2ph);
                var length = (int)bertFeature.GetLength(1);
                if (length != phoneIds.Length)
                {
                    throw new TtsException($"音素数と BERT 特徴長が不一致: {phoneIds.Length} != {length}");
                }

                // 5) スタイルベクトル (mean + (row - mean) * weight)
                var styleVec = ComputeStyleVector(options.StyleId, options.StyleWeight);

                // 6) ONNX 推論 (length_scale = 1 / max(0.1, speedScale))
                var lengthScale = 1f / MathF.Max(0.1f, options.SpeedScale);
                var audio = Run(
                    phoneIds, toneIds, langIds, bertFeature, styleVec,
                    speakerId, lengthScale,
                    options.SdpRatio, options.NoiseScale, options.NoiseScaleW);

                return new SynthesisResult(
                    Audio: audio,
                    SampleRate: SampleRate,
                    NormText: normText,
                    Phones: g2p.Phones,
                    Tones: g2p.Tones,
                    StyleVec: styleVec);
            }
        }

        /// <summary>
        /// スタイルベクトルを計算する（tts_model.get_style_vector と同一式:
        /// mean=style_vectors[0] を中心に weight ブレンド）。
        /// </summary>
        public float[] ComputeStyleVector(int styleId, float weight)
        {
            if (styleId < 0 || styleId >= package.NumStyles)
            {
                throw new TtsException($"style_id {styleId} は範囲外です (0..{package.NumStyles - 1})");
            }
            var mean = package.StyleVectors.GetStyleVectorLine(0);
            var line = package.StyleVectors.GetStyleVectorLine(styleId);
            var result = new float[line.Length];
            for (var i = 0; i < line.Length; i++)
            {
                result[i] = mean[i] + (line[i] - mean[i]) * weight;
            }
            return result;
        }

        /// <summary>
        /// 音声モデル ONNX を実行して float32 PCM を得る。
        /// </summary>
        /// <remarks>
        /// JP-Extra 本体モデルの入力順（参照 infer_onnx.infer_onnx で確認済み）:
        /// [x_tst, x_tst_lengths, sid, tones, language, bert(ja), style_vec,
        ///  (スカラー) length_scale, sdp_ratio, noise_scale, noise_scale_w]
        /// 名前が既知の入力は名前で、それ以外（スカラー 4 種）はグラフ宣言順に割当てる。
        /// </remarks>
        float[] Run(
            long[] phoneIds, long[] toneIds, long[] langIds,
            float[,] bertFeature, float[] styleVec,
            int speakerId, float lengthScale,
            float sdpRatio, float noiseScale, float noiseScaleW)
        {
            var length = phoneIds.Length;
            var styleDim = styleVec.Length;

            var xTst = new DenseTensor<long>(phoneIds.AsMemory(), new[] { 1, length }, false);
            var xTstLengths = new DenseTensor<long>(new long[] { length }, new[] { 1 }, false);
            var sid = new DenseTensor<long>(new long[] { speakerId }, new[] { 1 }, false);
            var tones = new DenseTensor<long>(toneIds.AsMemory(), new[] { 1, length }, false);
            var language = new DenseTensor<long>(langIds.AsMemory(), new[] { 1, length }, false);
            var styleVecTensor = new DenseTensor<float>(styleVec.AsMemory(), new[] { 1, styleDim }, false);

            // bert は (1, 1024, L): bertFeature は (1024, L) 行優先なのでそのまま連結
            var bertFlat = new float[1024 * length];
            var idx = 0;
            for (var d = 0; d < 1024; d++)
            {
                for (var t = 0; t < length; t++)
                {
                    bertFlat[idx++] = bertFeature[d, t];
                }
            }
            var bertTensor = new DenseTensor<float>(bertFlat, new[] { 1, 1024, length }, false);

            var tensorInputs = new Dictionary<string, NamedOnnxValue>(StringComparer.Ordinal)
            {
                ["x_tst"] = NamedOnnxValue.CreateFromTensor("x_tst", xTst),
                ["x_tst_lengths"] = NamedOnnxValue.CreateFromTensor("x_tst_lengths", xTstLengths),
                ["sid"] = NamedOnnxValue.CreateFromTensor("sid", sid),
                ["tones"] = NamedOnnxValue.CreateFromTensor("tones", tones),
                ["language"] = NamedOnnxValue.CreateFromTensor("language", language),
                ["bert"] = NamedOnnxValue.CreateFromTensor("bert", bertTensor),
                ["style_vec"] = NamedOnnxValue.CreateFromTensor("style_vec", styleVecTensor),
            };

            var scalarQueue = new Queue<float>(new[]
            {
                lengthScale, sdpRatio, noiseScale, noiseScaleW,
            });

            var inputs = new List<NamedOnnxValue>(tensorInputs.Count + scalarQueue.Count);
            foreach (var name in voiceSession.InputNames)
            {
                if (!tensorInputs.Remove(name, out var value))
                {
                    // スカラー入力: グラフ宣言順に length_scale → sdp_ratio → noise_scale → noise_scale_w
                    if (scalarQueue.Count == 0)
                    {
                        throw new TtsException($"未知の入力 {name} に対する値がありません");
                    }
                    var v = new DenseTensor<float>(new[] { scalarQueue.Dequeue() }, Array.Empty<int>(), false);
                    value = NamedOnnxValue.CreateFromTensor(name, v);
                }
                inputs.Add(value);
            }
            if (tensorInputs.Count > 0)
            {
                throw new TtsException(
                    $"モデルに入力が見つかりませんでした: [{string.Join(", ", tensorInputs.Keys)}]");
            }

            using (var output = voiceSession.Run(inputs))
            {
                var value = output[0];
                return value.ElementType switch
                {
                    TensorElementType.Float => value.AsTensor<float>().ToArray(),
                    TensorElementType.Float16 => ToFloatArray(value.AsTensor<Microsoft.ML.OnnxRuntime.Float16>().ToArray()),
                    _ => throw new TtsException($"音声モデル出力の型 ({value.ElementType}) は対応していません"),
                };
            }
        }

        static float[] ToFloatArray(Microsoft.ML.OnnxRuntime.Float16[] raw)
        {
            var result = new float[raw.Length];
            for (var i = 0; i < raw.Length; i++)
            {
                result[i] = raw[i].ToFloat();
            }
            return result;
        }

        /// <summary>リストの要素の間に item を挿入する（commons.intersperse 相当）。</summary>
        static long[] Intersperse(long[] list, long item)
        {
            var result = new long[list.Length * 2 + 1];
            for (var i = 0; i < result.Length; i++)
            {
                result[i] = item;
            }
            for (var i = 0; i < list.Length; i++)
            {
                result[2 * i + 1] = list[i];
            }
            return result;
        }

        public void Dispose()
        {
            voiceSession.Dispose();
            bert.Dispose();
        }
    }
}
