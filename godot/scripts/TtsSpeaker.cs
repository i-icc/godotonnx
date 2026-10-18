using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using LocalAivmxTts.Engine;
using LocalAivmxTts.Inference;

namespace Demo;

/// <summary>スピーカーの状態。</summary>
public enum SpeakerState
{
    Idle,
    Initializing,
    Ready,
    Synthesizing,
    Playing,
    Error,
}

/// <summary>
/// Godot 側の音声再生アダプタ。
/// <see cref="LocalTtsEngine"/> (Godot 非依存) を AudioStreamPlayer に橋渡しする。
/// 合成キュー → 再生キュー → AudioStreamWAV 再生、中断・キャンセルを担う。
/// 合成タスク以外の状態変更はすべてメインスレッドで行う (pump 方式)。
/// </summary>
public partial class TtsSpeaker : Node
{
    record SpeechItem(int Seq, string Text, SynthesisOptions Options);

    readonly List<SpeechItem> pending = [];
    readonly Queue<(SpeechItem Item, SynthesisResult Result)> playQueue = new();
    readonly List<string> styleNames = [];

    LocalTtsEngine? engine;
    AudioStreamPlayer player = null!;
    CancellationTokenSource cancelSource = new();
    bool synthBusy;
    int nextSeq;

    [Signal] public delegate void StateChangedEventHandler(int state, string message);
    [Signal] public delegate void PlaybackStartedEventHandler(string text, string styleName, float duration);
    [Signal] public delegate void QueueChangedEventHandler(int synthPending, int playPending);

    public SpeakerState CurrentState => currentState;
    SpeakerState currentState = SpeakerState.Idle;

    /// <summary>音声モデルファイルのパス (初期化後)。</summary>
    public string ModelPath { get; private set; } = "";

    public override void _Ready()
    {
        player = new AudioStreamPlayer();
        AddChild(player);
        player.Finished += OnPlayerFinished;
    }

    public override void _ExitTree()
    {
        StopAll();
        engine?.Dispose();
        engine = null;
    }

    /// <summary>モデル群をバックグラウンドで読み込む。</summary>
    public async Task InitializeAsync(string aivmx, string bertModel, string tokenizer, string jdic)
    {
        SetState(SpeakerState.Initializing, "モデル読み込み中… (初回は数秒かかります)");
        try
        {
            engine?.Dispose();
            engine = await LocalTtsEngine.InitializeAsync(aivmx, bertModel, tokenizer, jdic);

            // 以降の状態更新をメインスレッドに戻す (await 後はスレッドプールにいるため)
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

            styleNames.Clear();
            var styles = new List<(int Id, string Name)>();
            foreach (var (name, id) in engine.Styles)
            {
                styles.Add((id, name));
            }
            styles.Sort((a, b) => a.Id.CompareTo(b.Id));
            foreach (var (_, name) in styles)
            {
                styleNames.Add(name);
            }
            ModelPath = aivmx;
            SetState(SpeakerState.Ready, $"準備完了 ({engine.SampleRate}Hz / スタイル {styleNames.Count} 種)");
        }
        catch (Exception ex)
        {
            SetState(SpeakerState.Error, $"初期化失敗: {ex.Message}");
        }
    }

    /// <summary>利用可能なスタイル名一覧。</summary>
    public IReadOnlyList<string> StyleNames => styleNames;

