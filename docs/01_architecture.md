# 01 アーキテクチャ設計

## 構成図

```text
Godot 4 .NET (game)
  └── GodotTtsAdapter (Godot 依存・薄い)
        └── AivisTts.Runtime (C# Library, Godot 非依存)
             ├── AivmxPackage       ONNX(AIVMX) ロード + manifest/hparams/style_vector
             ├── SarashinmoText     日本語正規化 + 数字→漢字
             ├── SarashinG2P        DotNetG2P→SBV2音素/アクセント / word2ph 橋
             ├── MoraTables         mora_list/MORA_KATA 換算表
             ├── SymbolId           SBV2 記号表・ID変換
             ├── BertTokenizer      文字トークナイザー (deberta char-wwm)
             ├── BertFeatures       DeBERTa ONNX → (1024,L) 特徴
             ├── Sbv2Synthesizer    ONNX SynthesizerTrn 実行・後処理
             ├── SpeechQueue        発話キュー/キャンセル/割込み/キャッシュ
             └── LocalTtsEngine     ファサード (非同期/プログレス)
  └── AudioStreamPlayer (wave float[] → AudioStreamWAV 16bit)
```

## 設計方針

- Godot に依存するのは `GodotTtsAdapter` のみ。コアライブラリは POCO + System.* のみ。
- 合成は Task 上で逐次実行し、`CancellationToken` による中断を行う (ONNX 実行自体はキャンセル不可のため perf 単位でステップ分岐可能な分割合成を採用)。
- 同一 AIVMX の style 変更のみは `style_vec` 差し替えでモデル再ロード不要。
- 音声キャッシュ: (text, style, speed, styleWeight, sdp のハッシュ) → wave float[].

## モデル/データ同梱レイアウト (配布物)

```
StreamingAssets/
  Aivis/
    kohaku.aivmx            [255MB]
    bert/model_fp16.onnx    [653MB]
    bert/vocab.txt
    naist-jdic/ (4 files)   [~90MB 展開]
```

初回実行時に Godot 側で StreamingAssets → user:// もしくは直接パス参照 (Mac/Win 一致は `OS.GetPlatformData`? シンプル化のためエディタ/パッケージ両対応のリゾルバ実装)。

## 非同期 API (ファサード概括)

```csharp
await engine.InitializeAsync(modelPath, bertPath, dicPath, styles);
var items = engine.Synthesize(text, styleName, speed, styleWeight, sdpRatio, cancellationToken);
engine.Play(items => audioStreamPlayer);
engine.CancelAll(); engine.Queue(text, …);
```

## ONNX Runtime 構成

- NuGet `Microsoft.ML.OnnxRuntime` (CPU, osx-arm64+win-x64)
- session option: `ArenaExtendStrategy=kSameAsRequested`, `IntraOpNumThreads=Environment.ProcessorCount`
- 排他: 推論ロック (AivisSpeech 同様)

## デモ実装 (Phase 3, 2026-10-18 時点)

`godot/` に Godot 4.7 (.NET) デモを実装済み。構成:

```
godot/
  Demo.csproj                  Godot.NET.Sdk/4.7.0 → src/LocalAivmxTts を参照
  project.godot                980x720, main=scenes/Demo.tscn
  scenes/Demo.tscn             ルート Control のみ (UI は Demo.cs がコード構築)
  scripts/TtsPaths.cs          モデルパス解決 (sln から research/ を探索。env LOCALAIVMX_ROOT 上書き)
  scripts/TtsSpeaker.cs        アダプタ: LocalTtsEngine → AudioStreamWAV 再生 / キュー / 停止
  scripts/Demo.cs              UI 構築 + 操作 (スタイル4種 / 話速 / 重み / 履歴 / Ctrl+Enter / Esc)
```

- ライブラリ側ファサード: `src/LocalAivmxTts/Engine/LocalTtsEngine.cs`
  (非同期読み込み / 合成の直列化 / (text,style,weight,speed,sdp) メモリキャッシュ)
- 状態遷移はアダプタ側で **メインスレッドのみ** で触碰る pump 方式。
  合成タスクは `Callable.From(...).CallDeferred()` で結果をメインスレッドへ返す。
- 音声変換: float PCM → clip ×32768 → int16 LE → `AudioStreamWav (Format16Bits, mono)`。
- ヘッドレス検証: `TTS_DEMO_SMOKE=1` で初期化→1発話→終了を自動実行
  (`TTS_DEMO_SHOT`/`TTS_DEMO_SHOT2` でビューポートのスクリーンショット保存)。
- **標準版 Godot では C# スクリプトが無効** のため .NET (mono) 版エディタが必須。
- Phase 4 (配布: StreamingAssets レイアウト / エクスポート) は未着手。

