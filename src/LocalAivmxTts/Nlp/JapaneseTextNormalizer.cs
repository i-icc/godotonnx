using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;

namespace LocalAivmxTts.Nlp
{
    /// <summary>
    /// Style-Bert-VITS2 の <c>nlp/japanese/normalizer.py</c> 相当の日本語テキスト正規化。
    /// 結果は「ひらがな / カタカナ（全角長音記号「ー」を含む）/ 漢字 / 半角英字 /
    /// ギリシャ文字 / <c>.</c> <c>,</c> <c>?</c> <c>!</c> <c>'</c> <c>-</c>」のみからなる文字列になる。
    /// </summary>
    /// <remarks>
    /// 変換仕様（参照実装と同じ順序）:
    /// NFKC → 数字・通貨を漢字化 → 「~」「～」「〜」を長音記号「ー」へ →
    /// 句読点等の記号を ASCII 化 → 結合濁点・半濁点の除去。
    /// 数字の漢字化は num2words(lang="ja") と同等の挙動を自前実装した（<see cref="JapaneseNumberWords"/>）。
    /// </remarks>
    public static class JapaneseTextNormalizer
    {
        // 記号類の正規化マップ（参照実装の __REPLACE_MAP と同一内容・同一順序）
        static readonly (string From, string To)[] ReplaceMap =
        [
            ("：", ","),
            ("；", ","),
            ("，", ","),
            ("。", "."),
            ("！", "!"),
            ("？", "?"),
            ("\n", "."),
            ("．", "."),
            ("…", "..."),
            ("···", "..."),
            ("・・・", "..."),
            ("·", ","),
            ("・", ","),
            ("、", ","),
            ("$", "."),
            ("“", "'"),
            ("”", "'"),
            ("\"", "'"),
            ("‘", "'"),
            ("’", "'"),
            ("（", "'"),
            ("）", "'"),
            ("(", "'"),
            (")", "'"),
            ("《", "'"),
            ("》", "'"),
            ("【", "'"),
            ("】", "'"),
            ("[", "'"),
            ("]", "'"),
            ("\u02d7", "\u002d"), // ˗ Modifier Letter Minus Sign
            ("\u2010", "\u002d"), // ‐ Hyphen
            // "\u2011" は NFKC で \u2010 に変わるため省略
            ("\u2012", "\u002d"), // ‒ Figure Dash
            ("\u2013", "\u002d"), // – En Dash
            ("\u2014", "\u002d"), // — Em Dash
            ("\u2015", "\u002d"), // ― Horizontal Bar
            ("\u2043", "\u002d"), // ⁃ Hyphen Bullet
            ("\u2212", "\u002d"), // − Minus Sign
            ("\u23af", "\u002d"), // ⎯ Horizontal Line Extension
            ("\u23e4", "\u002d"), // ⏤ Straightness
            ("\u2500", "\u002d"), // ─ Box Drawings Light Horizontal
            ("\u2501", "\u002d"), // ━ Box Drawings Heavy Horizontal
            ("\u2e3a", "\u002d"), // ⸺ Two-Em Dash
            ("\u2e3b", "\u002d"), // ⸻ Three-Em Dash
            ("「", "'"),
            ("」", "'"),
        ];

        static readonly Dictionary<string, string> replaceMapDict = BuildReplaceMapDict();
        static readonly Regex ReplacePattern = BuildReplacePattern();
        static readonly Regex CleanupPattern = new(
            "[^\u3040-\u309F\u30A0-\u30FF\u4E00-\u9FFF\u3400-\u4DBF\u3005"
            + "\u0041-\u005A\u0061-\u007A"
            + "\uFF21-\uFF3A\uFF41-\uFF5A"
            + "\u0370-\u03FF\u1F00-\u1FFF"
            + "!?…,.'-]+",
            RegexOptions.Compiled);
        static readonly Dictionary<string, string> CurrencyMap = new()
        {
            ["$"] = "ドル",
            ["¥"] = "円",
            ["£"] = "ポンド",
            ["€"] = "ユーロ",
        };
        static readonly Regex CurrencyPattern = new(@"([$¥£€])([0-9.]*[0-9])", RegexOptions.Compiled);
        static readonly Regex NumberPattern = new(@"[0-9]+(\.[0-9]+)?", RegexOptions.Compiled);
        static readonly Regex NumberWithSeparatorPattern = new(@"[0-9]{1,3}(,[0-9]{3})+", RegexOptions.Compiled);

