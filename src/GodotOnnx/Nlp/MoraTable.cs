using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace GodotOnnx.Nlp
{
    /// <summary>
    /// Style-Bert-VITS2 の <c>nlp/japanese/mora_list.py</c>（モーラ対応表）と
    /// <c>g2p.py</c> 内のカナ → 音素変換（<c>__kata_to_phoneme_list</c> / <c>__handle_long</c>）の移植。
    /// </summary>
    /// <remarks>
    /// 参照実装（VOICEVOX由来の OpenJTalk モーラリスト）の内容を自前テーブルとして再構成した。
    /// 参照実装の差異点を踏襲: 「ン」→ <c>N</c>、「ッ」→ <c>q</c>、「デェ」は pyopenjtalk の
    /// 出力 (de e) と合わないため除外。
    /// </remarks>
    public static class MoraTable
    {
        // (カナ, 子音, 母音)。子音なしは null。
        static readonly (string Kana, string? Consonant, string Vowel)[] MinimumList =
        [
            ("ヴォ", "v", "o"), ("ヴェ", "v", "e"), ("ヴィ", "v", "i"), ("ヴァ", "v", "a"),
            ("ヴ", "v", "u"), ("ン", null, "N"), ("ワ", "w", "a"), ("ロ", "r", "o"),
            ("レ", "r", "e"), ("ル", "r", "u"), ("リョ", "ry", "o"), ("リュ", "ry", "u"),
            ("リャ", "ry", "a"), ("リェ", "ry", "e"), ("リ", "r", "i"), ("ラ", "r", "a"),
            ("ヨ", "y", "o"), ("ユ", "y", "u"), ("ヤ", "y", "a"), ("モ", "m", "o"),
            ("メ", "m", "e"), ("ム", "m", "u"), ("ミョ", "my", "o"), ("ミュ", "my", "u"),
            ("ミャ", "my", "a"), ("ミェ", "my", "e"), ("ミ", "m", "i"), ("マ", "m", "a"),
            ("ポ", "p", "o"), ("ボ", "b", "o"), ("ホ", "h", "o"), ("ペ", "p", "e"),
            ("ベ", "b", "e"), ("ヘ", "h", "e"), ("プ", "p", "u"), ("ブ", "b", "u"),
            ("フォ", "f", "o"), ("フェ", "f", "e"), ("フィ", "f", "i"), ("ファ", "f", "a"),
            ("フ", "f", "u"), ("ピョ", "py", "o"), ("ピュ", "py", "u"), ("ピャ", "py", "a"),
            ("ピェ", "py", "e"), ("ピ", "p", "i"), ("ビョ", "by", "o"), ("ビュ", "by", "u"),
            ("ビャ", "by", "a"), ("ビェ", "by", "e"), ("ビ", "b", "i"), ("ヒョ", "hy", "o"),
            ("ヒュ", "hy", "u"), ("ヒャ", "hy", "a"), ("ヒェ", "hy", "e"), ("ヒ", "h", "i"),
            ("パ", "p", "a"), ("バ", "b", "a"), ("ハ", "h", "a"), ("ノ", "n", "o"),
            ("ネ", "n", "e"), ("ヌ", "n", "u"), ("ニョ", "ny", "o"), ("ニュ", "ny", "u"),
            ("ニャ", "ny", "a"), ("ニェ", "ny", "e"), ("ニ", "n", "i"), ("ナ", "n", "a"),
            ("ドゥ", "d", "u"), ("ド", "d", "o"), ("トゥ", "t", "u"), ("ト", "t", "o"),
            ("デョ", "dy", "o"), ("デュ", "dy", "u"), ("デャ", "dy", "a"),
            // ("デェ", "dy", "e"), ← 除外（参照実装と同一の差異）
            ("ディ", "d", "i"), ("デ", "d", "e"), ("テョ", "ty", "o"), ("テュ", "ty", "u"),
            ("テャ", "ty", "a"), ("ティ", "t", "i"), ("テ", "t", "e"), ("ツォ", "ts", "o"),
            ("ツェ", "ts", "e"), ("ツィ", "ts", "i"), ("ツァ", "ts", "a"), ("ツ", "ts", "u"),
            ("ッ", null, "q"), // 参照実装で「cl」→「q」に変更したもの
            ("チョ", "ch", "o"), ("チュ", "ch", "u"), ("チャ", "ch", "a"), ("チェ", "ch", "e"),
            ("チ", "ch", "i"), ("ダ", "d", "a"), ("タ", "t", "a"), ("ゾ", "z", "o"),
            ("ソ", "s", "o"), ("ゼ", "z", "e"), ("セ", "s", "e"), ("ズィ", "z", "i"),
            ("ズ", "z", "u"), ("スィ", "s", "i"), ("ス", "s", "u"), ("ジョ", "j", "o"),
            ("ジュ", "j", "u"), ("ジャ", "j", "a"), ("ジェ", "j", "e"), ("ジ", "j", "i"),
            ("ショ", "sh", "o"), ("シュ", "sh", "u"), ("シャ", "sh", "a"), ("シェ", "sh", "e"),
            ("シ", "sh", "i"), ("ザ", "z", "a"), ("サ", "s", "a"), ("ゴ", "g", "o"),
            ("コ", "k", "o"), ("ゲ", "g", "e"), ("ケ", "k", "e"), ("グヮ", "gw", "a"),
            ("グ", "g", "u"), ("クヮ", "kw", "a"), ("ク", "k", "u"), ("ギョ", "gy", "o"),
            ("ギュ", "gy", "u"), ("ギャ", "gy", "a"), ("ギェ", "gy", "e"), ("ギ", "g", "i"),
            ("キョ", "ky", "o"), ("キュ", "ky", "u"), ("キャ", "ky", "a"), ("キェ", "ky", "e"),
            ("キ", "k", "i"), ("ガ", "g", "a"), ("カ", "k", "a"), ("オ", null, "o"),
            ("エ", null, "e"), ("ウォ", "w", "o"), ("ウェ", "w", "e"), ("ウィ", "w", "i"),
            ("ウ", null, "u"), ("イェ", "y", "e"), ("イ", null, "i"), ("ア", null, "a"),
        ];

        static readonly (string Kana, string? Consonant, string Vowel)[] AdditionalList =
        [
            ("ヴョ", "by", "o"), ("ヴュ", "by", "u"), ("ヴャ", "by", "a"), ("ヲ", null, "o"),
            ("ヱ", null, "e"), ("ヰ", null, "i"), ("ヮ", "w", "a"), ("ョ", "y", "o"),
            ("ュ", "y", "u"), ("ヅ", "z", "u"), ("ヂ", "j", "i"), ("ヶ", "k", "e"),
            ("ャ", "y", "a"), ("ォ", null, "o"), ("ェ", null, "e"), ("ゥ", null, "u"),
            ("ィ", null, "i"), ("ァ", null, "a"),
        ];

        /// <summary>カナ → (子音, 母音) の写像（参照実装の MORA_KATA_TO_MORA_PHONEMES 相当）</summary>
        public static readonly IReadOnlyDictionary<string, (string? Consonant, string Vowel)> KataToPhonemes =
            BuildKataToPhonemes();

        /// <summary>母音集合（参照実装の VOWELS。「ん」を含むため N を含む）</summary>
        public static readonly IReadOnlySet<string> Vowels = new HashSet<string> { "a", "i", "u", "e", "o", "N", };

        /// <summary>カナキーの文字数降順リスト（最長一致用）</summary>
        static readonly (string Kana, string? Consonant, string Vowel)[] SortedByLength = BuildSortedByLength();

        /// <summary>参照実装の (\w)(ー*) と同義（「ー」は \w にマッチする Unicode 文字）</summary>
        static readonly Regex LongPattern = new(@"(\w)(ー*)", RegexOptions.Compiled);

        /// <summary>
        /// カタカナ文字列を音素リストへ変換する（参照実装の <c>__kata_to_phoneme_list</c> 相当）。
        /// ・全文字が punctuation の文字列は 1 文字ずつそのまま返す
        /// ・文中の「ー」は直前音素の末尾文字の複製（LONG_PATTERN）
        /// ・先頭の「ー」はそのまま残す（呼び出し側の HandleLong が処理する）
        /// </summary>
        public static List<string> KataToPhonemeList(string text)
        {
            if (IsAllPunctuation(text))
            {
                var chars = new List<string>(text.Length);
                foreach (var c in text)
                {
                    chars.Add(c.ToString());
                }
                return chars;
            }

            if (!IsKatakanaOnly(text))
            {
                throw new ArgumentException($"Input must be katakana only: {text}", nameof(text));
            }

            // 参照実装と同様に space 区切りの中間文字列を構築する。
            // モーラの挿入は「 」+ 子音? +「 」+ 母音。未一致文字（「ー」等）はそのまま連結する
            // （参照実装は str.replace 相当のため「ー」を space なしでそのまま残す）。
            var sb = new StringBuilder(text.Length * 4);
            var i = 0;
            while (i < text.Length)
            {
                var matched = false;
                foreach (var (kana, consonant, vowel) in SortedByLength)
                {
                    if (i + kana.Length <= text.Length
                        && string.CompareOrdinal(text, i, kana, 0, kana.Length) == 0)
                    {
                        sb.Append(' ');
                        if (consonant != null)
                        {
                            sb.Append(consonant).Append(' ');
                        }
                        sb.Append(vowel);
                        i += kana.Length;
                        matched = true;
                        break;
                    }
                }
                if (!matched)
                {
                    // モーラ表に無い文字は「ー」のみ（先ほどのカタカナ判定より）。
                    // それ以外が来ることはないが防御的に例外にする。
                    if (text[i] != 'ー')
                    {
                        throw new ArgumentException(
                            $"Cannot convert to mora: '{text[i]}' at {i} in \"{text}\"", nameof(text));
                    }
                    sb.Append('ー');
                    i++;
                }
            }

            var spaced = sb.ToString();

            // 「ー」→ 直前の音素末尾文字の複製: (\w)(ー*) → g1 + (" " + g1) * count(g2)
            spaced = LongPattern.Replace(
                spaced,
                m => m.Groups[1].Value + string.Concat(
                    Enumerable.Repeat(" " + m.Groups[1].Value, m.Groups[2].Length)));

            var items = spaced.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return [.. items];
        }

        /// <summary>
        /// 単語ごとの音素リストへの長音記号処理（参照実装の <c>__handle_long</c> 相当）。
        /// 基本的に直前の単語（or 自身の直前音素）の末尾音素に置換するが、
        /// 直前の音素が母音でない場合（or 冒頭）はダッシュ「-」に変換する。
        /// </summary>
        /// <param name="sepPhonemes">単語ごとに分かれた音素（「ー」がそのまま残る）リストのリスト</param>
        public static void HandleLong(List<List<string>> sepPhonemes)
        {
            for (var i = 0; i < sepPhonemes.Count; i++)
            {
                var word = sepPhonemes[i];
                if (word.Count == 0)
                {
                    continue;
                }

                if (word[0] == "ー")
                {
                    if (i != 0)
                    {
                        var prevPhoneme = sepPhonemes[i - 1][^1];
                        word[0] = Vowels.Contains(prevPhoneme) ? prevPhoneme : "-";
                    }
                    else
                    {
                        word[0] = "-";
                    }
                }

                for (var j = 0; j < word.Count; j++)
                {
                    if (word[j] == "ー")
                    {
                        // 直前の音素の末尾文字（word[j-1] が変換済み「-」なら "-"）
                        word[j] = word[j - 1][^1].ToString();
                    }
                }
            }
        }

        static bool IsAllPunctuation(string text)
        {
            if (text.Length == 0)
            {
                return false;
            }
            foreach (var c in text)
            {
                if (c != '!' && c != '?' && c != '…' && c != ',' && c != '.' && c != '\'' && c != '-')
                {
                    return false;
                }
            }
            return true;
        }

        static bool IsKatakanaOnly(string text)
        {
            foreach (var c in text)
            {
                if (c < '\u30A0' || c > '\u30FF')
                {
                    return false;
                }
            }
            return true;
        }

        static Dictionary<string, (string?, string)> BuildKataToPhonemes()
        {
            var map = new Dictionary<string, (string?, string)>(MinimumList.Length + AdditionalList.Length, StringComparer.Ordinal);
            foreach (var (kana, consonant, vowel) in MinimumList)
            {
                map.Add(kana, (consonant, vowel));
            }
            foreach (var (kana, consonant, vowel) in AdditionalList)
            {
                map.TryAdd(kana, (consonant, vowel));
            }
            return map;
        }

        static (string Kana, string? Consonant, string Vowel)[] BuildSortedByLength()
        {
            var all = new List<(string Kana, string? Consonant, string Vowel)>(MinimumList.Length + AdditionalList.Length);
            foreach (var (kana, consonant, vowel) in MinimumList)
            {
                all.Add((kana, consonant, vowel));
            }
            foreach (var (kana, consonant, vowel) in AdditionalList)
            {
                all.Add((kana, consonant, vowel));
            }
            all.Sort((a, b) =>
            {
                var byLength = b.Kana.Length.CompareTo(a.Kana.Length);
                return byLength != 0 ? byLength : string.CompareOrdinal(a.Kana, b.Kana);
            });
            return [.. all];
        }
    }
}
