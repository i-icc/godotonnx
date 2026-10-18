using System;
using System.Collections.Generic;
using System.IO;
using Godot;

namespace Demo;

/// <summary>
/// モデル/辞書ファイルのパス解決。
/// 開発環境ではリポジトリルート (GodotOnnx.sln のある場所) の research/ を探索する。
/// 将来の配布 (Phase 4) では user:// や res://Aivis/ へのフォールバックを追加する。
/// </summary>
public static class TtsPaths
{
    public record ModelPaths(string Aivmx, string BertModel, string Tokenizer, string NaistJdic);

    /// <summary>既定のモデル名。</summary>
    public const string DefaultAivmxName = "kohaku.aivmx";

    public static ModelPaths? Resolve()
    {
        // 1) 環境変数による上書き (デモ用)
        var envRoot = System.Environment.GetEnvironmentVariable("GODOTONNX_ROOT");
        var candidates = new List<string>();
        if (!string.IsNullOrEmpty(envRoot))
        {
            candidates.Add(envRoot);
        }

        try
        {
            // エディタ実行時: res:// = プロジェクトディレクトリ (godot/)
            var res = ProjectSettings.GlobalizePath("res://");
            candidates.Add(res);
        }
        catch
        {
            // res:// が取れない環境 (エクスポート後) は実行ファイル基準で幅優先に探す
            candidates.Add(AppContext.BaseDirectory);
        }

        foreach (var start in candidates)
        {
            var root = FindRepoRoot(start) ?? start;
            var paths = TryBuild(root);
            if (paths is not null)
            {
                return paths;
            }
        }
        return null;
    }

    static string? FindRepoRoot(string start)
    {
        var dir = Directory.Exists(start) ? start : Path.GetDirectoryName(Path.GetFullPath(start));
        while (!string.IsNullOrEmpty(dir))
        {
            if (File.Exists(Path.Combine(dir, "GodotOnnx.sln")))
            {
                return dir;
            }
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    static ModelPaths? TryBuild(string root)
    {
        var aivmx = Path.Combine(root, "research", DefaultAivmxName);
        var bert = Path.Combine(root, "research", "bert", "model_fp16.onnx");
        var tokenizer = Path.Combine(root, "research", "bert", "tokenizer.json");
        var jdic = Path.Combine(root, "research", "naist-jdic");
        return File.Exists(aivmx) && File.Exists(bert) && File.Exists(tokenizer)
               && File.Exists(Path.Combine(jdic, "sys.dic"))
            ? new ModelPaths(aivmx, bert, tokenizer, jdic)
            : null;
    }
}
