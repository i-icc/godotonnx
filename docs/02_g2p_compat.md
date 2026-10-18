# G2P 互換検証記録 (pyopenjtalk 0.4.1 との突合)

比較 CLI: `tools/Sbv2Cli compare` — `tools/pythonref/dump/*.json` (pyopenjtalk + SBV2 g2p の実値 dump) と突合。
突合カテゴリ: norm/sep_text/sep_kata/phones/tones/phone_ids/tone_ids/lang_ids/word2ph/bert_text/bert_ids。

## 検証結果

```
全 16 slug PASS (align/dash/decimal/greet/latin/longvowel/newlines/
numbers/punct/question/quotes/sokuon/tenki/twister/voiced/youon)
```

- `phones/tones/word2ph/bert` はモデルに投入される本体なので完全一致が必須 → PASS。
- full context labels は参考情報 (モデルには直接渡さない。tone 導出は A1/A2/A3 + `^ $ # _ [ ]` だけ)。
  label差分の数値は各 slug の label 数と同値 (= 全 label が B/C/D フィールドのみ異なる)。

## 実装の差分吸収ポイント (自前実装 `Sbv2JapaneseG2P`)

### 1. MeCab 入力前の文字正規化 (`ToMecabInput`)

pyopenjtalk (open-jtalk mecab) は解析前に ASCII 文字を全角化する。
naist-jdic のエントリキーは全角 (例: `ＯＫ`→読み`オーケイ`, `ａ`単字) なので、
DotNetG2P の MeCabTokenizer (正規化なし純粋 trie 検索) は
半角のまま渡すと辞書ヒットせず 1字ずつ未知語になってしまう。

実測した open-jtalk mecab の正規化表 (pyopenjtalk 0.4.1 + naist-jdic 1.11):

| 入力 (ASCII) | 出力 | 備考 |
| --- | --- | --- |
| `A`–`Z`, `a`–`z` | 全角 `Ａ`–`Ｚ`, `ａ`–`ｚ` | `OK`→`ＯＫ`(名詞), `hello`→`ｈｅｌｌｏ`(フィラー, 字母読み) |
| `,` | `，`(U+FF0C) | `、`(U+3001) ではない |
| `.` | `．`(U+FF0E) | |
| `!` | `！`, `?` → `？` | |
| `-` | `−`(U+2212) | 全角ハイフン `－`(U+FF0D) **ではない** |
| `'` | `’`(U+2019) | 全角アポストロフィ `＇` **ではない** |
| `;` `:` | `；` `：` | normalize 段階で除去されるので通常入らない |

補足:
- norm_text 自体は半角のまま (`sep_text` では `ReplacePunctuation` で再び `,.!'` に戻る)。
- ギリシャ文字 (`αβγ`) は辞書エントリ (アルファ/ベータ/ガンマ) があり正規化対象外。

### 2. ヲ (`NormalizeNodeForLabels`)

- pyopenjtalk: 発音 `ヲ` → **1 音素 `o`** (助詞を=ヲ含む全ケース)。
- DotNetG2P: ヲ → **2 音素 `w`+`o`**。
- 対処: ラベル生成前のノード発音文字列で全角 `ヲ`→`オ` に置換 (モーラ上の独立した全角ヲのみ対象。
  拗音は小文字 `ォ` (U+30A9) を使うため影響なし)。

### 3. 冒頭長音規則の精緻化

- pyopenjtalk 警示 *"First mora should not be long vowel symbol"* は**アクセント句の先頭** mora が
  長音のときにのみ発生・除去する。
- **ChainFlag == true の結合ノード** (前のアクセント句に続く語) は除かない。
  例: `会いましょう.` の「う」→ NJD 発音 `ー` (特殊助動詞として結合) は
  直前の母音 (`o`) に置換されて **1 label `o`** になる (jpcommon の長音解決)。
  → 実測: pyopenjtalk 9 label vs (修正前の我々) 8 label。
- 修正後: 冒頭長音の除去条件を `node.ChainFlag != true` に変更し済み。
  `ーー` 単独 (句頭だけの長音語) は依然 0 モーラ (`No phoneme`) を踏襲。
- mid-word の連続長音 (`カーー` → `a` 母音繰返し, `ーーソーナノカーー` で確認済) は
  DotNetG2P の JPCommon 長音解決が pyopenjtalk 同値。

