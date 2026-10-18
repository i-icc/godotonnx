using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using DotNetG2P;
using DotNetG2P.JPCommon;
using DotNetG2P.Models;

namespace LocalAivmxTts.Nlp
{
    /// <summary>G2P の結果</summary>
    /// <param name="Phones">punctuation を含む音素列（先頭末尾に <c>_</c> を含む）</param>
    /// <param name="Tones">0/1 アクセント列（Phones と同長）</param>
    /// <param name="Word2Ph">文字ごとに割り当てられる音素数（先頭末尾に 1 を含む）</param>
    /// <param name="SepText">単語分割（正規化表記列）。BERT 入力は連結 <c>""</c> で与える。</param>
    /// <param name="SepKata">単語ごとのカタカナ読み（punctuation はその文字）</param>
    /// <param name="Labels">DotNetG2P によるフルコンテキストラベル（デバッグ/比較用）</param>
    public sealed record G2pResult(
        string[] Phones,
        int[] Tones,
        int[] Word2Ph,
        string[] SepText,
        string[] SepKata,
        string[] Labels)
    {
        /// <summary>単語ごとの音素数（HandleLong 後。index は SepText と一致）。</summary>
        public int[] SepPhonemeCounts { get; init; } = [];

        /// <summary>BERT 特徴抽出の入力となる <c>sep_text</c> 連結文字列。</summary>
        public string BertText => string.Concat(SepText);
    }

    /// <summary>
    /// Style-Bert-VITS2 の日本語 G2P（<c>nlp/japanese/g2p.py</c>）相当。
    /// </summary>
    /// <remarks>
    /// 参照実装は pyopenjtalk を使うが、ここでは DotNetG2P（formattable. な MeCab + NJD + フルコンテキストラベル）
    /// を解析に使い、音素・アクセント（tone）・word2ph を自前で組立てる。
    /// <list type="bullet">
    /// <item>phones/tone のアクセント計算は ESPnet 由来の <c>__pyopenjtalk_g2p_prosody</c> を
    /// DotNetG2P のフルコンテキストラベル上に移植したもの（DotNetG2P.<c>ToProsody()</c> は
    /// ESPnet と "_" 挿入が異なるため使用しない）。</item>
    /// <item>punctuation を含む phones / word2ph は参照実装と同一のアルゴリズム
    /// (<c>__align_tones</c>, <c>__distribute_phone</c>)。</item>
    /// <item>sep_tokenized（word2ph の分配基準）は 1 文字 = 1 トークン（実BF トークナイザーの
    /// 文字単位 WordLevel と同値）。<c>[CLS]</c>/<c>[SEP]</c> は数えない
    /// （参照実装の transformers <c>tokenize()</c> 相当）。</item>
    /// </list>
    /// </remarks>
    public static class Sbv2JapaneseG2P
    {
        /// <summary>トークン化（sep_tokenized）は <c>[CLS]</c>/<c>[SEP]</c> を含めない。</summary>
        public static string[] Punctuations => SymbolTable.Punctuations;

        /// <summary>
        /// DotNetG2P エンジンを構築する。テキスト正規化はこのライブラリ側
        /// (<see cref="JapaneseTextNormalizer"/>)で実施済みのため無効化する。
        /// </summary>
        /// <param name="naistJdicPath">naist-jdic 辞書ディレクトリ</param>
        public static G2PEngine CreateEngine(string naistJdicPath)
        {
            var tokenizer = new DotNetG2P.MeCab.MeCabTokenizer(naistJdicPath);
            var options = new G2POptions(
                enableTextNormalization: false, // 正規化済みテキストを受け取るため無効
                enableUnvoicedVowel: true,      // pyopenjtalk 互換の無声化処理
                enableDigitProcessing: true,
                enableAccentPhrase: true,
                enableAccentType: true,
                expandLongVowels: false);       // 長音は「ー」（フルコンテキストラベル内で前母音に展開）
            return new G2PEngine(tokenizer, options);
        }

