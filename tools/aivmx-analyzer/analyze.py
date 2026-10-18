"""AIVMX ファイルのメタデータと ONNX 入出力を分析する開発用スクリプト"""
import base64, json, sys, io
import numpy as np
import onnx

path = sys.argv[1]
model = onnx.load(path, load_external_data=False)
m = {e.key: e.value for e in model.metadata_props}
print("=== metadata keys ===")
for k in m:
    print(" -", k)

for key in ("aivm_hyper_parameters", "aivm_manifest"):
    if key in m:
        obj = json.loads(m[key])
        slim = {k: v for k, v in obj.items() if k != "speakers"} if key=="aivm_manifest" else obj
        if key == "aivm_manifest":
            spk = obj.get("speakers", [])
            slim["speaker_count"] = len(spk)
            if spk:
                s0 = spk[0]
                slim["speaker0"] = {k: (("<len%d>" % len(v)) if isinstance(v, str) and len(v) > 60 else v) for k, v in s0.items() if k != "icon"}
                slim["speaker0_styles"] = [{k: v for k, v in st.items() if k != "icon"} for st in s0.get("styles", [])]
        print(f"=== {key} ===")
        print(json.dumps(slim, ensure_ascii=False, indent=1)[:4000])

if "aivm_style_vectors" in m:
    raw = base64.b64decode(m["aivm_style_vectors"])
    arr = np.load(io.BytesIO(raw))
    print("=== aivm_style_vectors ===")
    print("shape:", arr.shape, "dtype:", arr.dtype)
    print("row0:", arr[0][:8])

print("=== ONNX inputs ===")
for i in model.graph.input:
    dims = [d.dim_param if d.dim_param else d.dim_value for d in i.type.tensor_type.shape.dim]
    print(f"  {i.name}: {dims}")
print("=== ONNX outputs ===")
for o in model.graph.output:
    dims = [d.dim_param if d.dim_param else d.dim_value for d in o.type.tensor_type.shape.dim]
    print(f"  {o.name}: {dims}")
