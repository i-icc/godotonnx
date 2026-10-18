using System;
using System.IO;
using System.Text.Json.Serialization;

namespace GodotOnnx.Model
{
    /// <summary>AIVMX メタデータ用の JSON シリアライズコンテキスト。</summary>
    [JsonSerializable(typeof(HyperParameters))]
    [JsonSerializable(typeof(AivmManifest))]
    internal sealed partial class HyperParametersContext : JsonSerializerContext
    {
    }
}