        /// <summary>日本語テキストを正規化する。</summary>
        public static string Normalize(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";

            var res = text.Normalize(NormalizationForm.FormKC);
            res = ConvertNumbersToWords(res);
            res = res.Replace("~", "ー");
            res = res.Replace("～", "ー");
            res = res.Replace("〜", "ー");

            res = ReplacePunctuation(res);

            // 結合文字の濁点・半濁点を削除（る゙ → る のように）
            res = res.Replace("\u3099", "").Replace("\u309a", "");
            return res;
        }

        /// <summary>
        /// 句読点等を <c>.</c>,<c>,</c>,<c>!</c>,<c>?</c>,<c>'</c>,<c>-</c> へ正規化し、
        /// OpenJTalk で読みが取れる文字（漢字・平仮名・カタカナ・英字・ギリシャ文字）のみ残す。
        /// </summary>
        public static string ReplacePunctuation(string text)
        {
            var replaced = ReplacePattern.Replace(text, m => replaceMapDict.TryGetValue(m.Value, out var to) ? to : "");
            return CleanupPattern.Replace(replaced, "");
        }

        static Dictionary<string, string> BuildReplaceMapDict()
        {
            var dict = new Dictionary<string, string>(ReplaceMap.Length, StringComparer.Ordinal);
            foreach (var (from, to) in ReplaceMap)
            {
                dict[from] = to;
            }
            return dict;
        }

        static Regex BuildReplacePattern()
        {
            var sb = new StringBuilder();
            for (var i = 0; i < ReplaceMap.Length; i++)
            {
                if (i > 0) sb.Append('|');
                // "\n" はリテラル改行にマッチさせたい
                sb.Append(ReplaceMap[i].From == "\n" ? "\\n" : Regex.Escape(ReplaceMap[i].From));
            }
            return new Regex(sb.ToString(), RegexOptions.Compiled);
        }

        /// <summary>記号や数字を日本語の文字表現に変換する。</summary>
        static string ConvertNumbersToWords(string text)
        {
            var res = NumberWithSeparatorPattern.Replace(text, m => m.Value.Replace(",", ""));
            res = CurrencyPattern.Replace(
                res,
                m => m.Groups[2].Value
                     + (CurrencyMap.TryGetValue(m.Groups[1].Value, out var unit) ? unit : m.Groups[1].Value));
            res = NumberPattern.Replace(res, m => JapaneseNumberWords.ToCardinalOrKeep(m.Value));
            return res;
        }
    }

    /// <summary>
    /// 数字 → 漢字器数詞変換。num2words(lang="ja") の <c>to_cardinal</c>（読みなし・prefer なし）を
    /// 挙動互換で自前実装したもの（参照実装のアルゴリズムは数学的手順であり当該実装は使用していない）。
    /// <list type="bullet">
    /// <item>漢数字のみ。よって長音連鎖（rendaku）の漢字側では発動しない（読み kana のみが対象）。</item>
    /// <item>整数は正確演算。小数は参照実装と同じ float round-trip 挙動（"10.05" → "十点零五"）。</item>
    /// <item>0 → "零"。負数は "マイナス" を付けて絶対値化。</item>
    /// <item>巨大数（>= 10^51＝参照実装 MAXVAL）は例外 → 呼び出し側が元の数字列を保持（ライブラリとしては変換失敗でも落ちない挙動）。</item>
    /// </list>
    /// </summary>
    static class JapaneseNumberWords
    {
        /// <summary>num2words の cards（参: 10^48 極 … 10^0 零）。辞書挿入順＝降順。</summary>
        static readonly (BigInteger Num, string Word)[] Cards =
        [
            (BigInteger.Pow(10, 48), "極"),
            (BigInteger.Pow(10, 44), "載"),
            (BigInteger.Pow(10, 40), "正"),
            (BigInteger.Pow(10, 36), "澗"),
            (BigInteger.Pow(10, 32), "溝"),
            (BigInteger.Pow(10, 28), "穣"),
            (BigInteger.Pow(10, 24), "秭"),
            (BigInteger.Pow(10, 20), "垓"),
            (BigInteger.Pow(10, 16), "京"),
            (BigInteger.Pow(10, 12), "兆"),
            (BigInteger.Pow(10, 8), "億"),
            (BigInteger.Pow(10, 4), "万"),
            (1000, "千"),
            (100, "百"),
            (10, "十"),
            (9, "九"),
            (8, "八"),
            (7, "七"),
            (6, "六"),
            (5, "五"),
            (4, "四"),
            (3, "三"),
            (2, "二"),
            (1, "一"),
            (0, "零"),
        ];

