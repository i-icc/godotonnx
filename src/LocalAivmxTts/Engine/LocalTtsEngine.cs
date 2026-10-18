using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LocalAivmxTts.Inference;

namespace LocalAivmxTts.Engine;

/// <summary>
/// エンジンの状態。
/// </summary>
public enum TtsEngineState
{
    /// <summary>未初期化。</summary>
    NotInitialized,
    /// <summary>初期化中 (モデル読み込み)。</summary>
    Initializing,
    /// <summary>使用可能。</summary>
    Ready,
}

/// <summary>
/// AIVMX ローカル音声合成のファサード (Godot 等ホスト非依存)。
/// </summary>
/// <remarks>
/// - モデル読み込み / 合成を非同期タスクで実行し、合成は 1 件ずつ直列化する。
/// - 同一 (text, style, weight, speed, sdp) の合成結果をメモリキャッシュする。
/// - スレッドセーフ。UI ホストはイベント/Task 経由で状態を受け取る。
/// </remarks>
public sealed class LocalTtsEngine : IDisposable
{
    readonly SemaphoreSlim gate = new(1, 1);
    readonly ConcurrentDictionary<string, SynthesisResult> cache = new();
    Sbv2Synthesizer? synthesizer;
    volatile TtsEngineState state = TtsEngineState.NotInitialized;

    /// <summary>エンジンの現在の状態。</summary>
    public TtsEngineState State => state;

    /// <summary>サンプリングレート (未初期化時 44100)。</summary>
    public int SampleRate => synthesizer?.SampleRate ?? 44100;

    /// <summary>スタイル名 → ID (例: ノーマル=0, あまあま=1 ...)。</summary>
    public IReadOnlyDictionary<string, int> Styles =>
        synthesizer?.Style2Id ?? new Dictionary<string, int>();

    /// <summary>キャッシュ済みエントリ数。</summary>
    public int CachedCount => cache.Count;

    /// <summary>初期化時に ModelsLoaded を発火させるなどの進行通知 (state, message)。</summary>
    public event Action<TtsEngineState, string>? Progress;

    LocalTtsEngine() { }

    /// <summary>
    /// エンジンを生成し、モデル 3 種 (音声 / BERT / 辞書) をバックグラウンドで読み込む。
    /// </summary>
    public static async Task<LocalTtsEngine> InitializeAsync(
        string aivmxPath, string bertModelPath, string tokenizerJsonPath,
        string naistJdicPath, CancellationToken ct = default)
    {
        var engine = new LocalTtsEngine();
        await engine.gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            engine.state = TtsEngineState.Initializing;
            engine.Progress?.Invoke(engine.state, "音声モデルを読み込み中…");
            var synth = await Task.Run(() => new Sbv2Synthesizer(
                aivmxPath, bertModelPath, tokenizerJsonPath, naistJdicPath), ct)
                .ConfigureAwait(false);
            engine.synthesizer = synth;
            engine.state = TtsEngineState.Ready;
            engine.Progress?.Invoke(engine.state,
                $"準備完了 ({synth.SampleRate}Hz, スタイル {synth.Style2Id.Count} 種)");
            return engine;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            engine.state = TtsEngineState.NotInitialized;
            engine.Progress?.Invoke(engine.state, $"初期化失敗: {ex.Message}");
            throw new TtsException($"エンジンの初期化に失敗しました: {ex.Message}", ex);
        }
        finally
        {
            engine.gate.Release();
        }
    }

    /// <summary>
    /// テキストを合成する (キャッシュヒット時は即返す)。呼び出しは直列化される。
    /// </summary>
    public async Task<SynthesisResult> SynthesizeAsync(
        string text, SynthesisOptions? options = null, CancellationToken ct = default)
    {
        options ??= SynthesisOptions.Default;
        var synth = synthesizer
            ?? throw new TtsException("エンジンが未初期化です (InitializeAsync を先に呼んでください)");

        var key = $"{text}\u0001{options.StyleId}\u0001{options.StyleWeight}\u0001{options.SpeedScale}\u0001{options.SdpRatio}";
        if (cache.TryGetValue(key, out var hit))
        {
            return hit;
        }

        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ct.ThrowIfCancellationRequested();
            if (cache.TryGetValue(key, out hit))
            {
                return hit;
            }
            var result = await Task.Run(
                () => synth.Synthesize(text, options), ct).ConfigureAwait(false);
            cache[key] = result;
            return result;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>メモリキャッシュを破棄する。</summary>
    public void ClearCache() => cache.Clear();

    /// <inheritdoc />
    public void Dispose()
    {
        synthesizer?.Dispose();
        synthesizer = null;
        cache.Clear();
        state = TtsEngineState.NotInitialized;
        gate.Dispose();
    }
}
