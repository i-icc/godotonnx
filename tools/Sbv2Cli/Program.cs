using System.Text;
using System.Text.Json;
using DotNetG2P;
using GodotOnnx.Inference;
using GodotOnnx.Model;
using GodotOnnx.Nlp;
using GodotOnnx.Setup;

// Sbv2Cli: GodotOnnx の開発用 CLI。G2P 検証 / リファレンス dump 比較 / 音声合成を行う。
//
// 使い方:
//   onnx <model.onnx>                    ONNX IO 仕様の表示
//   meta <model.aivmx>                   AIVMX メタデータ表示
//   g2p [--jdic DIR] <text...>           G2P 結果表示
//   compare [--dumpdir DIR] [--jdic DIR] <slug...>  リファレンス dump との突合
//   paths                                 資産パスの探索結果と欠落の表示
//   download [--base DIR][--only NAME][--verify-only][--yes]
//                                         モデル/辞書の取得 (SHA-256 検証付き)
//   synth [options] <text...>            音声合成 + WAV 出力

const string DefaultDumpDir = "tools/pythonref/dump";

return args.Length == 0 ? ShowUsage() : args[0] switch
{
    "onnx" => CmdOnnx(args.Skip(1).ToArray()),
    "meta" => CmdMeta(args.Skip(1).ToArray()),
    "g2p" => CmdG2p(args.Skip(1).ToArray()),
    "g2p-debug" => CmdG2pDebug(args.Skip(1).ToArray()),
    "compare" => CmdCompare(args.Skip(1).ToArray()),
    "paths" => CmdPaths(),
    "download" => CmdDownload(args.Skip(1).ToArray()),
    "synth" => CmdSynth(args.Skip(1).ToArray()),
    _ => ShowUsage(),
};

static int ShowUsage()
{
    Console.WriteLine("""
        usage:
          Sbv2Cli onnx <model.onnx>
          Sbv2Cli meta <model.aivmx>
          Sbv2Cli g2p [--jdic DIR] <text...>
          Sbv2Cli compare [--dumpdir DIR] [--jdic DIR] [slug...]
          Sbv2Cli paths
          Sbv2Cli download [--base DIR][--only NAME][--verify-only][--yes]
          Sbv2Cli synth [--model P][--bert P][--tok P][--jdic DIR]
                         [--style NAME|--style-id N][--weight F][--speed F]
                         [--sdp F][--noise F][--noise-w F][--sid N]
                         [--out FILE.wav] <text...>
        """);
    return 2;
}

static Dictionary<string, string> ParseOptions(string[] args, List<string> positional)
{
    var opts = new Dictionary<string, string>(StringComparer.Ordinal);
    for (var i = 0; i < args.Length; i++)
    {
        if (args[i].StartsWith("--", StringComparison.Ordinal))
        {
            var key = args[i][2..];
            if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                opts[key] = args[++i];
            }
            else
            {
                opts[key] = "true";
            }
        }
        else
        {
            positional.Add(args[i]);
        }
    }
    return opts;
}

static G2PEngine CreateEngine(Dictionary<string, string> opts)
{
    return Sbv2JapaneseG2P.CreateEngine(GetJdic(opts));
}

static int CmdOnnx(string[] args)
{
    if (args.Length < 1)
    {
        return ShowUsage();
    }
    Console.WriteLine("== inputs ==");
    foreach (var i in AivmxOnnxInfo.GetInputs(args[0]))
    {
        Console.WriteLine(i.ToString());
    }
    Console.WriteLine("== outputs ==");
    foreach (var o in AivmxOnnxInfo.GetOutputs(args[0]))
    {
        Console.WriteLine(o.ToString());
    }
    return 0;
}