    /// <summary>発話をキューに入れる (メインスレッドから呼ぶ)。</summary>
    public void Speak(string text, int styleId, float styleWeight, float speed)
    {
        if (engine is null || currentState == SpeakerState.Error || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var options = new SynthesisOptions(StyleId: styleId, StyleWeight: styleWeight, SpeedScale: speed);
        pending.Add(new SpeechItem(nextSeq++, text, options));
        EmitSignal(SignalName.QueueChanged, pending.Count, playQueue.Count);
        Pump();
    }

    /// <summary>合成予定・再生予定を全て破棄し、再生を停止する。</summary>
    public void StopAll()
    {
        pending.Clear();
        playQueue.Clear();

        // 実行中の合成タスクを破棄するためトークン源を置き換える
        cancelSource.Cancel();
        cancelSource.Dispose();
        cancelSource = new CancellationTokenSource();

        if (player.Playing)
        {
            // Stop は Finished を発火しない
            player.Stop();
        }
        SetState(currentState is SpeakerState.Playing or SpeakerState.Synthesizing
            ? SpeakerState.Idle : currentState, "停止しました");
        EmitSignal(SignalName.QueueChanged, pending.Count, playQueue.Count);
    }

    /// <summary>
    /// アイドルなら次の合成を開始する (メインスレッド専用)。
    /// </summary>
    void Pump()
    {
        if (synthBusy || pending.Count == 0 || engine is null)
        {
            return;
        }

        var item = pending[0];
        pending.RemoveAt(0);
        synthBusy = true;
        SetState(SpeakerState.Synthesizing, $"合成中: {Truncate(item.Text)}");
        EmitSignal(SignalName.QueueChanged, pending.Count, playQueue.Count);

        var token = cancelSource.Token;
        _ = Task.Run(async () =>
        {
            SynthesisResult result;
            try
            {
                result = await engine.SynthesizeAsync(item.Text, item.Options, token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Callable.From(() => OnSynthesisCancelled()).CallDeferred();
                return;
            }
            catch (Exception ex)
            {
                var msg = ex.Message;
                Callable.From(() => OnSynthesisFailed(msg)).CallDeferred();
                return;
            }
            Callable.From(() => OnSynthesized(item, result)).CallDeferred();
        }, token);
    }

    void OnSynthesisCancelled()
    {
        synthBusy = false;
        SetState(currentState == SpeakerState.Playing ? currentState : SpeakerState.Idle, "停止しました");
        EmitSignal(SignalName.QueueChanged, pending.Count, playQueue.Count);
    }

    void OnSynthesisFailed(string message)
    {
        synthBusy = false;
        SetState(SpeakerState.Error, $"合成エラー: {message}");
        EmitSignal(SignalName.QueueChanged, pending.Count, playQueue.Count);
    }

    void OnSynthesized(SpeechItem item, SynthesisResult result)
    {
        synthBusy = false;
        playQueue.Enqueue((item, result));
        if (!player.Playing)
        {
            PlayNext();
        }
        else
        {
            SetState(SpeakerState.Playing, $"再生中… (合成待ち {pending.Count} 件)");
        }
        EmitSignal(SignalName.QueueChanged, pending.Count, playQueue.Count);
        Pump();
    }

    /// <summary>再生キューの先頭を AudioStreamPlayer で鳴らす (メインスレッド専用)。</summary>
    void PlayNext()
    {
        if (playQueue.Count == 0)
        {
            if (currentState == SpeakerState.Playing)
            {
                SetState(SpeakerState.Idle, "待機中");
            }
            return;
        }

        var (item, result) = playQueue.Dequeue();
        var duration = result.Audio.Length / (float)result.SampleRate;
        var stream = new AudioStreamWav
        {
            Format = AudioStreamWav.FormatEnum.Format16Bits,
            MixRate = result.SampleRate,
            Stereo = false,
            Data = To16BitBytes(result.Audio),
        };
        player.Stream = stream;
        player.Play();
        var styleName = styleNames.Count > item.Options.StyleId
            ? styleNames[item.Options.StyleId] : $"style{item.Options.StyleId}";
        SetState(SpeakerState.Playing, $"再生中: {Truncate(item.Text)}");
        EmitSignal(SignalName.PlaybackStarted, item.Text, styleName, duration);
        EmitSignal(SignalName.QueueChanged, pending.Count, playQueue.Count);
    }

    void OnPlayerFinished()
    {
        PlayNext();
    }

    void SetState(SpeakerState s, string message)
    {
        var changed = currentState != s || message != lastMessage;
        currentState = s;
        if (changed)
        {
            GD.Print($"[TtsSpeaker] {s}: {message}");
        }
        lastMessage = message;
        EmitSignal(SignalName.StateChanged, (int)s, message);
    }

    string lastMessage = "";

    static string Truncate(string text, int max = 26)
    {
        var t = text.Replace("\n", " ");
        return t.Length <= max ? t : t[..max] + "…";
    }

    /// <summary>
    /// float32 PCM → int16 little-endian バイト列 (clip → ×32768、参照 convert_to_16_bit_wav 相当)。
    /// </summary>
    static byte[] To16BitBytes(float[] audio)
    {
        var bytes = new byte[audio.Length * 2];
        for (var i = 0; i < audio.Length; i++)
        {
            var f = Math.Clamp(audio[i], -1f, 1f);
            var v = (short)Math.Round(f * 32768f);
            bytes[i * 2] = (byte)v;
            bytes[i * 2 + 1] = (byte)((uint)v >> 8);
        }
        return bytes;
    }
}
