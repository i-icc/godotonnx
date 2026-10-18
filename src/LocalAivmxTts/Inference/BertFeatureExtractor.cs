using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LocalAivmxTts.Nlp;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace LocalAivmxTts.Inference
{
    /// <summary>
    /// DeBERTa-v2 日本語文字レベル BERT (ONNX) から音素列長分の特徴量を抽出する
    /// （Style-Bert-VITS2 <c>extract_bert_feature_onnx</c> 相当）。
    /// </summary>
    /// <remarks>
    /// 参照実装の流れ:
    /// <list type="bullet">
    /// <item>tokenizer(text) → [CLS] + 文字列 + [SEP]（int64 の input_ids / attention_mask）</item>
    /// <item>ONNX 出力 (S, 1024)：S = len(text) + 2</item>
    /// <item>word2ph[i] 個ずつ res[i] を tile → (L, 1024) → 転置 (1024, L)</item>
    /// </list>
    /// </remarks>
    public sealed class BertFeatureExtractor : IDisposable
    {
        const int HiddenSize = 1024;

        readonly InferenceSession session;

        readonly string inputIdsName;
        readonly string attentionMaskName;

        /// <summary>トークナイザー。</summary>
        public BertTokenizer Tokenizer { get; }

        /// <summary>
        /// BERT ONNX セッションを構築する。
        /// </summary>
        /// <param name="modelPath">model_fp16.onnx などの ONNX モデルパス</param>
        /// <param name="tokenizerJsonPath">tokenizer.json パス</param>
        /// <param name="intraOpThreads">intra-op スレッド数 (0 で既定)</param>
        public BertFeatureExtractor(string modelPath, string tokenizerJsonPath, int intraOpThreads = 0)
        {
            Tokenizer = new BertTokenizer(tokenizerJsonPath);

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

            session = new InferenceSession(modelPath, options);

            string? ids = null, mask = null;
            foreach (var name in session.InputNames)
            {
                if (name.Contains("ids", StringComparison.Ordinal))
                {
                    ids = name;
                }
                else if (name.Contains("mask", StringComparison.Ordinal))
                {
                    mask = name;
                }
            }
            if (ids is null || mask is null)
            {
                throw new InvalidDataException(
                    $"BERT ONNX モデルに input_ids / attention_mask 入力が見つかりません: [{string.Join(", ", session.InputNames)}]");
            }
            inputIdsName = ids;
            attentionMaskName = mask;
        }

        /// <summary>
        /// テキスト（sep_text 連結文字列、空白・制御文字なし）から BERT 特徴量 (1024, L) を抽出する。
        /// </summary>
        /// <param name="text">sep_text 連結文字列</param>
        /// <remarks>word2ph は外部から受け取らず、<paramref name="wordCountPerToken"/> を切换る。</remarks>
        public float[,] Extract(string text, IReadOnlyList<int> wordCountPerToken)
        {
            var (inputIds, attentionMask, _) = Tokenizer.Encode(text);

            if (wordCountPerToken.Count != inputIds.Length)
            {
                throw new InvalidOperationException(
                    $"word2ph 長 ({wordCountPerToken.Count}) とトークン数 ({inputIds.Length}) が不一致です (text={text})");
            }

            var idsTensor = new DenseTensor<long>(inputIds.AsMemory(), new[] { 1, inputIds.Length }, false);
            var maskTensor = new DenseTensor<long>(attentionMask.AsMemory(), new[] { 1, attentionMask.Length }, false);

            var inputs = new List<NamedOnnxValue>(2)
            {
                NamedOnnxValue.CreateFromTensor(inputIdsName, idsTensor),
                NamedOnnxValue.CreateFromTensor(attentionMaskName, maskTensor),
            };

            float[,] res;
            using (var result = session.Run(inputs))
            {
                res = ToFloat2(result[0]); // (S, 1024) ※ S = len(text) + 2
            }

            var length = wordCountPerToken.Sum();
            var phoneLevel = new float[HiddenSize, length];
            var idx = 0;
            for (var i = 0; i < res.GetLength(0); i++)
            {
                var count = wordCountPerToken[i];
                for (var t = 0; t < count; t++)
                {
                    for (var d = 0; d < HiddenSize; d++)
                    {
                        phoneLevel[d, idx] = res[i, d];
                    }
                    idx++;
                }
            }
            if (idx != length)
            {
                throw new InvalidOperationException($"音素数が word2ph 合計に一致しません ({idx} != {length})");
            }

            return phoneLevel;
        }

        /// <summary>ONNX 出力を (S, 1024) の float 二次元配列に変換する（fp16 対応）。</summary>
        static float[,] ToFloat2(DisposableNamedOnnxValue output)
        {
            // まず float32 で受ける
            if (output.ElementType == TensorElementType.Float)
            {
                var tensor = output.AsTensor<float>();
                return tensor.ToArray().Reshape(tensor.Dimensions);
            }
            if (output.ElementType == TensorElementType.Float16)
            {
                var tensor16 = output.AsTensor<Microsoft.ML.OnnxRuntime.Float16>();
                var raw = tensor16.ToArray();
                var dims = tensor16.Dimensions.ToArray();
                var rows = dims[0];
                var cols = dims.Length > 1 ? dims[1] : 1;
                var result = new float[rows, cols];
                var idx = 0;
                for (var r = 0; r < rows; r++)
                {
                    for (var c = 0; c < cols; c++)
                    {
                        result[r, c] = raw[idx].ToFloat();
                        idx++;
                    }
                }
                return result;
            }
            throw new InvalidDataException($"BERT 出力の型 ({output.ElementType}) は対応していません");
        }

        /// <summary>入力名の確認用。</summary>
        public IReadOnlyList<string> InputNames => session.InputNames;

        public void Dispose()
        {
            session.Dispose();
        }
    }

    /// <summary>フラット配列を形状に合わせて扱うためのヘルパー。</summary>
    file static class FloatArrayExtensions
    {
        /// <summary><see cref="Tensor{T}.ToArray"/> の結果 (行優先) を (行数, 列数) の二次元配列へ。</summary>
        public static float[,] Reshape(this float[] flat, ReadOnlySpan<int> dims)
        {
            if (dims.Length != 2)
            {
                throw new InvalidDataException($"期待した形状は 2 次元ですが {dims.Length} 次元です");
            }
            var rows = dims[0];
            var cols = dims[1];
            if (flat.Length != rows * cols)
            {
                throw new InvalidDataException($"形状不一致: データ {flat.Length} 個 != ({rows}, {cols})");
            }
            var result = new float[rows, cols];
            var idx = 0;
            for (var r = 0; r < rows; r++)
            {
                for (var c = 0; c < cols; c++)
                {
                    result[r, c] = flat[idx];
                    idx++;
                }
            }
            return result;
        }
    }
}
