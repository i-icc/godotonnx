using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GodotOnnx.Nlp
{
    /// <summary>
    /// BERT トークナイザー（research/bert/tokenizer.json 相当）。
    /// </summary>
    /// <remarks>
    /// 参照実装の設定:
    /// - normalizer = BertNormalizer(clean_text=true, handle_chinese_chars=false,
    ///   strip_accents=false, lowercase=false) + NFKC
    /// - pre_tokenizer = WhitespaceSplit + 文字単位 Split（1 文字 = 1 トークン）
    /// - post_processor = TemplateProcessing: [CLS] text [SEP]
    /// - model = WordLevel、vocab 22,012 語、未知語は [UNK]
    ///
    /// このパイプラインでは正規化済みテキスト（空白・制御文字は既に除去済み）を入力するため、
    /// normalizer は NFKC（冪等）と空白・制御文字の近似クリーンのみを適用する。
    /// </remarks>
    public sealed class BertTokenizer
    {
        const int PadId = 0;
        const int ClsId = 1;
        const int SepId = 2;
        const int UnkId = 3;

        /// <summary>語彙（token → id）。vocab.txt の行番号ではなく tokenizer.json の ID が真実。</summary>
        public IReadOnlyDictionary<string, int> Vocab { get; }

        /// <summary>トークナイザーを構築する。</summary>
        /// <param name="tokenizerJsonPath">tokenizer.json のパス</param>
        public BertTokenizer(string tokenizerJsonPath)
        {
            using var stream = File.OpenRead(tokenizerJsonPath);
            using var document = JsonDocument.Parse(stream);
            var vocabElement = document.RootElement.GetProperty("model").GetProperty("vocab");
            var vocab = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var entry in vocabElement.EnumerateObject())
            {
                vocab[entry.Name] = entry.Value.GetInt32();
            }
            Vocab = vocab;
        }

        /// <summary>
        /// BERT 入力を構築する:<c>[CLS] text [SEP]</c> 形式の input_ids と
        /// 全 1 の attention_mask、トークン文字列の配列を返す。
        /// </summary>
        public (long[] InputIds, long[] AttentionMask, string[] Tokens) Encode(string text)
        {
            var normalized = NormalizeForBert(text);
            var tokens = new List<string>(normalized.Length + 2) { "[CLS]" };
            var ids = new List<long>(tokens.Capacity) { ClsId };
            foreach (var c in normalized)
            {
                var token = c.ToString(); // 1文字トークン
                tokens.Add(token);
                ids.Add(Vocab.TryGetValue(token, out var id) ? id : UnkId);
            }
            tokens.Add("[SEP]");
            ids.Add(SepId);

            var mask = new long[ids.Count];
            Array.Fill(mask, 1L);
            return ([.. ids], mask, [.. tokens]);
        }

        /// <summary>
        /// 単語の sep_tokenized（word2ph 分配基準）。このトークナイザーは文字単位なので
        /// 各文字がそのままトークンになる（未知語も 1 文字 = 1 トークン扱い）。
        /// </summary>
        public IReadOnlyList<string> Tokenize(string word) => SplitChars(word);

        /// <summary>NFKC + テキストクリーン（参照実装の BertNormalizer 相当の近似）。</summary>
        static string NormalizeForBert(string text)
        {
            var normalized = text.Normalize(System.Text.NormalizationForm.FormKC);
            var sb = new System.Text.StringBuilder(normalized.Length);
            foreach (var c in normalized)
            {
                // 制御文字・空白は正規化テキスト現れないが念のため space 化する
                if (char.IsControl(c))
                {
                    sb.Append(' ');
                }
                else if (char.IsWhiteSpace(c))
                {
                    sb.Append(' ');
                }
                else
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }

        static string[] SplitChars(string s)
        {
            var tokens = new string[s.Length];
            for (var i = 0; i < s.Length; i++)
            {
                tokens[i] = s[i].ToString();
            }
            return tokens;
        }
    }
}