        /// <summary>
        /// MeCab に入力するためのテキストに変換する（open-jtalk mecab の文字正規化と同一規則）:
        /// ASCII 句読点 <c>,</c> <c>.</c> <c>!</c> <c>?</c> を全角 <c>，</c> <c>．</c> <c>！</c> <c>？</c>、
        /// <c>-</c> を <c>−</c>(U+2210 的に U+2212)、<c>'</c> を <c>’</c>(U+2019)、
        /// 英字を全角英字へ変換する（これにより naist-jdic のエントリと一致する）。
        /// </summary>
        public static string ToMecabInput(string normText)
        {
            var sb = new StringBuilder(normText.Length);
            foreach (var c in normText)
            {
                sb.Append(c switch
                {
                    ',' => '，',      // U+FF0C 全角-comma（meCab 正規化と同一）
                    '.' => '．',      // U+FF0E
                    '!' => '！',      // U+FF01
                    '?' => '？',      // U+FF1F
                    '-' => '−',      // U+2212 Minus Sign（meCab 正規化と同一・全角ハイフン U+FF0D ではない）
                    '\'' => '’',     // U+2019
                    >= 'A' and <= 'Z' => (char)(c + 0xFEE0),
                    >= 'a' and <= 'z' => (char)(c + 0xFEE0),
                    _ => c,
                });
            }
            return sb.ToString();
        }

        /// <summary>
        /// メインの G2P。normalize_message() で正規化済みの normText を受け取り、
        /// phones（punctuation を含む音素列）、tones（0/1 アクセント列）、
        /// word2ph（文字ごとに割り当てられる音素数。先頭末尾に 1 を含む）を返す。
        /// </summary>
        /// <param name="normText">正規化済みテキスト</param>
        /// <param name="engine">MeCab 辞書をセットアップ済みの DotNetG2P エンジン</param>
        /// <param name="useJpExtra">false の場合「N」を「n」に変換する</param>
        public static G2pResult G2p(string normText, G2PEngine engine, bool useJpExtra = true)
        {
            // NJD 解析（sep_text/sep_kata とフルコンテキストラベルの両方に使う）
            var nodes = engine.Analyze(ToMecabInput(normText));

            // sep 系は pyopenjtalk と同じ「無変更ノード」から計算する
            var (sepText, sepKata) = TextToSepKata(nodes);
            List<List<string>> sepPhonemes = new(sepKata.Count);
            foreach (var kata in sepKata)
            {
                sepPhonemes.Add(MoraTable.KataToPhonemeList(kata));
            }
            MoraTable.HandleLong(sepPhonemes);

            // ラベルは pyopenjtalk と同一の前処理（冒頭長音除去・ヲ→オ）を
            // 適用したノードから生成する（DotNetG2P 単体では音素が一致しないため）
            var labelNodes = nodes.Select(NormalizeNodeForLabels).ToList();
            var utterance = JPCommonBuilder.Build(labelNodes);
            var labels = FullContextLabel.Generate(utterance);

            var phoneWPunct = new List<string>(sepPhonemes.Sum(p => p.Count));
            foreach (var item in sepPhonemes)
            {
                phoneWPunct.AddRange(item);
            }
            var sepPhonemeCounts = new int[sepPhonemes.Count];
            for (var i = 0; i < sepPhonemes.Count; i++)
            {
                sepPhonemeCounts[i] = sepPhonemes[i].Count;
            }

            var phoneToneWoPunct = FixDotNetG2PPairs(G2PhoneToneWoPunct(labels));
            var phoneToneList = AlignTones(phoneWPunct, phoneToneWoPunct);

            // word2ph: 分配の基準は sep_tokenized の文字数
            var word2Ph = new List<int>(sepText.Count * 2);
            for (var i = 0; i < sepText.Count; i++)
            {
                var phonemeCount = sepPhonemes[i].Count;
                var wordLen = IsPunctuation(sepText[i]) ? 1 : sepText[i].Length;
                word2Ph.AddRange(DistributePhone(phonemeCount, wordLen));
            }

            // 「_」をやや広めの padding として先頭と末尾に入れ、word2ph もそれに合わせる
            phoneToneList.Insert(0, ("_", 0));
            phoneToneList.Add(("_", 0));
            word2Ph.Insert(0, 1);
            word2Ph.Add(1);

            var phones = new string[phoneToneList.Count];
            for (var i = 0; i < phoneToneList.Count; i++)
            {
                var (phone, tone) = phoneToneList[i];
                phones[i] = phone;
            }

            if (!useJpExtra)
            {
                phones = phones.Select(p => p == "N" ? "n" : p).ToArray();
            }

            var tonesList = phoneToneList.Select(t => t.Item2).ToList();
            if (phones.Length != word2Ph.Sum())
            {
                throw new InvalidOperationException(
                    $"phones {phones.Length} != sum(word2ph) {word2Ph.Sum()} (${normText})");
            }

            return new G2pResult(
                Phones: phones,
                Tones: [.. tonesList],
                Word2Ph: [.. word2Ph],
                SepText: [.. sepText],
                SepKata: [.. sepKata],
                Labels: [.. labels])
            {
                SepPhonemeCounts = sepPhonemeCounts,
            };
        }

