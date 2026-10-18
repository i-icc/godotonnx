using System;
using System.Collections.Generic;

namespace GodotOnnx.Nlp
{
    /// <summary>
    /// Style-Bert-VITS2 の <c>nlp/symbols.py</c> に対応する記号表。
    /// 音素記号 → モデル入力 ID、tone/language オフセットの変換を提供する。
    /// </summary>
    /// <remarks>
    /// <c>SYMBOLS = [PAD] + sorted(set(ZH + JP + EN)) + PUNCTUATION_SYMBOLS</c> の定義と
    /// <see href="https://github.com/tsukumijima/Style-Bert-VITS2/blob/main/style_bert_vits2/nlp/symbols.py"/>
    /// のテーブル内容を参照して自前移植した。
    /// <list type="number">
    /// <item>ZH tone: 0..5 (NUM_ZH_TONES=6)</item>
    /// <item>JP tone: 0..1 → +6 (NUM_JP_TONES=2)</item>
    /// <item>EN tone: 0..3 → +8 (NUM_EN_TONES=4)</item>
    /// </list>
    /// language ID = ZH:0, JP:1, EN:2。
    /// </remarks>
    public static class SymbolTable
    {
        /// <summary>PUNCTUATIONS: SBV2 の <c>PUNCTUATIONS</c> と同一の並び</summary>
        public static readonly string[] Punctuations = ["!", "?", "…", ",", ".", "'", "-"];

        static readonly string[] PunctuationSymbols = [.. Punctuations, "SP", "UNK"];

        static readonly string[] ZhSymbols =
        [
            "E", "En", "a", "ai", "an", "ang", "ao", "b", "c", "ch", "d", "e", "ei",
            "en", "eng", "er", "f", "g", "h", "i", "i0", "ia", "ian", "iang", "iao",
            "ie", "in", "ing", "iong", "ir", "iu", "j", "k", "l", "m", "n", "o",
            "ong", "ou", "p", "q", "r", "s", "sh", "t", "u", "ua", "uai", "uan",
            "uang", "ui", "un", "uo", "v", "van", "ve", "vn", "w", "x", "y", "z",
            "zh", "AA", "EE", "OO",
        ];

        static readonly string[] JpSymbols =
        [
            "N", "a", "a:", "b", "by", "ch", "d", "dy", "e", "e:", "f", "g", "gy",
            "h", "hy", "i", "i:", "j", "k", "ky", "m", "my", "n", "ny", "o", "o:",
            "p", "py", "q", "r", "ry", "s", "sh", "t", "ts", "ty", "u", "u:", "w",
            "y", "z", "zy",
        ];

        static readonly string[] EnSymbols =
        [
            "aa", "ae", "ah", "ao", "aw", "ay", "b", "ch", "d", "dh", "eh", "er",
            "ey", "f", "g", "hh", "ih", "iy", "jh", "k", "l", "m", "n", "ng", "ow",
            "oy", "p", "r", "s", "sh", "t", "th", "uh", "uw", "V", "w", "y", "z",
            "zh",
        ];

        /// <summary>SYMBOLS 全体（_ + NORMAL + PUNCT特別群）</summary>
        public static readonly string[] Symbols = BuildSymbols();

        /// <summary>音素記号 → シンボル ID（初期化順序: Symbols の後に必須）</summary>
        static readonly Dictionary<string, int> idMap = BuildIdMap();

        /// <summary>Sil phonemes (punctuation + SP + UNK) のシンボル ID</summary>
        public static readonly int[] SilPhonemeIds = BuildSilPhonemeIds();

        /// <summary>音素記号 → シンボル ID（見つからなければ -1）</summary>
        public static int GetId(string symbol) =>
            idMap.TryGetValue(symbol, out var id) ? id : -1;

        /// <summary>lang_ids 全要素が "JP"(=1) になる言語 ID</summary>
        public const int LanguageJp = 1;

        /// <summary>JP tone のオフセット（tone に加算する値）= NUM_ZH_TONES</summary>
        public const int ToneOffsetJp = 6;

        static string[] BuildSymbols()
        {
            var normal = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var s in ZhSymbols) normal.Add(s);
            foreach (var s in JpSymbols) normal.Add(s);
            foreach (var s in EnSymbols) normal.Add(s);

            var list = new List<string>(normal.Count + PunctuationSymbols.Length + 1)
            {
                "_" /* PAD */,
            };
            list.AddRange(normal);
            list.AddRange(PunctuationSymbols);
            return [.. list];
        }

        static int[] BuildSilPhonemeIds()
        {
            var ids = new int[PunctuationSymbols.Length];
            for (var i = 0; i < PunctuationSymbols.Length; i++)
            {
                ids[i] = GetId(PunctuationSymbols[i]);
            }
            return ids;
        }

        static Dictionary<string, int> BuildIdMap()
        {
            var map = new Dictionary<string, int>(Symbols.Length, StringComparer.Ordinal);
            for (var i = 0; i < Symbols.Length; i++)
            {
                map[Symbols[i]] = i;
            }
            return map;
        }
    }
}
