using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace GodotOnnx.Model
{
    /// <summary>
    /// AIVMX の <c>aivm_hyper_parameters</c> メタデータのうち推論に必要な部分
    /// （Style-Bert-VITS2 <c>HyperParameters</c> のサブセット）。
    /// </summary>
    public sealed record HyperParameters(
        [property: JsonPropertyName("model_name")] string? ModelName,
        [property: JsonPropertyName("version")] string Version,
        [property: JsonPropertyName("data")] HyperParametersData Data)
    {
        /// <summary>モデルが JP-Extra で学習されているか。</summary>
        [JsonIgnore]
        public bool UseJpExtra => Version is not null && Version.EndsWith("JP-Extra", StringComparison.Ordinal);

        /// <summary>サンプリングレート (Hz)。</summary>
        [JsonIgnore]
        public int SamplingRate => Data.SamplingRate;

        /// <summary>音素列の <c>_</c> intersperse 有効 (data.add_blank)。</summary>
        [JsonIgnore]
        public bool AddBlank => Data.AddBlank;

        /// <summary>スタイル ID の辞書 (スタイル名 → ID)。</summary>
        [JsonIgnore]
        public IReadOnlyDictionary<string, int> Style2Id => Data.Style2Id;
    }

    /// <summary>ハイパーパラメータの <c>data</c> セクション（推論に必要な部分のみ）。</summary>
    public sealed record HyperParametersData(
        [property: JsonPropertyName("use_jp_extra")] bool UseJpExtraField,
        [property: JsonPropertyName("sampling_rate")] int SamplingRate,
        [property: JsonPropertyName("add_blank")] bool AddBlank,
        [property: JsonPropertyName("n_speakers")] int? NSpeakers,
        [property: JsonPropertyName("spk2id")] IReadOnlyDictionary<string, int> Spk2Id,
        [property: JsonPropertyName("num_styles")] int? NumStyles,
        [property: JsonPropertyName("style2id")] IReadOnlyDictionary<string, int> Style2Id)
    {
    }
}