        /// <summary>
        /// paresd NJD ノードリストから sep_text / sep_kata を作る。
        /// （参照実装の <c>text_to_sep_kata</c>: run_frontend → node["string"]/node["pron"]）
        /// </summary>
        public static (List<string> SepText, List<string> SepKata) TextToSepKata(
            IReadOnlyList<NjdNode> nodes)
        {
            var sepText = new List<string>(nodes.Count);
            var sepKata = new List<string>(nodes.Count);

            foreach (var node in nodes)
            {
                // word: 実際の単語の文字列（正規化された記号表記）
                var word = JapaneseTextNormalizer.ReplacePunctuation(node.Surface);
                // yomi: 読み（カタカナ読み）。DotNetG2P の ToKatakana() は無声化マーカーを含まないため
                // 参照実装の pron.replace("’","") と同値。
                var pronunciation = node.Pronunciation;
                if (pronunciation == null)
                {
                    continue; // 発音を持たないノード（pyopenjtalk でもエラー相当）
                }
                var yomi = pronunciation.ToKatakana();

                if (string.IsNullOrEmpty(yomi))
                {
                    continue;
                }

                if (yomi == "、")
                {
                    // word は正規化済みなので ".", ",", "!", "'", "-", "？", "…", 等からなる
                    // punctuation 系文字か判定する。
                    if (!IsSubsetOfPunctuations(word))
                    {
                        // pyopenjtalk が読めない文字等のときに起こる。読めない文字は「'」に置換
                        yomi = new string('\'', word.Length);
                    }
                    else
                    {
                        yomi = word;
                    }
                }
                else if (yomi == "？")
                {
                    // 参照実装: assert word == "?"
                    // （ここでは "?" 以外が来た場合、実際の word を尊重して "?"; に合わせる）
                    if (word == "?")
                    {
                        yomi = "?";
                    }
                    else
                    {
                        // MeCab 側が word を他の記号に変換している可能性は高くない。防御的処理
                        yomi = word;
                    }
                }

                sepText.Add(word);
                sepKata.Add(yomi);
            }

            return (sepText, sepKata);
        }

        /// <summary>
        /// フルコンテキストラベルから ESPnet 方式で音素 + 韻律記号を抽出する
        /// （参照実装の <c>__pyopenjtalk_g2p_prosody</c> に相当）。
        /// 「ん」は「N」、「っ」は「cl」（参照実装内で後から「q」に変換）。
        /// </summary>
        public static List<string> ProsodySymbolsFromLabels(
            IReadOnlyList<string> labels, bool dropUnvoicedVowels = true)
        {
            var phones = new List<string>(labels.Count);
            var n = labels.Count;
            for (var i = 0; i < n; i++)
            {
                var label = labels[i];

                var p3 = P3Pattern.Match(label) is { Success: true } m3 ? m3.Groups[1].Value : "";
                if (p3.Length == 0)
                {
                    throw new InvalidOperationException($"Invalid label: {label}");
                }
                if (dropUnvoicedVowels && p3.Length == 1
                    && "AEIOU".Contains(p3[0]))
                {
                    p3 = p3.ToLowerInvariant();
                }

                // 文頭/文末の sil
                if (p3 == "sil")
                {
                    if (i == 0)
                    {
                        phones.Add("^");
                    }
                    else if (i == n - 1)
                    {
                        var e3 = MatchInt(E3Pattern, label);
                        phones.Add(e3 == 1 ? "?" : "$");
                    }
                    continue;
                }

                if (p3 == "pau")
                {
                    phones.Add("_");
                    continue;
                }

                phones.Add(p3);

                var a1 = MatchInt(A1Pattern, label);
                var a2 = MatchInt(A2Pattern, label);
                var a3 = MatchInt(A3Pattern, label);
                var f1 = MatchInt(F1Pattern, label);
                var a2Next = MatchInt(A2Pattern, labels[i + 1]);

                if (a3 == 1 && a2Next == 1 && "aeiouAEIOUNcl".Contains(p3))
                {
                    phones.Add("#");
                }
                else if (a1 == 0 && a2Next == a2 + 1 && a2 != f1)
                {
                    phones.Add("]");
                }
                else if (a2 == 1 && a2Next == 2)
                {
                    phones.Add("[");
                }
            }

            return phones;
        }

