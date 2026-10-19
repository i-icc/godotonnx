using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using GodotOnnx.Setup;

namespace GodotOnnx.Godot;

/// <summary>
/// モデル資産の検査と取得を担うノード。
/// ライセンス同意の提示は呼び出し側が担うこと (同意前に DownloadAsync を呼ばない)。
/// 進捗と完了はシグナルでメインスレッドへ通知する。
/// </summary>
public partial class TtsModelManager : Node
{
    [Signal] public delegate void ProgressChangedEventHandler(string message, double fraction);

    [Signal] public delegate void FinishedEventHandler(bool ok, string message);

    CancellationTokenSource? cancelSource;

    /// <summary>資産の整備状況 (ネットワーク参照なし、SHA-256 照合)。</summary>
    public static InspectResult Inspect(string baseDir) => ModelDownloader.Inspect(baseDir);

    /// <summary>既定の取得先 base (user:// 直下)。</summary>
    public static string DefaultBaseDir() => ProjectSettings.GlobalizePath("user://");

    /// <summary>カタログに基づき models/ 以下へ取得する。整備済みのものはスキップされる。</summary>
    public async Task<bool> DownloadAsync(string baseDir, IReadOnlyList<string>? onlyNames = null)
    {
        Cancel();
        cancelSource = new CancellationTokenSource();
        var token = cancelSource.Token;

        void OnProgress(DownloadProgress p)
        {
            var fraction = p.BytesTotal <= 0 ? -1d : (double)p.BytesDone / p.BytesTotal;
            Callable.From(() => EmitSignal(SignalName.ProgressChanged, p.Message, fraction)).CallDeferred();
        }

        try
        {
            var progress = new Progress<DownloadProgress>(OnProgress);
            using var downloader = new ModelDownloader();
            await downloader.DownloadAllAsync(baseDir, progress, token, onlyNames).ConfigureAwait(false);

            Callable.From(() => EmitSignal(SignalName.Finished, true, "モデルの取得が完了しました")).CallDeferred();
            return true;
        }
        catch (OperationCanceledException)
        {
            Callable.From(() => EmitSignal(SignalName.Finished, false, "中断しました")).CallDeferred();
            return false;
        }
        catch (Exception ex)
        {
            Callable.From(() => EmitSignal(SignalName.Finished, false, $"取得に失敗しました: {ex.Message}")).CallDeferred();
            return false;
        }
    }

    /// <summary>進行中のダウンロードを中断する。</summary>
    public void Cancel() => cancelSource?.Cancel();

    public override void _ExitTree()
    {
        Cancel();
    }
}
