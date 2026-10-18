# Phase 0: 技術検証レポート

日付: 2026-10-17
対象: Godot 4 .NET 内で AIVMX (Style-Bert-VITS2 JP-Extra) モデルを直接推論し、ローカルで日本語 TTS を実現する構成の実現可能性検証

## 1. 実現可能性の結論

**実現可能。** 以下の検証により、AivisSpeech-Engine と同等の推論パイプラインを C# (+ ONNX Runtime) で構成できることを確認した。
外部 TTS サーバー・AivisSpeech GUI・Python 実行環境は一切不要。

## 2. 検証対象

| 参照先 | 役割 |
| --- | --- |
| AivisSpeech-Engine (master @ clone) | 正式推論パイプライン (`style_bert_vits2_tts_engine.py`) |
| tsukumijima/Style-Bert-VITS2 フォーク (`9fc7f5a`) | ONNX 推論 (`infer_onnx.py`)、G2P (`g2p.py`)、正規化、BERT 特徴抽出 |
| uStyle-Bert-VITS2 (Unity C#) | 移植可能性の参考 (`SBV2ModelRunner`, OpenJTalk native wrapper 使用) |
| aivmlib README | AIVMX ファイルフォーマット仕様 |
| AivisHub コハクページ & API | モデル取得・メタデータ・ライセンス |

## 3. AIVMX フォーマット検証 (コハク実物で確認)

SHA-256 `3f5c08b5…333270a` (Hub 公表値と一致) を実解析。

- AIVMX = **ONNX モデルの metadata_props にメタデータを格納**したもの
  - `aivm_manifest`: JSON (話者・スタイル・ライセンス等、v1.0)
  - `aivm_hyper_parameters`: JSON (SBV2 config.json 相当)
  - `aivm_style_vectors`: **Base64 化された NumPy .npy**
- コハクのハイパーパラメータ検証結果
  - `use_jp_extra: true`, `sampling_rate: 44100`, `spk2id: {コハク: 0}`
  - `style2id: {ノーマル: 0, あまあま: 1, せつなめ: 2, ねむたい: 3}` (4 スタイル →要件どおり)
- スタイルベクトル shape `(4, 256)` float32 → ONNX `style_vec` 入力 (1,256) にそのまま行指定で渡す

## 4. ONNX モデル入出力 (実測)

SynthesizerTrn (AIVMX 本体):

```
入力:
  x_tst            (1, L)          int64  音素ID列
  x_tst_lengths    (1)             int64
  sid              (1)             int64  話者ID (コハク=0)
  tones            (1, L)          int64  アクセント (0/1)
  language         (1, L)          int64  言語ID (JP-Extra は 0)
  bert             (1, 1024, L)    float32 BERT特徴 (DeBERTa v2 Large = 1024 次元)
  style_vec        (1, 256)        float32 スタイルベクトル
  length_scale, sdp_ratio, noise_scale, noise_scale_w  (Float スカラー)
出力:
  output           (1, 1, T) 音声波形 float32 [-1,1] 相当 → int16 へ
```

補助出力6つはデバッグ用、`output` のみ使用。`emo` 入力なし (純 JP-Extra)。

BERT (tsukumijima/deberta-v2-large-japanese-char-wwm-onnx `model_fp16.onnx` 653MB):

```
入力:  input_ids (1, S) int64 / attention_mask (1, S) int64
出力:  output (S, 1024) float32   ※PyTorch版の hidden_states[-3] に相当する層を選択済み
トークナイザー: 文字単位 WordLevel 22,012語彙
  正規化: BertNormalizer(clean_text) + NFKC
  前処理: WhitespaceSplit + 文字Split
  後処理: [CLS] text [SEP]
  max 512 tokens (CLS/SEP 込み → 本文 510 文字に制限)
```

## 5. 公式推論パイプライン (C# 移植仕様として確定)

`infer_onnx.py` + `g2p.py` より:

1. **正規化** `normalize_text`: NFKC → 数字→漢字 (num2words ja) → `~～〜`→`ー` → 句読点マップ置換 (`：。！？\n…・、` 等→ `,.!?…'` 等) → 許可文字以外削除 → 結合濁点削除
2. **g2p** (`use_jp_extra=True`):
   - a) 音素アクセント列: ESPnet 版 `pyopenjtalk_g2p_prosody` (フルコンテキストラベル → `^ $ # _ [ ]` マーカー、cl→q、無声母音は無視)
   - b) 形態素ごと読み列: `run_frontend` → (単語表記, カタカナ読み)、読めない字は `'`
   - c) カタカナ→音素 (モーラ表 mora_list.py)、長音 `ー` は直前母音へ展開
   - d) `__align_tones` で a) のアクセントを c) の音素列へ割当て
   - e) word2ph: 単語の文字数へ音素数を均等配分 (`_` 追加で全長+2)
3. **シンボル→ID**: `SYMBOLS = [PAD] + sorted(中文+日本語+英語記号) + [!, ?, …, ,, ., ', -, SP, UNK]`; tones に `+NUM_JP_TONES(2)`、language=`1`(JP)
4. **`add_blank`**: 0 (`_`) を音素間に挿入、word2ph を ×2 (`word2ph[0] += 1`)
5. **BERT 特徴**: `sep_text` 連結文字列 → トークン化 → DeBERTa ONNX → (S,1024) → 各文字の特徴を word2ph 個 tile → 転置 (1024, L)
6. **ONNX 推論**: 上記テンソル投入。デフォルト `sdp_ratio=0.2, noise=0.6, noisew=0.8, length=1/speedScale, style_weight=1.0` を調整可
7. **後処理**: clip(-1,1)×32768 → int16 (`convert_to_16_bit_wav`)。AivisSpeech は前後無音トリミング+指定無音追加以後 float 化