        /// <summary>
        /// DotNetG2P のモーラ→音素変換と pyopenjtalk の差分を吸収する。
        /// pyopenjtalk では 'dy'+'i'（ディ）→ d+i、'ty'+'i'（ティ）→ t+i のみで、
        /// dy/ty は i 母音を取らない（デャ/デュ/デョ・テャ/テュ/テョは a/u/o）ため安全な置換。
        /// ヲ は <see cref="NormalizeNodeForLabels"/> で pron を オ に変換済み。
        /// </summary>
        public static List<(string, int)> FixDotNetG2PPairs(List<(string, int)> phoneToneWoPunct)
        {
            for (var i = 0; i < phoneToneWoPunct.Count - 1; i++)
            {
                if (phoneToneWoPunct[i + 1].Item1 != "i")
                {
                    continue;
                }

                phoneToneWoPunct[i] = phoneToneWoPunct[i].Item1 switch
                {
                    "dy" => ("d", phoneToneWoPunct[i].Item2),
                    "ty" => ("t", phoneToneWoPunct[i].Item2),
                    _ => phoneToneWoPunct[i],
                };
            }
            return phoneToneWoPunct;
        }

        /// <summary>
        /// 韻律記号列（^, [ , ], _, #, $, ? を含む）から、punctuation を含まない音素と
        /// アクセント (0/1) のリストを作る（参照実装の <c>__g2phone_tone_wo_punct</c> 相当）。
        /// </summary>
        public static List<(string Phone, int Tone)> G2PhoneToneWoPunct(
            IReadOnlyList<string> labels, bool dropUnvoicedVowels = true)
        {
            var prosodies = ProsodySymbolsFromLabels(labels, dropUnvoicedVowels);

            var result = new List<(string, int)>(prosodies.Count);
            var currentPhrase = new List<(string, int)>();
            var currentTone = 0;

            for (var i = 0; i < prosodies.Count; i++)
            {
                var letter = prosodies[i];

                if (letter == "^")
                {
                    // 参照実装は文頭でASSERTするが、ここでは無条件に無視する
                    continue;
                }
                if (letter is "$" or "?" or "_" or "#")
                {
                    result.AddRange(FixPhoneTone(currentPhrase));
                    // "$"/"?" は文末、"_" はポーズ、"#" はアクセント句境界のいずれも、
                    // 次のアクセント句に備えるインデックスだけ進める
                    currentPhrase = [];
                    currentTone = 0;
                    continue;
                }
                if (letter == "[")
                {
                    currentTone++;
                    continue;
                }
                if (letter == "]")
                {
                    currentTone--;
                    continue;
                }

                if (letter == "cl")
                {
                    letter = "q"; // 「っ」
                }
                currentPhrase.Add((letter, currentTone));
            }

            // ループ末尾に未処理のフレーズが残った場合（無いはずだが安全のため）処理する
            if (currentPhrase.Count > 0)
            {
                result.AddRange(FixPhoneTone(currentPhrase));
            }

            return result;
        }