        /// <summary>clean() の要素。tuple か list かを区別する（Python の構造をそのまま模倣）。</summary>
        sealed class NumVal
        {
            public (string Text, BigInteger Num) Tup;
            public List<NumVal>? List;

            public bool IsTuple => List is null;

            public NumVal((string Text, BigInteger Num) tup) => Tup = tup;

            public NumVal(List<NumVal> items) => List = items;
        }

        static readonly BigInteger MaxVal = 1000 * BigInteger.Pow(10, 48);

        /// <summary>変換できない場合（巨大数等）は入力文字列をそのまま返す。</summary>
        public static string ToCardinalOrKeep(string literal)
        {
            try
            {
                return ToCardinal(literal);
            }
            catch (OverflowException)
            {
                return literal;
            }
        }

        /// <summary>num2words(lang="ja").to_cardinal(value) と同値の文字列を返す。</summary>
        public static string ToCardinal(string literal)
        {
            var trimmed = literal.Trim();
            var isDecimal = trimmed.Contains('.') || trimmed.ToLowerInvariant().Contains('e');

            if (!isDecimal)
            {
                // 整数: 正確変算
                var value = BigInteger.Parse(trimmed, System.Globalization.CultureInfo.InvariantCulture);
                if (value < 0)
                {
                    return "マイナス" + IntegerToWords(-value);
                }
                if (value >= MaxVal)
                {
                    throw new OverflowException("number too large to convert to kanji: " + trimmed);
                }
                return IntegerToWords(value);
            }

            // 小数: 参照実装と同じく float round-trip で精度を決める
            var d = double.Parse(trimmed, System.Globalization.CultureInfo.InvariantCulture);
            // .NET の double.ToString() は最短丸め（Python str(float) と同等）
            var s = d.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (Math.Abs(d) >= 1e51)
            {
                throw new OverflowException("number too large to convert to kanji: " + trimmed);
            }

            // str が指数表記（"1E-05"）なら小数部桁数は指数から、そうでないなら '.' 以降の桁数
            var mantissa = s;
            var exp = 0;
            var eIndex = s.IndexOfAny(['E', 'e']);
            if (eIndex >= 0)
            {
                mantissa = s[..eIndex];
                exp = int.Parse(s[(eIndex + 1)..], System.Globalization.CultureInfo.InvariantCulture);
            }
            var dotIndex = mantissa.IndexOf('.');
            var precision = dotIndex >= 0 ? mantissa.Length - dotIndex - 1 : Math.Abs(exp);
            if (precision <= 0)
            {
                // Python str(float) は整数でも "1.0" のように "0" を1桁持つため precision=1 相当になる
                precision = 1;
            }

            var sb = new StringBuilder();
            if (d < 0)
            {
                sb.Append("マイナス");
            }

            var pre = (BigInteger)Math.Truncate(Math.Abs(d));
            sb.Append(IntegerToWords(pre));
            sb.Append('点');
            // base.float2tuple: post = abs(value - pre) * 10^precision; |round(post)-post| < 0.01 なら round を採用
            var postValue = (Math.Abs(d) - (double)pre) * Math.Pow(10, precision);
            var post = Math.Abs(postValue - Math.Round(postValue)) < 0.01
                ? (long)Math.Round(postValue, MidpointRounding.ToEven)
                : (long)Math.Floor(postValue);
            var postStr = Math.Max(0, precision - post.ToString().Length) is var pad
                ? new string('0', Math.Max(0, pad)) + post
                : post.ToString();
            for (var i = 0; i < precision; i++)
            {
                sb.Append(IntegerToWords((int)(postStr[i] - '0')));
            }
            return sb.ToString();
        }