### 4. ディ / ティ (ラベル側音素マップ差分 `FixDotNetG2PPairs`)

DotNetG2P `Pronunciation.MoraPhonemeMap` の差異 (実測):

| モーラ | DotNetG2P | pyopenjtalk (SBV2 mora_list) |
| --- | --- | --- |
| ディ (Dhi) | `dy`+`i` | `d`+`i` |
| ティ (Thi) | `ty`+`i` | `t`+`i` |
| ヂ (Di) | `j`+`i` | `j`+`i` (一致) |
| ヅ (Du) | `z`+`u` | `z`+`u` (一致) |
| デェ | `d`,`e`,`e` (デ+ェ) | `d`,`e`,`e` (一致) |
| ウォ | `w`+`o` | `w`+`o` (一致) |
| クォ | `k`,`u`,`o` | `k`,`u`,`o` (一致) |
| ヴォ | `v`+`o` | `v`+`o` (一致; NJD が ヴ→ボ に変換するときは b) |

→ 修正: ラベル側の phoneTone 列で `dy`+`i`→`d`+`i`, `ty`+`i`→`t`+`i` のみ置換。
pyopenjtalk の `dy`/`ty` は 母音 a/u/o (デャ/デュ/デョ, テャ/テュ/テョ) でしか現れないため、`i` 直前のみの置換は安全。

## ラベルの B/C/D フィールド差 (参考情報, 未着手)

pyopenjtalk の B/C/D (単語・句・文の通し番号系) vs DotNetG2P:
DotNetG2P は B2/B3/C1/C2/C3/D1/D2/D3 に**固定の定数**を入れるようで、
pyopenjtalk の実カウンタと異なる (例: `B:32-33_25` vs `B:20-1_1`)。
p1–p5 音素部と A/E/F/G/H/I/J/K は一致済 (会いましょう. で確認)。
tone 導出 (A1/A2/A3 + マーカー) には影響しないため放置。将来 DotNetG2P 側の対応待ち。

## ONNX 合成側の修正 (synth 実動作確認)

- `Sbv2Synthesizer.Run`: `x_tst_lengths`/`sid` を rank-0 ではなく **rank-1 (`[1]`)** で投入 (ORT エラー修正)。
- `WaveWriter`: `blockAlign` を int 型 (4バイト書込) で出力していたため
  fmt チャンク実寸 18B ≠ 宣言 16B → bitsPerSample が bits=0 に読まれ (bad sample width)、
  全パーサが WAV を拒否していた。**const short** で書込サイズ修正。
- 合成結果: 4 スタイル (ノーマル/あまあま/せつなめ/ねむたい) いずれも
  0.5–2.6s で合成完了, 16bit/mono/44.1kHz WAV, peak ~0.78 FS。
  スタイル間で波形 hash と RMS 包絡が変化している事を確認。
- `RandomNormalLike` がモデル内にあるため**実行毎に波形非一致** (同一テキスト・同一スタイルでも
  サンプル長も変動: 例 148,992–153,600 samples @44100Hz)。決定的出力は要 ONNX ランダム固定
  (将来タスク。RTP ゲーム用途では通常不要)。
- **句読点のみの入力** (`、、。` 等): 正規化後に発音可能文字がない場合、
  参照実装は `__fix_phone_tone` で `ValueError` になるが、ライブラリは
  **無音 0.25 秒**を返す (`HasSpeechCharacters` ガード)。
  なお「ー」単独など音素 0 モーラの入力は参照同様に例外 (参照互換)。

## CLI ツール (`tools/Sbv2Cli`)

- `g2p-debug`: ノード/ラベル/韻律/woPunct/wPunct/aligned を表示。
  本体 G2p と同一経路 (`NormalizeNodeForLabels` + `FixDotNetG2PPairs`) を経由するよう修正済。
- `compare [slug...]`: dump と突合。省略時は全 slug。
- `synth`: `--model/--bert/--tok/--jdic/--style(--style-id)/--weight/--speed/--sdp/--noise/--noise-w/--sid/--out`。
  style 名 (`ノーマル`,`あまあま`,`せつなめ`,`ねむたい`) 指定も可。
