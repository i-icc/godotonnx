using System;
using System.Collections.Generic;
using System.IO;
using Godot;

namespace GodotOnnx.Godot;

/// <summary>
/// モデル/辞書ファイルのパス解決。
/// ユーザーは README の手順に従って models/ 以下に配置する。探索順:
///   1. env GODOTONNX_ROOT 直下 (上書き指定)
///   2. リポジトリルート (GodotOnnx.sln を親方向に探索)
///   3. res:// (PCK に同梱した場合。エディタ実行中は godot/ プロジェクト)
///   4. user:// (エクスポート済みゲームで user://models/ を更新した場合)
/// 各 base とも直下の models/ を優先し、開発環境の旧配置 research/ もフォールバックする。
/// </summary>
public static class TtsPaths
{
    public record ModelPaths(string Aivmx, string BertModel, string Tokenizer, string NaistJdic);

    /// <summary>既定の AIVMX パッケージ名。</summary>
    public const string DefaultAivmxName = "kohaku.aivmx";

    /// <summary>ユーザーに案内する資産ディレクトリ名。</summary>
    public const string UserDir = "models";

    /// <summary>旧配置 (開発環境) の資産ディレクトリ名。models/ が見つからない場合のフォールバック。</summary>
    public const string LegacyDir = "research";

    public static ModelPaths? Resolve()
    {
        foreach (var start in BaseCandidates())
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

    /// <summary>モデルが見つからなかった場合の診断メッセージ (探索した候補を列挙)。</summary>
    public static string DescribeMissing()
    {
        var lines = new List<string>
        {
            "モデル/辞書が見つかりません。README の手順に従って配置してください:",
            $"  <base>/{UserDir}/{DefaultAivmxName}",
            $"  <base>/{UserDir}/bert/model_fp16.onnx",
            $"  <base>/{UserDir}/bert/tokenizer.json",
            $"  <base>/{UserDir}/naist-jdic/sys.dic",
            $"(<base> 直下の {UserDir}/ を優先し、開発環境用の旧配置 {LegacyDir}/ もフォールバックする)",
        };
        foreach (var start in BaseCandidates())
        {
            var root = FindRepoRoot(start) ?? start;
            lines.Add($"  base: {root}");
        }
        return string.Join("\n", lines);
    }

    static IEnumerable<string> BaseCandidates()
    {
        // 1) 環境変数による上書き (デモ用)
        var envRoot = System.Environment.GetEnvironmentVariable("GODOTONNX_ROOT");
        if (!string.IsNullOrEmpty(envRoot))
        {
            yield return envRoot;
        }

        // 2) エディタ実行時: res:// = プロジェクトディレクトリ (godot/)
        //    Globalize に失敗する環境 (エクスポート後) では実行ファイル基準で探す
        string? resDir = null;
        try
        {
            resDir = ProjectSettings.GlobalizePath("res://");
        }
        catch
        {
            // 次の候補へ
        }
        yield return resDir ?? AppContext.BaseDirectory;

        // 3) user:// (グローバルユーザデータ領域)
        string? userDir = null;
        try
        {
            userDir = ProjectSettings.GlobalizePath("user://");
        }
        catch
        {
            // 取れない環境ではスキップ
        }
        if (userDir is not null)
        {
            yield return userDir;
        }
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
        var aivmx = Pick(root, DefaultAivmxName, File.Exists);
        var bert = Pick(root, Path.Combine("bert", "model_fp16.onnx"), File.Exists);
        var tokenizer = Pick(root, Path.Combine("bert", "tokenizer.json"), File.Exists);
        var jdic = Pick(root, "naist-jdic", p => File.Exists(Path.Combine(p, "sys.dic")));
        return aivmx is not null && bert is not null && tokenizer is not null && jdic is not null
            ? new ModelPaths(aivmx, bert, tokenizer, jdic)
            : null;
    }

    /// <summary>root 下の dir/rel を models/ → research/ の順に探し、存在するものを返す。</summary>
    static string? Pick(string root, string rel, Func<string, bool> exists)
    {
        foreach (var dir in new[] { UserDir, LegacyDir })
        {
            var p = Path.Combine(root, dir, rel);
            if (exists(p))
            {
                return p;
            }
        }
        return null;
    }
}