`pitch_scale≠1` 時の pyworld によるVOICE変換 (adjust_voice) は品質劣化のattriteが明記されており、v1 は未対応 (pitch=1.0 固定)。

## 6. Japanese G2P の C# 実現方式 (決定)

**`DotNetG2P` (Pure C# NuGet, Apache-2.0) + naist-jdic** を採用。

理由:
- OpenJTalk と**同一の naist-jdic 辞書**を用いる MeCab 実装で、6 段階 NJD 処理(発音生成・数字読み・アクセント句結合・アクセント型・無声化)を OpenJTalk 互換として再現
- `Analyze()` → NjdNode 列(表記 Surface / 読み Pronunciation(ToKatakana) / AccentType / ChainFlag)、`ToProsodyFeatures()` → 音素+対応 A1/A2/A3
- ネイティブ依存ゼロ (macOS Apple Silicon / Windows x64 で同条件)

**注意 (実測** `ProsodyExtractor.cs` **)**: DotNetG2P の `ToProsody()` は ESPnet 原典と異なるbitset (`_` をモーラ間区別に大量挿入、頭高型で mora1 前に `[`)。よって **SBV2 の `__g2phone_tone_wo_punct` 相当 Map を自前実装**し、`Analyze()` (NjdNode) を入力に tone 列を構成する方針。検証は Python pyopenjtalk 出力と突き合わせで差分確認 (未完タスク: T-1)。

代替案 (フォールバック): `ayutaz/openjtalk-native` (Windows/macOS 対応の OpenJTalk 共有ライブラリ、フルコンテキストラベル C API) をラップして pyopenjtalk と完全一致させる。辞書も同一。v2 として検討。

## 7. tsqyomi (文脈読み分け) について

AivisSpeech Engine は tsqyomi (ONNX 小物モデル) で文脈依存の読み振り分けを実施 (「65」→ 六十五/ろくじゅうご など)。
**v1 PoC では未導入** (g2p の `use_tsqyomi=False` 相当)。読み精度は将来改善タスク。

## 8. ライセンス整理

| 項目 | ライセンス | 配布可否 |
| --- | --- | --- |
| AivisSpeech-Engine | LGPL-3.0 | 参照のみ・コード持ち込まない ✔ |
| style-bert-vits2 (tsukumijima) | AGPL-3.0 | 参照のみ・アルゴリズム自前実装 ✔ |
| uStyle-Bert-VITS2 | Apache-2.0 | 参照可能 |
| コハク (.aivmx) | ACML 1.0 (manifest 内完全文) | 配布可 (添付必須、禁止事項遵守)。開発環境の実物 metadata で確認済 (2026-10-18) |
| deberta-v2-large-japanese-char-wwm-onnx (tsukumijima) | CC-BY-SA-4.0 (基盤 ku-nlp モデル由来) | **リポジトリ非同梱**。ユーザー取得手順のみ文書化 ✔ (T-2 解決) |
| DotNetG2P | Apache-2.0 | NuGet 同梱 ✔ |
| naist-jdic | BSD (© 2009 NAIST, COPYING 同梱) | **リポジトリ非同梱**。ユーザー取得手順のみ文書化 ✔ (T-3 解決) |
| ONNX Runtime | MIT | ✔ |
| Godot | MIT / Godot ライセンス | ✔ |

本実装は LGPL/AGPL コードをコピーせずアルゴリズム仕様を自前実装する (リンクしない)。

## 9. 未解決事項 / リスク

| ID | 内容 | 対応 |
| --- | --- | --- |
| T-1 | DotNetG2P 経由の phone/tone 列が pyopenjtalk と完全一致するか | **解決** (2026-10-17): compare 16/16 slug PASS。差分吸収は `ToMecabInput`(ASCII→全角 mecab 正規化), `NormalizeNodeForLabels`(ヲ→オ / 句頭長音除去を ChainFlag で判定+F fixDotNetG2PPairs(dy/ty+i)。ラベル尾部 B/C/D フィールドのみ定数差が残存 (モデル入力に影響なし) → `docs/02_g2p_compat.md` |
| T-2 | BERT (char-wwm) の同梱ライセンス条件 | **解決** (2026-10-18): tsukumijima 変換 ONNX / 基盤 ku-nlp モデルとも CC-BY-SA-4.0。モデルはリポジトリに同梱しない方針 → `THIRD-PARTY-LICENSES.md` + README の取得手順で対応 |
| T-3 | naist-jdic 再配布条件 (表記とREADME) | **解決** (2026-10-18): COPYING は BSD (© 2009 NAIST)。リポジトリ同梱をやめユーザー取得 (BSD 注意書きを THIRD-PARTY-LICENSES.md に転載済み) |
| T-4 | 長文 (数千文字) での BERT 512 制限とメモリ | 文分割 (500文字以内) をライブラリ側で実施 |
| T-5 | NumToKanji (num2words ja) の完全移植数値パターン | 基本形実装+不足時に成長させる (numbers/decimal dump は PASS 済) |
| T-6 | Apple Silicon/Windows 以外 (Linux/iOS/Android) | スコープ外だが ONNX Runtime のみ対応可 |
| T-7 | ONNX 内 RandomNormalLike により波形が実行毎に非一致 (長さも±4%) | v1 は仕様として記録。決定的出力が必要なら ONNX ランダム固定/シード注入を将来検討 |