        /// <summary>tone 値を 0/1 の範囲に補正する（参照実装の <c>__fix_phone_tone</c> 相当）。</summary>
        public static List<(string, int)> FixPhoneTone(List<(string Phone, int Tone)> phoneToneList)
        {
            var toneValues = phoneToneList.Select(t => t.Tone).ToHashSet();
            if (toneValues.Count == 1)
            {
                if (!toneValues.Contains(0))
                {
                    throw new InvalidOperationException($"Unexpected tone values: [{string.Join(",", toneValues)}]");
                }
                return phoneToneList;
            }
            if (toneValues.Count == 2)
            {
                if (toneValues.Contains(0) && toneValues.Contains(1))
                {
                    return phoneToneList;
                }
                if (toneValues.Contains(-1) && toneValues.Contains(0))
                {
                    return phoneToneList
                        .Select(t => (t.Phone, t.Tone == -1 ? 0 : 1))
                        .ToList();
                }
                throw new InvalidOperationException($"Unexpected tone values: [{string.Join(",", toneValues)}]");
            }
            throw new InvalidOperationException($"Unexpected tone values: [{string.Join(",", toneValues)}]");
        }

        /// <summary>
        /// punctuation を含む音素列とアクセント列を合成する
        /// （参照実装の <c>__align_tones</c> 相当）。
        /// </summary>
        public static List<(string Phone, int Tone)> AlignTones(
            IReadOnlyList<string> phonesWithPunct,
            IReadOnlyList<(string Phone, int Tone)> phoneToneList)
        {
            var result = new List<(string, int)>(phonesWithPunct.Count);
            var toneIndex = 0;
            foreach (var phone in phonesWithPunct)
            {
                if (toneIndex >= phoneToneList.Count)
                {
                    result.Add((phone, 0)); // 余った punctuation
                }
                else if (phone == phoneToneList[toneIndex].Phone)
                {
                    result.Add((phone, phoneToneList[toneIndex].Tone));
                    toneIndex++;
                }
                else if (IsPunctuation(phone))
                {
                    result.Add((phone, 0));
                }
                else
                {
                    throw new InvalidOperationException(
                        $"Unexpected phone: {phone} (toneIndex={toneIndex})");
                }
            }
            return result;
        }

        /// <summary>音素数 nPhone を単語の文字数 nWord に均等近くに分配する。</summary>
        public static List<int> DistributePhone(int nPhone, int nWord)
        {
            if (nWord <= 0)
            {
                return [];
            }
            var phonesPerWord = new int[nWord];
            for (var i = 0; i < nPhone; i++)
            {
                var minTasks = int.MaxValue;
                var minIndex = 0;
                for (var j = 0; j < phonesPerWord.Length; j++)
                {
                    if (phonesPerWord[j] < minTasks)
                    {
                        minTasks = phonesPerWord[j];
                        minIndex = j;
                    }
                }
                phonesPerWord[minIndex]++;
            }
            return [.. phonesPerWord];
        }

