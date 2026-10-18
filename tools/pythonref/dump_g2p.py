# Style-Bert-VITS2 (tsukumijima フォーク) の実装を参照実装として直接 import し、
# C# 実装 (LocalAivmxTts) の検証用リファレンス出力を JSON にダンプする研究用スクリプト。
# 解析専用: この環境は検証にのみ使用する
import base64
import json
import sys
import types
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
SBV2_DIR = REPO_ROOT / "research" / "sbv2"
BERT_DIR = REPO_ROOT / "research" / "bert"
OUT_DIR = Path(__file__).resolve().parent / "dump"

# style_bert_vits2.nlp.japanese.g2p の import 時に transformers / huggingface_hub の
# モジュールレベル import が走るため、重い本物の import を避けるスタブを先に差し込む
if "transformers" not in sys.modules:
    _stub = types.ModuleType("transformers")
    _stub.__getattr__ = lambda name: type(name, (), {})  # どんな属性もダミークラスで返す
    sys.modules["transformers"] = _stub
if "huggingface_hub" not in sys.modules:
    _stub2 = types.ModuleType("huggingface_hub")
    _stub2.__getattr__ = lambda name: type(name, (), {})
    sys.modules["huggingface_hub"] = _stub2

sys.path.insert(0, str(SBV2_DIR))

from style_bert_vits2.nlp import cleaned_text_to_sequence  # noqa: E402
from style_bert_vits2.nlp.japanese.bert_feature import extract_bert_feature_onnx  # noqa: E402
from style_bert_vits2.nlp.japanese.g2p import g2p, text_to_sep_kata  # noqa: E402
from style_bert_vits2.nlp.japanese.normalizer import normalize_text  # noqa: E402

# g2p() 内の sep_tokenized 計算で使われるHFトークナイザーを、
# 同一リポジトリ同梱の tokenizer.json をそのまま読む tokenizers (Rust) で置き換える
from tokenizers import Tokenizer as FastTokenizer  # noqa: E402

from style_bert_vits2 import nlp as nlp_pkg  # noqa: E402

REAL_TOKENIZER = FastTokenizer.from_file(str(BERT_DIR / "tokenizer.json"))


class _FastTok:
    @staticmethod
    def tokenize(word: str) -> list[str]:
        return REAL_TOKENIZER.encode(word).tokens


nlp_pkg.bert_models.load_tokenizer = lambda language: _FastTok

import pyopenjtalk  # noqa: E402


def dump_one(text: str, slug: str) -> None:
    norm = normalize_text(text)
    sep_text, sep_kata = text_to_sep_kata(norm, raise_yomi_error=False)
    phones, tones, word2ph = g2p(norm, use_jp_extra=True, raise_yomi_error=False)
    ids, tone_ids, lang_ids = cleaned_text_to_sequence(phones, tones, "JP")
    labels = pyopenjtalk.make_label(pyopenjtalk.run_frontend(norm))
    bert_text = "".join(sep_text)
    enc = REAL_TOKENIZER.encode(bert_text)
    bert_out = {
        "bert_text": bert_text,
        "input_ids": enc.ids,
        "tokens": enc.tokens,
    }
    payload = {
        "text": text,
        "norm_text": norm,
        "sep_text": sep_text,
        "sep_kata": sep_kata,
        "phones": phones,
        "tones": tones,
        "word2ph": word2ph,
        "phone_ids": ids,
        "tone_ids": tone_ids,
        "lang_ids": lang_ids,
        "labels": labels,
        "bert": bert_out,
    }
    OUT_DIR.mkdir(exist_ok=True)
    (OUT_DIR / f"{slug}.json").write_text(
        json.dumps(payload, ensure_ascii=False, indent=1), encoding="utf-8"
    )


SENTENCES = [
    ("greet", "こんにちは、音声合成の世界へようこそ！"),
    ("tenki", "今日はいい天気ですね。"),
    ("longvowel", "ラーメン、コーヒー、一年生！"),
    ("sokuon", "ちょっと、お兄さん、こっち見て。"),
    ("youon", "ヴォイス、フィルム、ディレクター、キュート！"),
    ("align", "……私は、、そう思う。"),
    ("dash", "ーーソーナノカーー"),
    ("numbers", "1000匹のネコが11人で2250円の魚を買った。10時30分に会いましょう。"),
    ("decimal", "平均は10.5倍で、1932年に発表された。"),
    ("latin", "OKです。日本語とEnglishとαβγのテスト。"),
    ("punct", "??あ、、！！！なるほど...."),
    ("quotes", "'引用符'と-ハイフン、ダッシューは長音"),
    ("twister", "赤巻紙青巻紙黄巻紙。あのバスガス爆発事故。"),
    ("question", "初めまして、ハルカと申します。あなたのお名前は？"),
    ("newlines", "日本語テキストの\n改行を含むテスト。最後です"),
    ("voiced", "常連さんはヴァイオリンとドゥームをリクエストした。"),
]

if __name__ == "__main__":
    only = sys.argv[1:] if len(sys.argv) > 1 else None
    for slug, text in SENTENCES:
        if only and slug not in only:
            continue
        dump_one(text, slug)
        print(f"dumped {slug}: {text[:20]}")