static int CmdDownload(string[] args)
{
    var positional = new List<string>();
    var opts = ParseOptions(args, positional);
    var baseDir = opts.TryGetValue("base", out var b) ? Path.GetFullPath(b) : GodotOnnxPaths.Root;
    var verifyOnly = opts.ContainsKey("verify-only");
    var yes = opts.ContainsKey("yes");
    string[]? only = opts.TryGetValue("only", out var o) ? o.Split(',') : null;

    var models = Path.Combine(baseDir, "models");
    Console.WriteLine($"base : {baseDir} (→ {models})");
    var report = ModelDownloader.Inspect(baseDir);
    foreach (var e in report.Entries)
    {
        var problems = e.Complete ? "OK" : string.Join("; ", e.Problems);
        Console.WriteLine($"  [{(e.Complete ? "OK " : "!! ")}] {e.Name}: {problems}");
    }
    if (verifyOnly)
    {
        return report.Complete ? 0 : 1;
    }

    Console.WriteLine();
    var entries = ModelCatalog.Entries;
    foreach (var e in entries)
    {
        if (only is not null && !only.Contains(e.Name, StringComparer.OrdinalIgnoreCase))
        {
            continue;
        }
        Console.WriteLine($"- {e.Name}  [{e.LicenseName}]  {ModelCatalog.PerEntryBytes(e) / 1048576}MB");
        Console.WriteLine($"  参照: {e.ManualUrl}");
        if (!string.IsNullOrEmpty(e.Credit))
        {
            Console.WriteLine($"  クレジット表記 (任意): {e.Credit}");
        }
    }
    Console.WriteLine($"合計: {ModelCatalog.TotalBytes / 1048576}MB → {models}");
    if (!yes)
    {
        Console.WriteLine("(dry run) 実際にダウンロードするには --yes を付けて再実行してください");
        return 0;
    }

    using var downloader = new ModelDownloader();
    var progress = new Progress<DownloadProgress>(p =>
        Console.WriteLine($"  {p.BytesDone * 100d / Math.Max(1, p.BytesTotal),5:F1}%  {p.Message}"));
    downloader.DownloadAllAsync(baseDir, progress, CancellationToken.None, only)
        .GetAwaiter().GetResult();
    Console.WriteLine("ダウンロード完了");
    return 0;
}

static int CmdPaths()
{
    Console.WriteLine($"root : {GodotOnnxPaths.Root}");
    var jdic = GodotOnnxPaths.Asset("naist-jdic", p => File.Exists(Path.Combine(p, "sys.dic")));
    Console.WriteLine($"model (aivmx)     : {Status(GodotOnnxPaths.Asset(GodotOnnxPaths.DefaultAivmxName, File.Exists))}");
    Console.WriteLine($"bert (model_fp16) : {Status(GodotOnnxPaths.Asset(GodotOnnxPaths.DefaultBertName, File.Exists))}");
    Console.WriteLine($"bert (tokenizer)  : {Status(GodotOnnxPaths.Asset(GodotOnnxPaths.DefaultTokenizerName, File.Exists))}");
    Console.WriteLine($"naist-jdic        : {Status(jdic)}");
    Console.WriteLine($"(探索順: <root>/models/ → <root>/research/ (開発環境の旧配置))");
    Console.WriteLine("入手先とライセンス: README \"Models you need\" および THIRD-PARTY-LICENSES.md");
    return 0;
}

static string Status(string path) =>
    File.Exists(path) || Directory.Exists(path)
        ? $"OK      {path}"
        : $"MISSING {path}";