        /// <summary>
        /// pyopenjtalk（open jtalk jpcommon_label.c）と同一の規則でラベル生成前の
        /// 発音前処理を行った NjdNode を返す:
        /// 1. 「単語の最初のモーラが長音記号なら除去する」（DotNetG2P はこの位置を
        ///    fallback 'a' に変えてしまうため、冒頭の長音モーラを取り除く）
        /// 2. 全角「ヲ」を「オ」へ変換する（pyopenjtalk は ヲ→o の1音素、
        ///    DotNetG2P は ヲ→w+o の2音素になるため。拗音の小文字 ォ は影響なし）
        /// sep 系（sep_text/sep_kata）は元ノードを使う。
        /// </summary>
        /// <summary>
        /// pyopenjtalk（open jtalk jpcommon_label.c）と同一の規則でラベル生成前の
        /// 発音前処理を行った NjdNode を返す:
        /// 1. 「アクセント句の最初のモーラが長音記号なら除去する」（DotNetG2P はこの位置を
        ///    fallback 'a' に変えてしまうため、アクセント句冒頭の長音モーラを取り除く）。
        ///    ChainFlag == true のノード（前のアクセント句に結合する語、例: ましょうの「う」）
        ///    の冒頭長音は除去せず、前の母音に置換される（jpcommon の長音解決に任せる）
        /// 2. 全角「ヲ」を「オ」へ変換する（pyopenjtalk は ヲ→o の1音素、
        ///    DotNetG2P は ヲ→w+o の2音素になるため。拗音の小文字 ォ は影響なし）
        /// sep 系（sep_text/sep_kata）は元ノードを使う。
        /// </summary>
        public static NjdNode NormalizeNodeForLabels(NjdNode node)
        {
            var pron = node.Pronunciation;
            var kata = pron?.ToKatakana();

            // 2) ヲ → オ（「ヲ」は発音モーラ上ほぼ独立したカタカナとして現れる）
            if (kata is not null && kata.Contains('ヲ'))
            {
                kata = kata.Replace('ヲ', 'オ');
                pron = Pronunciation.FromKatakana(kata, pron!.AccentPosition);
            }

            // 1) アクセント句冒頭の長音モーラを落とす（pyopenjtalk: First mora should not be long vowel symbol）。
            //    結合ノード（ChainFlag == true）は句内なので、長音は jpcommon が前母音へ解決する。
            var isChainStart = node.ChainFlag != true;
            if (pron is null || pron.Moras.Count == 0 || !pron.Moras[0].IsLong || !isChainStart)
            {
                // 変更が無ければ元ノードをそのまま使う
                if (ReferenceEquals(pron, node.Pronunciation))
                {
                    return node;
                }
                return RebuildNode(node, pron!);
            }

            var drop = 0;
            while (drop < pron.Moras.Count && pron.Moras[drop].IsLong)
            {
                drop++;
            }

            Pronunciation stripped;
            if (drop < kata!.Length)
            {
                // AccentType は変更しない（MeCab が冒頭長音の単語で acc>0 を返すことは Total 実測上ない）
                stripped = Pronunciation.FromKatakana(kata[drop..], pron.AccentPosition);
            }
            else
            {
                // 全モーラが長音（"ーー" 単独等）→ pyopenjtalk はラベルを出さない（No phoneme）。
                // 空の発音にしておくと JPCommonBuilder は sil のみのラベルを生成する。
                stripped = Pronunciation.FromKatakana("", 0);
            }

            return RebuildNode(node, stripped);
        }

        static NjdNode RebuildNode(NjdNode node, Pronunciation pron) => new(node.Surface, node.Details)
        {
            Pronunciation = pron,
            AccentType = node.AccentType,
            ChainFlag = node.ChainFlag,
            ChainRule = node.ChainRule,
        };

        /// <summary>PUNCTUATIONS との完全一致判定。</summary>
        public static bool IsPunctuation(string s)
        {
            foreach (var p in SymbolTable.Punctuations)
            {
                if (s == p)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>文字列が PUNCTUATIONS のみからなるか。</summary>
        static bool IsSubsetOfPunctuations(string s)
        {
            if (s.Length == 0)
            {
                return false;
            }
            foreach (var c in s)
            {
                if (c != '!' && c != '?' && c != '…' && c != ',' && c != '.' && c != '\'' && c != '-')
                {
                    return false;
                }
            }
            return true;
        }

        // ---- 参照実装の正規表現群 ----
        static readonly Regex P3Pattern = new(@"\-(.*?)\+", RegexOptions.Compiled);
        static readonly Regex A1Pattern = new(@"/A:([0-9\-]+)\+", RegexOptions.Compiled);
        static readonly Regex A2Pattern = new(@"\+(\d+)\+", RegexOptions.Compiled);
        static readonly Regex A3Pattern = new(@"\+(\d+)/", RegexOptions.Compiled);
        static readonly Regex E3Pattern = new(@"!(\d+)_", RegexOptions.Compiled);
        static readonly Regex F1Pattern = new(@"/F:(\d+)_", RegexOptions.Compiled);

        static int MatchInt(Regex pattern, string label)
        {
            var match = pattern.Match(label);
            if (!match.Success)
            {
                return -50; // 参照実装と同じ番兵値
            }
            return int.Parse(match.Groups[1].Value);
        }
    }
}