        /// <summary>整数 → 漢字列（num2words の splitnum + clean + merge 相当）。</summary>
        public static string IntegerToWords(BigInteger value)
        {
            return CleanList(Splitnum(value)).Tup.Text;
        }

        /// <summary>
        /// base.Num2Word_Base.clean(val) の移植。
        /// tuple×tuple の隣接ペアを merge（残りは余り全体を 1 要素の list として append）、
        /// それ以外は各リスト要素を clean した結果で置換する。
        /// </summary>
        static NumVal CleanList(List<NumVal> val)
        {
            while (val.Count != 1)
            {
                var outVal = new List<NumVal>(val.Count);
                var left = val[0];
                var right = val[1];
                if (left.IsTuple && right.IsTuple)
                {
                    outVal.Add(new NumVal(Merge(left.Tup, right.Tup)));
                    if (val.Count > 2)
                    {
                        // Python の val[2:] は「余りのリスト全体」を 1 要素として追加する
                        outVal.Add(new NumVal([.. val.Skip(2)]));
                    }
                }
                else
                {
                    foreach (var elem in val)
                    {
                        outVal.Add(elem.IsTuple ? elem : elem.List!.Count == 1 ? elem.List[0] : CleanList(elem.List));
                    }
                }
                val = outVal;
            }
            return val[0];
        }

        static List<NumVal> Splitnum(BigInteger value)
        {
            foreach (var (num, word) in Cards)
            {
                if (num > value)
                {
                    continue;
                }

                BigInteger div, mod;
                if (value.IsZero)
                {
                    div = BigInteger.One;
                    mod = BigInteger.Zero;
                }
                else
                {
                    div = BigInteger.DivRem(value, num, out mod);
                }

                var outList = new List<NumVal>(3);
                if (div.IsOne)
                {
                    outList.Add(new NumVal(("一", BigInteger.One)));
                }
                else
                {
                    if (div == value)
                    {
                        // 参照実装の "(div * cards[elem], div * elem)" 相当。
                        // cards に 1..9 を全て含むため実運用では到達しない（到達時は例外的に二重化）
                        return [new NumVal((word + word, div * num))];
                    }
                    outList.Add(new NumVal(Splitnum(div)));
                }

                outList.Add(new NumVal((word, num)));

                if (mod.Sign != 0)
                {
                    outList.Add(new NumVal(Splitnum(mod)));
                }

                return outList;
            }
            throw new InvalidOperationException("Unreachable");
        }

        /// <summary>merge(lpair, rpair)（読み指定なし＝漢字のみの経路）。rendaku は対象外なので単純結合。</summary>
        static (string, BigInteger) Merge((string text, BigInteger num) l, (string text, BigInteger num) r)
        {
            var (ltext, lnum) = l;
            var (rtext, rnum) = r;

            if (lnum.IsOne && rnum < 10000)
            {
                return r;
            }
            if (lnum > rnum)
            {
                return (ltext + rtext, lnum + rnum);
            }
            if (lnum == rnum)
            {
                // 参照実装はここで None（=失敗）となるが、実運用で到達しないため防御的に加算
                return (ltext + rtext, lnum + rnum);
            }
            // lnum < rnum → rendaku。読み（kana）指定時のみ異文字が現れるため漢字では単純結合
            return (ltext + rtext, lnum * rnum);
        }
    }
}