static int CmdMeta(string[] args)
{
    if (args.Length < 1)
    {
        return ShowUsage();
    }
    var package = AivmxLoader.Load(args[0]);
    Console.WriteLine($"name        : {package.Manifest.Name}");
    Console.WriteLine($"uuid        : {package.Manifest.Uuid ?? "(なし)"}");
    Console.WriteLine($"version     : {package.HyperParameters.Version}");
    Console.WriteLine($"sampling    : {package.HyperParameters.SamplingRate} Hz");
    Console.WriteLine($"use_jp_extra: {package.HyperParameters.Data.UseJpExtraField}");
    Console.WriteLine($"add_blank   : {package.HyperParameters.AddBlank}");
    Console.WriteLine($"spk2id      : {string.Join(", ", package.HyperParameters.Data.Spk2Id.Select(kv => $"{kv.Key}={kv.Value}"))}");
    Console.WriteLine($"style2id    : {string.Join(", ", package.HyperParameters.Data.Style2Id.Select(kv => $"{kv.Key}={kv.Value}"))}");
    Console.WriteLine($"style_vecs  : ({package.NumStyles}, {package.StyleVectorDim})");
    Console.WriteLine($"row0[:8]    : {string.Join(", ", package.StyleVectors.GetStyleVectorLine(0).Take(8).Select(v => v.ToString("0.####")))}");
    return 0;
}

static int CmdG2p(string[] args)
{
    var positional = new List<string>();
    var opts = ParseOptions(args, positional);
    var engine = CreateEngine(opts);
    var text = string.Join(" ", positional);
    var norm = JapaneseTextNormalizer.Normalize(text);
    var g2p = Sbv2JapaneseG2P.G2p(norm, engine);
    Console.WriteLine($"norm   : {norm}");
    Console.WriteLine($"sepText: {string.Join(" | ", g2p.SepText)}");
    Console.WriteLine($"sepKata: {string.Join(" | ", g2p.SepKata)}");
    Console.WriteLine($"phones : {string.Join(" ", g2p.Phones)}");
    Console.WriteLine($"tones  : {string.Join("", g2p.Tones)}");
    Console.WriteLine($"word2ph: {string.Join(",", g2p.Word2Ph)}");
    Console.WriteLine($"counts : {string.Join(",", g2p.SepPhonemeCounts)}");
    Console.WriteLine($"bertText: {g2p.BertText}");
    return 0;
}

int CmdG2pDebug(string[] args)
{
    var positional = new List<string>();
    var opts = ParseOptions(args, positional);
    var engine = CreateEngine(opts);
    var text = string.Join(" ", positional);
    var norm = JapaneseTextNormalizer.Normalize(text);

    var nodes = engine.Analyze(norm);
    Console.WriteLine("nodes:");
    foreach (var n in nodes)
    {
        Console.WriteLine($"  [{n.Surface}] pron={n.Pronunciation?.ToKatakana()} acc={n.AccentType}");
    }
    var utterance = DotNetG2P.JPCommon.JPCommonBuilder.Build(
        nodes.Select(Sbv2JapaneseG2P.NormalizeNodeForLabels).ToList());
    var labels = DotNetG2P.JPCommon.FullContextLabel.Generate(utterance);
    Console.WriteLine($"labels({labels.Count}):");
    foreach (var l in labels)
    {
        Console.WriteLine($"  {l}");
    }
    var prosodies = Sbv2JapaneseG2P.ProsodySymbolsFromLabels(labels);
    Console.WriteLine($"prosodies: {string.Join(" ", prosodies)}");
    var woPunct = Sbv2JapaneseG2P.FixDotNetG2PPairs(Sbv2JapaneseG2P.G2PhoneToneWoPunct(labels));
    Console.WriteLine($"woPunct : {string.Join(" ", woPunct.Select(t => $"{t.Item1}/{t.Item2}"))}");
    var (sepText, sepKata) = Sbv2JapaneseG2P.TextToSepKata(nodes);
    List<List<string>> sepPhonemes = new(sepKata.Count);
    foreach (var kata in sepKata)
    {
        sepPhonemes.Add(MoraTable.KataToPhonemeList(kata));
    }
    MoraTable.HandleLong(sepPhonemes);
    var phoneWPunct = new List<string>(sepPhonemes.Sum(p => p.Count));
    foreach (var item in sepPhonemes)
    {
        phoneWPunct.AddRange(item);
    }
    Console.WriteLine($"sepKata : {string.Join("|", sepKata)}");
    Console.WriteLine($"wPunct  : {string.Join(" ", phoneWPunct)}");
    try
    {
        var aligned = Sbv2JapaneseG2P.AlignTones(phoneWPunct, woPunct);
        Console.WriteLine($"aligned : {string.Join(" ", aligned.Select(t => $"{t.Phone}/{t.Tone}"))}");
    }
    catch (Exception e)
    {
        Console.WriteLine($"align FAIL: {e.Message}");
    }
    return 0;
}

int CmdCompare(string[] args)
{
    var positional = new List<string>();
    var opts = ParseOptions(args, positional);
    var dumpDir = opts.TryGetValue("dumpdir", out var d) ? d : Path.Combine(GodotOnnxPaths.Root, DefaultDumpDir);

    if (!File.Exists(Path.Combine(dumpDir, "greet.json")))
    {
        Console.WriteLine($"dump dir が見つかりません: {dumpDir}");
        return 1;
    }

    var slugs = positional.Count > 0
        ? positional.ToArray()
        : Directory.GetFiles(dumpDir, "*.json").Select(Path.GetFileNameWithoutExtension).OfType<string>().Order().ToArray();

    var engine = CreateEngine(opts);
    var tokPath = opts.TryGetValue("tok", out var t) ? t : GodotOnnxPaths.Asset(GodotOnnxPaths.DefaultTokenizerName, File.Exists);
    var totalFail = 0;
    foreach (var slug in slugs)
    {
        var ok = CompareSlug(engine, dumpDir, tokPath, slug);
        if (!ok)
        {
            totalFail++;
        }
    }
    Console.WriteLine(totalFail == 0 ? $"全 {slugs.Length} slug PASS" : $"{totalFail}/{slugs.Length} slug FAIL");
    return totalFail == 0 ? 0 : 1;
}

bool CompareSlug(G2PEngine engine, string dumpDir, string tokPath, string slug)
{
    var path = Path.Combine(dumpDir, slug + ".json");
    using var doc = JsonDocument.Parse(File.ReadAllText(path));
    var root = doc.RootElement;

    var rawText = root.GetProperty("text").GetString()!;
    var dumpNorm = root.GetProperty("norm_text").GetString()!;
    var dumpSepText = root.GetProperty("sep_text").EnumerateArray().Select(e => e.GetString()!).ToArray();
    var dumpSepKata = root.GetProperty("sep_kata").EnumerateArray().Select(e => e.GetString()!).ToArray();
    var dumpPhones = root.GetProperty("phones").EnumerateArray().Select(e => e.GetString()!).ToArray();
    var dumpTones = root.GetProperty("tones").EnumerateArray().Select(e => e.GetInt32()).ToArray();
    var dumpWord2Ph = root.GetProperty("word2ph").EnumerateArray().Select(e => e.GetInt32()).ToArray();
    var dumpPhoneIds = root.GetProperty("phone_ids").EnumerateArray().Select(e => e.GetInt32()).ToArray();
    var dumpToneIds = root.GetProperty("tone_ids").EnumerateArray().Select(e => e.GetInt32()).ToArray();
    var dumpLangIds = root.GetProperty("lang_ids").EnumerateArray().Select(e => e.GetInt32()).ToArray();
    var dumpLabels = root.GetProperty("labels").EnumerateArray().Select(e => e.GetString()!).ToArray();
    var bertEl = root.GetProperty("bert");
    var dumpBertText = bertEl.GetProperty("bert_text").GetString()!;
    var dumpBertIds = bertEl.GetProperty("input_ids").EnumerateArray().Select(e => e.GetInt64()).ToArray();

    // 自前実装の実行
    var ourNorm = JapaneseTextNormalizer.Normalize(rawText);
    var g2p = Sbv2JapaneseG2P.G2p(dumpNorm, engine, useJpExtra: true);

    // phone_ids / tone_ids / lang_ids
    var ourPhoneIds = new int[g2p.Phones.Length];
    var ourToneIds = new int[g2p.Phones.Length];
    var ourLangIds = new int[g2p.Phones.Length];
    for (var i = 0; i < g2p.Phones.Length; i++)
    {
        ourPhoneIds[i] = SymbolTable.GetId(g2p.Phones[i]);
        ourToneIds[i] = g2p.Tones[i] + SymbolTable.ToneOffsetJp;
        ourLangIds[i] = SymbolTable.LanguageJp;
    }

    // dump 版 word2ph の再構成
    // (非句読点単語は dump の substitute トークナイザー仕様上 CLS/SEP 込みで +2 文字)
    var expectedDumpWord2Ph = new List<int>
    {
        1,
    };
    for (var i = 0; i < g2p.SepText.Length; i++)
    {
        var nTok = Sbv2JapaneseG2P.IsPunctuation(g2p.SepText[i]) ? 1 : g2p.SepText[i].Length + 2;
        expectedDumpWord2Ph.AddRange(Sbv2JapaneseG2P.DistributePhone(g2p.SepPhonemeCounts[i], nTok));
    }
    expectedDumpWord2Ph.Add(1);

    var (ids, _, _) = new BertTokenizer(tokPath).Encode(g2p.BertText);

    // 比較
    var failures = new List<string>();

    void Compare(string name, Func<string> ours, Func<string> theirs)
    {
        var a = ours();
        var b = theirs();
        if (a != b)
        {
            failures.Add(name);
            Console.WriteLine($"  [{slug}] {name} MISMATCH");
            Console.WriteLine($"    ours  : {Truncate(a)}");
            Console.WriteLine($"    theirs: {Truncate(b)}");
        }
    }

    Compare("norm", () => ourNorm, () => dumpNorm);
    Compare("sep_text", () => string.Join("|", g2p.SepText), () => string.Join("|", dumpSepText));
    Compare("sep_kata", () => string.Join("|", g2p.SepKata), () => string.Join("|", dumpSepKata));
    Compare("phones", () => string.Join(" ", g2p.Phones), () => string.Join(" ", dumpPhones));
    Compare("tones", () => string.Join(",", g2p.Tones), () => string.Join(",", dumpTones));
    Compare("phone_ids", () => string.Join(",", ourPhoneIds), () => string.Join(",", dumpPhoneIds));
    Compare("tone_ids", () => string.Join(",", ourToneIds), () => string.Join(",", dumpToneIds));
    Compare("lang_ids", () => string.Join(",", ourLangIds), () => string.Join(",", dumpLangIds));
    Compare("word2ph(dump規格)", () => string.Join(",", expectedDumpWord2Ph), () => string.Join(",", dumpWord2Ph));
    Compare("bert_text", () => g2p.BertText, () => dumpBertText);
    Compare("bert_ids", () => string.Join(",", ids), () => string.Join(",", dumpBertIds));

    // 不変条件
    if (g2p.Word2Ph.Length != g2p.BertText.Length + 2)
    {
        failures.Add("word2ph不変条件");
        Console.WriteLine($"  [{slug}] word2ph 長 {g2p.Word2Ph.Length} != bertText+2 ({g2p.BertText.Length + 2})");
    }

    // ラベル差分（DotNetG2P vs pyopenjtalk）は参考情報
    var labelDiff = 0;
    if (g2p.Labels.Length != dumpLabels.Length)
    {
        labelDiff = int.MaxValue;
    }
    else
    {
        for (var i = 0; i < g2p.Labels.Length; i++)
        {
            if (g2p.Labels[i] != dumpLabels[i])
            {
                labelDiff++;
            }
        }
    }

    var ok = failures.Count == 0;
    Console.WriteLine($"{ok.ToString().ToUpperInvariant(),-4} {slug,-10} 長さ: phones={g2p.Phones.Length} label差分={labelDiff}"
        + (failures.Count > 0 ? $"  失敗=[{string.Join(", ", failures)}]" : ""));
    return ok;
}

static string Truncate(string s) => s.Length <= 200 ? s : s[..200] + "…(切り詰め)";

static int CmdSynth(string[] args)
{
    var positional = new List<string>();
    var opts = ParseOptions(args, positional);
    var text = string.Join(" ", positional);
    if (text.Length == 0)
    {
        return ShowUsage();
    }

    ResolvePaths(opts);
    var synthesizer = new Sbv2Synthesizer(
        opts["model"],
        opts["bert"],
        opts["tok"],
        GetJdic(opts));

    var styleId = 0;
    if (opts.TryGetValue("style", out var styleName))
    {
        var styleDict = synthesizer.Style2Id.ToDictionary(kv => kv.Key, kv => kv.Value);
        if (!styleDict.TryGetValue(styleName, out styleId))
        {
            Console.WriteLine($"スタイル {styleName} が見つかりません。[{string.Join(", ", styleDict.Keys)}]");
            return 1;
        }
    }
    else if (opts.TryGetValue("style-id", out var sidStr))
    {
        styleId = int.Parse(sidStr);
    }

    var options = new SynthesisOptions(
        StyleId: styleId,
        StyleWeight: opts.TryGetValue("weight", out var w) ? float.Parse(w) : 1.0f,
        SpeedScale: opts.TryGetValue("speed", out var s) ? float.Parse(s) : 1.0f,
        SdpRatio: opts.TryGetValue("sdp", out var sdp) ? float.Parse(sdp) : 0.2f,
        NoiseScale: opts.TryGetValue("noise", out var n) ? float.Parse(n) : 0.6f,
        NoiseScaleW: opts.TryGetValue("noise-w", out var nw) ? float.Parse(nw) : 0.8f);
    var sid = opts.TryGetValue("sid", out var sidNum) ? int.Parse(sidNum) : 0;

    var sw = System.Diagnostics.Stopwatch.StartNew();
    var result = synthesizer.Synthesize(text, options, sid);
    var inferMs = sw.ElapsedMilliseconds;

    string outFile;
    if (opts.TryGetValue("out", out var outPath))
    {
        outFile = outPath;
    }
    else
    {
        outFile = $"tts_{DateTime.Now:yyyyMMdd_HHmmss}_{styleId}.wav";
    }
    result.SaveWave(outFile);

    Console.WriteLine($"norm    : {result.NormText}");
    Console.WriteLine($"phones  : {result.Phones.Length} (= text '{text}')");
    Console.WriteLine($"style   : id={styleId} weight={options.StyleWeight}");
    Console.WriteLine($"audio   : {result.Audio.Length} samples @ {result.SampleRate}Hz = {result.Audio.Length / (double)result.SampleRate:F2}s");
    Console.WriteLine($"elapsed : {inferMs}ms (load含む)");
    Console.WriteLine($"wav     : {Path.GetFullPath(outFile)}");
    return 0;
}

static void ResolvePaths(Dictionary<string, string> opts)
{
    if (!opts.ContainsKey("model"))
    {
        opts["model"] = GodotOnnxPaths.Asset(GodotOnnxPaths.DefaultAivmxName, File.Exists);
    }
    if (!opts.ContainsKey("bert"))
    {
        opts["bert"] = GodotOnnxPaths.Asset(GodotOnnxPaths.DefaultBertName, File.Exists);
    }
    if (!opts.ContainsKey("tok"))
    {
        opts["tok"] = GodotOnnxPaths.Asset(GodotOnnxPaths.DefaultTokenizerName, File.Exists);
    }
}

static string GetJdic(Dictionary<string, string> opts)
{
    if (opts.TryGetValue("jdic", out var j))
    {
        return j;
    }
    var env = Environment.GetEnvironmentVariable("NAIST_JDIC_PATH");
    if (!string.IsNullOrEmpty(env))
    {
        return env;
    }
    return GodotOnnxPaths.Asset("naist-jdic", p => File.Exists(Path.Combine(p, "sys.dic")));
}

/// <summary>プロジェクトルート検出 + 資産 (モデル/辞書) のパス解決。</summary>
static class GodotOnnxPaths
{
    /// <summary>ユーザーが README の手順で配置する標準の資産ディレクトリ。</summary>
    public const string UserAssetsDir = "models";

    /// <summary>開発環境の旧配置 (models/ が見つからない場合のフォールバック)。</summary>
    public const string LegacyAssetsDir = "research";

    public const string DefaultAivmxName = "kohaku.aivmx";
    public const string DefaultBertName = "bert/model_fp16.onnx";
    public const string DefaultTokenizerName = "bert/tokenizer.json";

    public static readonly string Root = FindRoot();

    /// <summary>資産 (root 相対) を models/ → research/ の順に探し、最初に存在するものを返す。</summary>
    public static string Asset(string rel, Func<string, bool> exists)
    {
        var preferred = Path.Combine(Root, UserAssetsDir, rel);
        if (exists(preferred))
        {
            return preferred;
        }
        var legacy = Path.Combine(Root, LegacyAssetsDir, rel);
        if (exists(legacy))
        {
            return legacy;
        }
        return preferred;
    }

    static string FindRoot()
    {
        // cwd が root の場合もあるし、bin から実行される場合もある
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; dir = dir.Parent, i++)
        {
            if (File.Exists(Path.Combine(dir.FullName, "GodotOnnx.sln")))
            {
                return dir.FullName;
            }
        }
        return Directory.GetCurrentDirectory();
    }
}
