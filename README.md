# godotonnx — ローカル AIVMX TTS (Godot 4 / C#)

AivisSpeech 互換の AIVMX 音声モデル (**コハク**) を、Python / 外部 TTS サーバなしで
**Godot 4 (.NET/C#) + ONNX Runtime だけでローカル発話**させるライブラリとデモです。

```
日本語テキスト → 正規化 → G2P (DotNetG2P + naist-jdic)
             → BERT (DeBERTa v2 char ONNX)
             → AIVMX 音声モデル (ONNX) → PCM (44.1kHz)
```

## 構成

| パス | 内容 |
|---|---|
| `src/LocalAivmxTts` | コアライブラリ (Godot 非依存)。正規化 / G2P / BERT / 合成 / ファサード |
| `tools/Sbv2Cli` | 検証 CLI (`compare` = pyopenjtalk dump 突合, `synth` 合成, `meta` 等々) |
| `godot/` | Godot 4.7 (.NET) デモアプリ |
| `docs/` | 設計・互換検証の記録 |
| `research/` | 参照用リポジトリ群・モデル (**git 管理外**。別途取得) |

## セットアップ

- .NET SDK 8.0
- **Godot 4.7 .NET (mono) 版** — 標準版では C# スクリプトが動きません
- モデル 3 点を以下に配置 (スクリプトが `LocalAivmxTts.sln` があるディレクトリから `research/` を探索します):
  - `research/kohaku.aivmx` … AivisSpeech から入手 (コハク AIVMX)
  - `research/bert/model_fp16.onnx`, `research/bert/tokenizer.json`
  - `research/naist-jdic/` (sys.dic 等 8 ファイル)

## 実行

### Godot デモ

```sh
export PATH="$HOME/.dotnet:$PATH"
dotnet build godot/Demo.csproj
~/Applications/Godot_mono.app/Contents/MacOS/Godot --path godot
```

- スタイル 4 種 (ノーマル / あまあま / せつなめ / ねむたい)・話速・スタイル重みを UI で指定
- Ctrl+Enter で発話、Esc で停止、履歴ダブルクリックで再発話 (キャッシュで即再生)

### CLI (検証)

```sh
cd tools/Sbv2Cli && dotnet run -- compare   # G2P が pyopenjtalk と一致するか (16/16 slug PASS)
cd tools/Sbv2Cli && dotnet run -- synth --style あまあま --out out.wav "こんにちは"
cd tools/Sbv2Cli && dotnet run -- meta ../../research/kohaku.aivmx
```

### ヘッドレス動作確認 (CI でも使用可)

```sh
TTS_DEMO_SMOKE=1 <Godot .NET> --headless --path godot
# → [TtsSpeaker] Ready → Synthesizing → Playing → Idle → [Smoke] exit
```

## 動作特性

- 同一入力でも波形は実行毎に微妙に変化します (モデル内 RandomNormalLike 由来、AivisSpeech と同仕様)
- モデル読み込み後の同一条件の再合成はメモリキャッシュから即時応答
- 現在のターゲット: macOS (Apple Silicon) / Windows x64。実装メモは `docs/02_g2p_compat.md` 参照
