using System.Collections.Generic;

namespace GodotOnnx.Setup;

/// <summary>
/// ユーザーが配置すべきモデル/辞書資産の既定カタログ。
/// パスは「関連する各 base (リポジトリルート / res:// / user://) 直下の models/」基準。
/// サイズと SHA-256 は AivisSpeech 1.0 / Hugging Face / r9y9-open_jtalk v1.11.1 の
/// 実データで検算済み (URL は複数所持し、ハッシュ不一致なら上書き拒否する)。
/// </summary>
public static class ModelCatalog
{
    /// <summary>単一ファイルの取得指示。</summary>
    public sealed record FileSpec(string RelativePath, long SizeBytes, string Sha256, string[] Urls);

    /// <summary>tar.gz アーカイブを取得して展開する指示 (辞書など複数ファイル)。</summary>
    public sealed record ArchiveSpec(
        string ArchiveName,
        long SizeBytes,
        string Sha256,
        string[] Urls,
        string TargetRelativeDir,
        string PrefixInArchive,
        IReadOnlyList<MemberSpec> Members);

    /// <summary>アーカイブ内の1メンバー (展開先は TargetRelativeDir 直下)。</summary>
    public sealed record MemberSpec(string MemberName, string Sha256);

    public sealed record Entry(
        string Name,
        string LicenseName,
        string LicenseSummary,
        string Credit,
        string ManualUrl,
        IReadOnlyList<FileSpec> Files,
        ArchiveSpec? Archive);

    /// <summary>標準カタログ [コハク voice, BERT, NAIST 辞書]。</summary>
    public static IReadOnlyList<Entry> Entries => [Voice, Bert, Dictionary];

    /// <summary>コハク (AIVMX voice model)。ACML 1.0 — 本文はモデル内部に埋め込み。</summary>
    public static Entry Voice { get; } = new(
        Name: "voice: コハク (AIVMX)",
        LicenseName: "ACML 1.0",
        LicenseSummary: "Full license text is embedded inside the .aivmx; redistribution requires keeping it (see THIRD-PARTY-LICENSES.md).",
        Credit: "AivisSpeech: コハク",
        ManualUrl: "https://hub.aivis-project.com/aivm-models/22e8ed77-94fe-4ef2-871f-a86f94e9a579",
        Files:
        [
            new("kohaku.aivmx", 255326987, "3f5c08b52bb8a64efd361268580c81510f96c927cd6905aa7dbae6851333270a",
                [
                    "https://assets.aivis-project.com/aivm-models/a04de3ee-1440-4b55-8ba5-b8b6a452f7a4/ee0b10c4-8b0a-44ea-a27d-cfb96e942c98.aivmx",
                ]),
        ],
        Archive: null);

    /// <summary>BERT (DeBERTa v2 large JP char)。CC-BY-SA-4.0 (tsukumijima 変換版)。</summary>
    public static Entry Bert { get; } = new(
        Name: "BERT: DeBERTa v2 large JP char (FP16 ONNX)",
        LicenseName: "CC-BY-SA-4.0",
        LicenseSummary: "ShareAlike applies to derivative distributions.",
        Credit: "",
        ManualUrl: "https://huggingface.co/tsukumijima/deberta-v2-large-japanese-char-wwm-onnx",
        Files:
        [
            new("bert/model_fp16.onnx", 653075699, "23f633ae7c5900ff82b35a428b67a54e7e7911d5d6a6dcfc77967be8f1c94dc6",
                [
                    "https://huggingface.co/tsukumijima/deberta-v2-large-japanese-char-wwm-onnx/resolve/main/model_fp16.onnx",
                ]),
            new("bert/tokenizer.json", 432079, "9e09664b4879bced4b2d00e9d996e95dcd6ca714014bf90aeb3fa9a3f70cac4f",
                [
                    "https://huggingface.co/tsukumijima/deberta-v2-large-japanese-char-wwm-onnx/resolve/main/tokenizer.json",
                ]),
        ],
        Archive: null);

    /// <summary>NAIST 日本語辞書 (Open JTalk 1.11 UTF-8 辞書)。BSD — COPYING 同梱。</summary>
    public static Entry Dictionary { get; } = new(
        Name: "dictionary: NAIST Japanese dictionary (Open JTalk 1.11 UTF-8)",
        LicenseName: "BSD",
        LicenseSummary: "COPYING is installed together with the dictionary; keep it when redistributing.",
        Credit: "",
        ManualUrl: "https://github.com/r9y9/open_jtalk/releases/tag/v1.11.1",
        Files: [],
        Archive: new(
            "open_jtalk_dic_utf_8-1.11.tar.gz",
            23646843,
            "fe6ba0e43542cef98339abdffd903e062008ea170b04e7e2a35da805902f382a",
            [
                "https://github.com/r9y9/open_jtalk/releases/download/v1.11.1/open_jtalk_dic_utf_8-1.11.tar.gz",
                "https://downloads.sourceforge.net/open-jtalk/open_jtalk_dic_utf_8-1.11.tar.gz",
            ],
            "naist-jdic",
            "open_jtalk_dic_utf_8-1.11",
            [
                new MemberSpec("sys.dic", "ca57d9029691a70a5dfb99afc2844180256161d7130da65b1a867510e129b9a6"),
                new MemberSpec("unk.dic", "ce97851ecda075914fa3ffe7294a1ab34ee4f6d56ba6bf9197d74143b5dffbfe"),
                new MemberSpec("char.bin", "888ee94c5a8a7a26d24ab3f1b7155441351954fd51ea06b4a2f78bd742492b2f"),
                new MemberSpec("matrix.bin", "62fd16b4f64c851d5dc352ef0d5740c5fc83ddc7c203b2b0b1fc5271969a14ce"),
                new MemberSpec("left-id.def", "db1adac8a7f9e5854cd82ea044c85115249206c8181b9d88cf92ae2ee5e87b84"),
                new MemberSpec("right-id.def", "db1adac8a7f9e5854cd82ea044c85115249206c8181b9d88cf92ae2ee5e87b84"),
                new MemberSpec("pos-id.def", "3460aa742053085af47cdfc889a1e0e6f557e89b406e501ba81c9ccc286de0c7"),
                new MemberSpec("rewrite.def", "7f7c8dfbfe24092e8a149a9b6e0a3a7f1c2cf37d6c3dc29d1cccc6c004da9c1c"),
                new MemberSpec("COPYING", "f4eca42ebd930e2c6e57fca58319d989bebcd1510cb7714b149c50f5425135ea"),
            ]));

    /// <summary>カタログ全体のダウンロード所要バイト数 (検証圧縮率無し)。</summary>
    public static long TotalBytes => PerEntryBytes(Voice) + PerEntryBytes(Bert) + PerEntryBytes(Dictionary);

    public static long PerEntryBytes(Entry e)
    {
        long n = 0;
        foreach (var f in e.Files)
        {
            n += f.SizeBytes;
        }
        if (e.Archive is not null)
        {
            n += e.Archive.SizeBytes;
        }
        return n;
    }
}
