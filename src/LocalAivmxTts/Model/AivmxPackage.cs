using System;
using System.Collections.Generic;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LocalAivmxTts.Model
{
    /// <summary>
    /// AIVM マニフェスト（<c>aivm_manifest</c> メタデータ）のうち必要な部分。
    /// </summary>
    public sealed record AivmManifest(
        [property: JsonPropertyName("manifest_version")] string? ManifestVersion,
        [property: JsonPropertyName("uuid")] string? Uuid,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("description")] string? Description,
        [property: JsonPropertyName("license")] string? License,
        [property: JsonPropertyName("creators")] string[]? Creators,
        [property: JsonPropertyName("speakers")] AivmSpeaker[]? Speakers)
    {
    }

    /// <summary>マニフェストの話者情報。</summary>
    public sealed record AivmSpeaker(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("uuid")] string? Uuid,
        [property: JsonPropertyName("local_id")] int LocalId,
        [property: JsonPropertyName("styles")] AivmStyle[]? Styles)
    {
    }

    /// <summary>マニフェストのスタイル情報。</summary>
    public sealed record AivmStyle(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("local_id")] int LocalId,
        [property: JsonPropertyName("id")] int? Id)
    {
    }

    /// <summary>AIVMX パッケージの読み込み結果。</summary>
    public sealed record AivmxPackage(
        AivmManifest Manifest,
        HyperParameters HyperParameters,
        float[,] StyleVectors)
    {
        /// <summary>style_vectors の行数（＝モデルが定義するスタイル数）。</summary>
        public int NumStyles => StyleVectors.GetLength(0);

        /// <summary>style_vector の次元数。</summary>
        public int StyleVectorDim => StyleVectors.GetLength(1);
    }

    /// <summary>
    /// <c>.aivmx</c> ファイル（ONNX ModelProto + AIVMX メタデータ）を読み込む。
    /// </summary>
    /// <remarks>
    /// ONNX ModelProto の <c>metadata_props</c> (field 14, StringStringEntryProto)
    /// のみをストリーミングに探索する。255MB 級のモデルファイルを全文パースせずに
    /// メタデータだけを取り出すため、GraphProto (field 7) の内容は読み飛ばす。
    /// </remarks>
    public static class AivmxLoader
    {
        static readonly byte[] NpyMagic = { 0x93, 0x4E, 0x55, 0x4D, 0x50, 0x59 }; // "\x93NUMPY"

        /// <summary>
        /// AIVMX ファイルからメタデータを読み込む。
        /// </summary>
        /// <param name="path">.aivmx ファイルパス</param>
        public static AivmxPackage Load(string path)
        {
            using var fs = File.OpenRead(path);
            var metadata = ReadMetadataProps(fs);

            if (!metadata.TryGetValue("aivm_hyper_parameters", out var hpsJson))
            {
                throw new InvalidDataException("aivm_hyper_parameters メタデータが存在しません (AIVMX 形式ではない?)");
            }
            if (!metadata.TryGetValue("aivm_style_vectors", out var styleVecB64))
            {
                throw new InvalidDataException("aivm_style_vectors メタデータが存在しません");
            }

            var hps = JsonSerializer.Deserialize(hpsJson, HyperParametersContext.Default.HyperParameters)
                ?? throw new InvalidDataException("aivm_hyper_parameters の JSON パースに失敗しました");

            AivmManifest? manifest = null;
            if (metadata.TryGetValue("aivm_manifest", out var manifestJson))
            {
                manifest = JsonSerializer.Deserialize(manifestJson, HyperParametersContext.Default.AivmManifest);
            }

            var styleVectors = ParseStyleVectors(styleVecB64, hps.Data.NumStyles ?? 4);

            return new AivmxPackage(manifest ?? new AivmManifest(null, null, hps.ModelName ?? "", null, null, null, null), hps, styleVectors);
        }

        /// <summary>StyleVectors の行（スタイル ID → ベクトルの getter）。</summary>
        public static float[] GetStyleVectorLine(AivmxPackage package, int styleId)
        {
            if (styleId < 0 || styleId >= package.NumStyles)
            {
                throw new ArgumentOutOfRangeException(nameof(styleId), $"style_id {styleId} は 0..{package.NumStyles - 1} の範囲で指定してください");
            }
            return package.StyleVectors.GetStyleVectorLine(styleId);
        }

        /// <summary>二次元配列から 1 行取り出すヘルパー。</summary>
        public static float[] GetStyleVectorLine(this float[,] matrix, int row)
        {
            var dim = matrix.GetLength(1);
            var line = new float[dim];
            for (var d = 0; d < dim; d++)
            {
                line[d] = matrix[row, d];
            }
            return line;
        }

        /// <summary>
        /// ONNX ModelProto のルートレベルを走査し <c>metadata_props</c> (field 14) を取り出す。
        /// </summary>
        static Dictionary<string, string> ReadMetadataProps(Stream stream)
        {
            var result = new Dictionary<string, string>();

            // ルートの field 違い wiretype:
            //   1: ir_version (varint), 2: producer_name, 3: producer_version,
            //   4: domain, 5: model_version (varint), 6: doc_string,
            //   7: graph (len-delim), 8: opset_import (len-delim), 14: metadata_props (len-delim)
            while (ReadVarint(stream) is { } tagRaw)
            {
                var wireType = (int)(tagRaw & 0x7);
                var field = (int)(tagRaw >> 3);

                switch (wireType)
                {
                    case 0: // varint
                        ReadVarint(stream);
                        break;

                    case 1: // 64-bit
                        Skip(stream, 8);
                        break;

                    case 2: // length-delimited
                        {
                            var length = (long)ReadVarint(stream)!.Value;
                            if (field == 14)
                            {
                                var entry = ParseStringStringEntry(stream, length);
                                result[entry.Key] = entry.Value;
                            }
                            else
                            {
                                // graph 等の巨大 field は読み飛ばす
                                Skip(stream, length);
                            }
                            break;
                        }

                    case 3: // start group
                    case 4: // end group
                        throw new InvalidDataException($"Unexpected group wiretype at field {field} (ONNX モデルの破損?)");

                    case 5: // 32-bit
                        Skip(stream, 4);
                        break;

                    default:
                        throw new InvalidDataException($"Invalid wiretype {wireType} (ONNX モデルの破損?)");
                }
            }

            return result;
        }

        /// <summary>StringStringEntryProto { key=1, value=2 } のパース。</summary>
        static (string Key, string Value) ParseStringStringEntry(Stream stream, long length)
        {
            var end = stream.Position + length;
            string? key = null;
            string? value = null;

            while (stream.Position < end)
            {
                var tag = ReadVarint(stream)!.Value;
                var wireType = (int)(tag & 0x7);
                var field = (int)(tag >> 3);
                if (wireType != 2)
                {
                    throw new InvalidDataException($"Unexpected wiretype {wireType} in StringStringEntryProto");
                }
                var len = (long)ReadVarint(stream)!.Value;
                var bytes = new byte[len];
                stream.ReadExactly(bytes);

                if (field == 1)
                {
                    key = Encoding.UTF8.GetString(bytes);
                }
                else if (field == 2)
                {
                    value = Encoding.UTF8.GetString(bytes);
                }
                // 未知 field は無視
            }

            if (key is null || value is null)
            {
                throw new InvalidDataException("metadata_props エントリに key/value が揃っていません");
            }
            return (key, value);
        }

        static ulong? ReadVarint(Stream stream)
        {
            ulong value = 0;
            var shift = 0;
            while (true)
            {
                var b = stream.ReadByte();
                if (b < 0)
                {
                    return shift == 0 ? null : value;
                }
                value |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0)
                {
                    return value;
                }
                shift += 7;
                if (shift > 63)
                {
                    throw new InvalidDataException("varint が長すぎます (ファイルの破損?)");
                }
            }
        }

        static void Skip(Stream stream, long length)
        {
            if (length > 64 * 1024 * 1024)
            {
                // 巨大な field (graph 本体) は Seek で飛ばす
                stream.Seek(length, SeekOrigin.Current);
            }
            else
            {
                stream.Seek(length, SeekOrigin.Current);
            }
        }

        /// <summary>
        /// <c>aivm_style_vectors</c> (Base64 化 .npy ファイル) を float 型の二次元配列 [(num_styles, dim)] にデコードする。
        /// </summary>
        public static float[,] ParseStyleVectors(string base64, int expectedRows)
        {
            var raw = Convert.FromBase64String(base64);
            return ParseNpy(raw, expectedRows);
        }

        /// <summary>.npy 形式 (float32) の本体をパースする。</summary>
        public static float[,] ParseNpy(byte[] raw, int expectedRows)
        {
            if (raw.Length < 6 + NpyMagic.Length)
            {
                throw new InvalidDataException(".npy ヘッダが破損しています");
            }

            if (raw[0] != NpyMagic[0]) throw new InvalidDataException(".npy magic が不正です");

            var major = raw[6];
            var minor = raw[7];

            int headerLen;
            if (major <= 1)
            {
                headerLen = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(8, 2));
            }
            else
            {
                headerLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(8, 4));
            }
            var headerOffset = 10 + (major <= 1 ? 0 : 2);
            var dataOffset = headerOffset + headerLen;

            var header = Encoding.ASCII.GetString(raw, headerOffset, headerLen);
            var descr = ExtractStringJsonValue(header, "'descr'");
            if (descr is not ("<f4" or "float32"))
            {
                throw new InvalidDataException($"style_vectors の dtype ({descr}) は float32 のみ対応しています");
            }

            // 'shape': (rows, dim) の tuple を専用パースで取り出す
            var shapeIdx = header.IndexOf("'shape'");
            if (shapeIdx < 0)
            {
                throw new InvalidDataException(".npy ヘッダに shape がありません");
            }
            var parenStart = header.IndexOf('(', shapeIdx);
            var parenEnd = header.IndexOf(')', shapeIdx);
            if (parenStart < 0 || parenEnd < 0)
            {
                throw new InvalidDataException($".npy shape が不正です: {header[shapeIdx..]}");
            }
            var dims = header.Substring(parenStart + 1, parenEnd - parenStart - 1).Trim();
            var parts = dims.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length != 2)
            {
                throw new InvalidDataException($"style_vectors は二次元配列である必要がありますが shape=({dims}) です");
            }
            var rows = int.Parse(parts[0]);
            var dim = int.Parse(parts[1]);

            if (rows != expectedRows)
            {
                throw new InvalidDataException($"style_vectors の行数 ({rows}) と num_styles ({expectedRows}) が不一致です");
            }

            var result = new float[rows, dim];
            var data = new Span<byte>(raw, dataOffset, rows * dim * 4);
            for (var r = 0; r < rows; r++)
            {
                for (var d = 0; d < dim; d++)
                {
                    var idx = (r * dim + d) * 4;
                    result[r, d] = BinaryPrimitives.ReadSingleLittleEndian(data.Slice(idx, 4));
                }
            }

            return result;
        }

        /// <summary>Python dict 風ヘッダ文字列からシングルクオート key の値を粗く取り出す。</summary>
        static string? ExtractStringJsonValue(string header, string keyLiteral)
        {
            var idx = header.IndexOf(keyLiteral, StringComparison.Ordinal);
            if (idx < 0)
            {
                return null;
            }
            // '(  descr': '<f4'
            var colon = header.IndexOf(':', idx + keyLiteral.Length);
            if (colon < 0)
            {
                return null;
            }
            var quoteStart = header.IndexOf('\'', colon);
            var quoteEnd = header.IndexOf('\'', quoteStart + 1);
            if (quoteStart < 0 || quoteEnd < 0)
            {
                return null;
            }
            return header.Substring(quoteStart + 1, quoteEnd - quoteStart - 1);
        }
    }
}
