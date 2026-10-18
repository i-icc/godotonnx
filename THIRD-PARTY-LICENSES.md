# Third-party licenses and notices

This repository ships **only source code written for this project** (MIT — see [`LICENSE`](LICENSE)).
All neural models, dictionaries, and other data must be **obtained separately** by the user, as described in the [README](README.md#models-you-need). Nothing below is bundled, embedded, or redistributed in this repository.

| # | Component | License | How this project uses it |
|---|-----------|---------|--------------------------|
| 1 | Microsoft.ML.OnnxRuntime (NuGet) | MIT | Linked dependency — runs all ONNX inference |
| 2 | DotNetG2P / DotNetG2P.MeCab (NuGet) | Apache-2.0 | Linked dependency — Japanese G2P (phoneme + accent) |
| 3 | NAIST Japanese Dictionary (naist-jdic) | BSD-style (© 2009 Nara Institute of Science and Technology) | Runtime data — user downloads; **not** distributed here |
| 4 | [tsukumijima/deberta-v2-large-japanese-char-wwm-onnx](https://huggingface.co/tsukumijima/deberta-v2-large-japanese-char-wwm-onnx) (FP16 BERT) | CC-BY-SA-4.0 (base: [ku-nlp/deberta-v2-large-japanese-char-wwm](https://huggingface.co/ku-nlp/deberta-v2-large-japanese-char-wwm)) | Runtime data — user downloads; **not** distributed here |
| 5 | コハク AIVMX voice model | **ACML 1.0** (embedded in the model metadata) | Runtime data — user downloads; **not** distributed here |
| 6 | Style-Bert-VITS2 | AGPL-3.0 | **Reference only.** Algorithms were re-implemented independently in C#; no source code was copied |
| 7 | AivisSpeech / AivisSpeech-Engine | LGPL-3.0 | **Reference only** (AIVM/AIVMX format, inference pipeline shape); no source code was copied |
| 8 | Godot Engine | MIT | Demo application host |

---

## 1. Microsoft.ML.OnnxRuntime

- Source: <https://github.com/microsoft/onnxruntime>
- License: MIT — <https://github.com/microsoft/onnxruntime/blob/main/LICENSE>
- Used as a NuGet package; its license text is included in the package itself.

## 2. DotNetG2P / DotNetG2P.MeCab

- Source: <https://github.com/ayutaz/dot-net-g2p> (see its [NOTICE](https://github.com/ayutaz/dot-net-g2p/blob/main/NOTICE) file for the upstream notices included in the dictionary data)
- License: Apache-2.0 — <https://github.com/ayutaz/dot-net-g2p/blob/main/LICENSE>
- Used as NuGet packages. The Apache-2.0 text is included in the packages themselves.

## 3. NAIST Japanese Dictionary

- Source: <https://sourceforge.net/projects/naist-jdic/> (NAIST: <https://alaginrc.nacl.nii.ac.jp/dic_viewtop_en.html>) — compiled dictionaries are also built from these sources by MeCab ecosystem projects
- License (verbatim notice, as required by the BSD terms):

  > Copyright (c) 2009, Nara Institute of Science and Technology, Japan.
  > All rights reserved.
  >
  > Redistribution and use in source and binary forms, with or without modification, are permitted provided that the following conditions are met:
  > Redistributions of source code must retain the above copyright notice, this list of conditions and the following disclaimer. Redistributions in binary form must reproduce the above copyright notice, this list of conditions and the following disclaimer in the documentation and/or other materials provided with the distribution. Neither the name of the Nara Institute of Science and Technology (NAIST) nor the names of its contributors may be used to endorse or promote products derived from this software without specific prior written permission.
  >
  > THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

## 4. BERT (DeBERTa v2, Japanese, char-level, FP16 ONNX)

- Source: <https://huggingface.co/tsukumijima/deberta-v2-large-japanese-char-wwm-onnx> (`model_fp16.onnx`, `tokenizer.json`)
- Base model: [ku-nlp/deberta-v2-large-japanese-char-wwm](https://huggingface.co/ku-nlp/deberta-v2-large-japanese-char-wwm), License: **CC-BY-SA-4.0**
- If you redistribute the converted weights, follow CC-BY-SA-4.0 (attribution + share-alike for the weights). This project does not redistribute them.

## 5. Voice model "コハク" (AIVMX)

- Model UUID: `22e8ed77-94fe-4ef2-871f-a86f94e9a579`, architecture: Style-Bert-VITS2 (JP-Extra)
- Creators: オズチャット -Oz Chat- / Trippy, voice: ねゆたろ
- License: **ACML 1.0** (Aivis Common Model License) — <https://github.com/Aivis-Project/ACML/blob/master/ACML-1.0.md>
  - The **full license text is embedded in the model file itself** (ONNX `metadata_props` key `aivm_manifest` → `license`).
  - Free use, commercial use, modification and redistribution of the *model* are all allowed, with prohibited-use clauses (impersonation, defamation, political/religious activity, fraud, etc.).
  - **If you redistribute the model (or an app bundling it), you must attach the ACML text together with it.**
  - Credit (optional but requested by the creator): **`AivisSpeech: コハク`**

## 6–7. Reference-only projects

- Style-Bert-VITS2 — <https://github.com/tsukumijima/Style-Bert-VITS2> (AGPL-3.0)
- AivisSpeech — <https://github.com/Aivis-Project/AivisSpeech> (LGPL-3.0)
- AivisSpeech-Engine — <https://github.com/Aivis-Project/AivisSpeech-Engine> (LGPL-3.0; VOICEVOX-derived modules are LGPL-3.0)

These were used as *documents to read* while studying the pipeline (input normalization rules, phoneme/accent conventions, `x_tst`/`bert`/`style_vec` inference inputs). This repository contains **no source code copied from them**; all C# code here was written for this project. (Not legal advice — if you plan a product release, confirm your own interpretation.)

## 8. Godot Engine

- Source: <https://github.com/godotengine/godot> — License: MIT. The demo under `godot/` uses the Godot editor/runtime but contains nothing that affects the licensing of this repository's code.
