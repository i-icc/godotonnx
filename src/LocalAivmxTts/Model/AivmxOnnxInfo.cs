using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace LocalAivmxTts.Model
{
    /// <summary>graph 入力（または出力）の情報。</summary>
    /// <param name="Name">入力名</param>
    /// <param name="ElemType">TensorProto.DataType (1=float, 7=int64, 10=float16)</param>
    /// <param name="Dims">shape 次元 (-1 は unknown。空はスカラー)</param>
    public sealed record OnnxValueInfo(string Name, int ElemType, IReadOnlyList<long> Dims)
    {
        public override string ToString()
        {
            var d = string.Join(",", Dims);
            var t = ElemType switch
            {
                1 => "float32",
                7 => "int64",
                10 => "float16",
                _ => $"type{ElemType}",
            };
            return $"{Name}: {t}[{d}]";
        }
    }

    /// <summary>
    /// ONNX ModelProto をストリーミング走査して graph 入力・出力の情報を取り出す
    /// （AIVMX 本体を InferenceSession に読み込まずに IO 仕様を確認するための補助）。
    /// </summary>
    public static class AivmxOnnxInfo
    {
        const int FieldGraph = 7;
        const int FieldGraphInput = 11;
        const int FieldGraphOutput = 12;

        /// <summary>.aivmx / .onnx ファイルの graph 入力を（グラフ内の並び順で）取得する。</summary>
        public static IReadOnlyList<OnnxValueInfo> GetInputs(string path) => Scan(path, FieldGraphInput);

        /// <summary>.aivmx / .onnx ファイルの graph 出力を取得する。</summary>
        public static IReadOnlyList<OnnxValueInfo> GetOutputs(string path) => Scan(path, FieldGraphOutput);

        static IReadOnlyList<OnnxValueInfo> Scan(string path, int targetField)
        {
            using var fs = File.OpenRead(path);
            var graphRange = FindGraph(fs);
            if (graphRange is null)
            {
                throw new InvalidDataException("graph (field 7) が見つかりません");
            }

            var (start, length) = graphRange.Value;
            fs.Seek(start, SeekOrigin.Begin);
            return CollectValueInfos(fs, length, targetField);
        }

        /// <summary>ModelProto から graph (field 7) のデータ領域を探す。</summary>
        static (long Start, long Length)? FindGraph(Stream stream)
        {
            while (ReadVarint(stream) is { } tagRaw)
            {
                var wireType = (int)(tagRaw & 0x7);
                var field = (int)(tagRaw >> 3);
                switch (wireType)
                {
                    case 0:
                        ReadVarint(stream);
                        break;
                    case 1:
                        stream.Seek(8, SeekOrigin.Current);
                        break;
                    case 2:
                        var length = (long)ReadVarint(stream)!.Value;
                        if (field == FieldGraph)
                        {
                            return (stream.Position, length);
                        }
                        stream.Seek(length, SeekOrigin.Current);
                        break;
                    case 5:
                        stream.Seek(4, SeekOrigin.Current);
                        break;
                    default:
                        throw new InvalidDataException($"Invalid wiretype {wireType}");
                }
            }
            return null;
        }

        /// <summary>GraphProto 内で field targetField (input/output) の ValueInfoProto を順に収集する。</summary>
        static List<OnnxValueInfo> CollectValueInfos(Stream stream, long graphLength, int targetField)
        {
            var result = new List<OnnxValueInfo>();
            var end = stream.Position + graphLength;
            while (stream.Position < end)
            {
                var tagRaw = ReadVarint(stream)!.Value;
                var wireType = (int)(tagRaw & 0x7);
                var field = (int)(tagRaw >> 3);
                switch (wireType)
                {
                    case 0:
                        ReadVarint(stream);
                        break;
                    case 1:
                        stream.Seek(8, SeekOrigin.Current);
                        break;
                    case 2:
                        var length = (long)ReadVarint(stream)!.Value;
                        if (field == targetField)
                        {
                            result.Add(ParseValueInfo(stream, length));
                        }
                        else if (field == targetField + 1 || field == targetField + 2)
                        {
                            // 対象外の field（output/value_info など）は読み飛ばす
                            stream.Seek(length, SeekOrigin.Current);
                        }
                        else
                        {
                            stream.Seek(length, SeekOrigin.Current);
                        }
                        break;
                    case 5:
                        stream.Seek(4, SeekOrigin.Current);
                        break;
                    default:
                        throw new InvalidDataException($"Invalid wiretype {wireType}");
                }
            }
            return result;
        }

        /// <summary>ValueInfoProto { name=1, type=2 } をパースする。</summary>
        static OnnxValueInfo ParseValueInfo(Stream stream, long length)
        {
            var end = stream.Position + length;
            string? name = null;
            int elemType = 0;
            var dims = new List<long>();

            while (stream.Position < end)
            {
                var tagRaw = ReadVarint(stream)!.Value;
                var wireType = (int)(tagRaw & 0x7);
                var field = (int)(tagRaw >> 3);
                switch (wireType)
                {
                    case 2:
                        var len = (long)ReadVarint(stream)!.Value;
                        if (field == 1)
                        {
                            name = ReadString(stream, len);
                        }
                        else if (field == 2)
                        {
                            // TypeProto
                            ParseType(stream, len, ref elemType, dims);
                        }
                        else
                        {
                            stream.Seek(len, SeekOrigin.Current);
                        }
                        break;
                    default:
                        throw new InvalidDataException($"ValueInfoProto: unexpected wiretype {wireType}");
                }
            }

            return new OnnxValueInfo(name ?? "?", elemType, dims);
        }

        /// <summary>TypeProto { tensor_type=1 } をパースする。</summary>
        static void ParseType(Stream stream, long length, ref int elemType, List<long> dims)
        {
            var end = stream.Position + length;
            while (stream.Position < end)
            {
                var tagRaw = ReadVarint(stream)!.Value;
                var wireType = (int)(tagRaw & 0x7);
                var field = (int)(tagRaw >> 3);
                switch (wireType)
                {
                    case 0:
                        var v = (long)ReadVarint(stream)!.Value;
                        if (field == 1)
                        {
                            // tensor_type の elem_type
                            elemType = (int)v;
                        }
                        break;
                    case 2:
                        var len = (long)ReadVarint(stream)!.Value;
                        if (field == 1)
                        {
                            // Tensor { elem_type=1, shape=2 }
                            var subEnd = stream.Position + len;
                            while (stream.Position < subEnd)
                            {
                                var t = ReadVarint(stream)!.Value;
                                var wt = (int)(t & 0x7);
                                var f = (int)(t >> 3);
                                if (wt == 0 && f == 1)
                                {
                                    elemType = (int)(long)ReadVarint(stream)!.Value;
                                }
                                else if (wt == 2 && f == 2)
                                {
                                    var shapeLen = (long)ReadVarint(stream)!.Value;
                                    ParseShape(stream, shapeLen, dims);
                                }
                                else if (wt == 0)
                                {
                                    ReadVarint(stream);
                                }
                                else if (wt == 2)
                                {
                                    var skip = (long)ReadVarint(stream)!.Value;
                                    stream.Seek(skip, SeekOrigin.Current);
                                }
                                else if (wt == 1) { stream.Seek(8, SeekOrigin.Current); }
                                else if (wt == 5) { stream.Seek(4, SeekOrigin.Current); }
                                else { throw new InvalidDataException($"Tensor: wiretype {wt}"); }
                            }
                        }
                        else
                        {
                            stream.Seek(len, SeekOrigin.Current);
                        }
                        break;
                    case 1:
                        stream.Seek(8, SeekOrigin.Current);
                        break;
                    case 5:
                        stream.Seek(4, SeekOrigin.Current);
                        break;
                    default:
                        throw new InvalidDataException($"TypeProto: wiretype {wireType}");
                }
            }
        }

        /// <summary>TensorShapeProto { dim=1 } をパースする。</summary>
        static void ParseShape(Stream stream, long length, List<long> dims)
        {
            var end = stream.Position + length;
            while (stream.Position < end)
            {
                var tagRaw = ReadVarint(stream)!.Value;
                var wireType = (int)(tagRaw & 0x7);
                var field = (int)(tagRaw >> 3);
                switch (wireType)
                {
                    case 2:
                        var len = (long)ReadVarint(stream)!.Value;
                        if (field == 1)
                        {
                            // Dimension
                            var dimEnd = stream.Position + len;
                            long dimValue = -1;
                            while (stream.Position < dimEnd)
                            {
                                var t = ReadVarint(stream)!.Value;
                                var wt = (int)(t & 0x7);
                                var f = (int)(t >> 3);
                                if (wt == 0)
                                {
                                    var v = (long)ReadVarint(stream)!.Value;
                                    if (f == 1)
                                    {
                                        dimValue = v;
                                    }
                                }
                                else if (wt == 2)
                                {
                                    var skip = (long)ReadVarint(stream)!.Value;
                                    if (f == 2)
                                    {
                                        dimValue = -1; // dim_param (動的次元)
                                    }
                                    stream.Seek(skip, SeekOrigin.Current);
                                }
                                else
                                {
                                    throw new InvalidDataException($"Dimension: wiretype {wt}");
                                }
                            }
                            dims.Add(dimValue);
                        }
                        else
                        {
                            stream.Seek(len, SeekOrigin.Current);
                        }
                        break;
                    default:
                        throw new InvalidDataException($"TensorShapeProto: wiretype {wireType}");
                }
            }
        }

        static string ReadString(Stream stream, long length)
        {
            var bytes = new byte[length];
            stream.ReadExactly(bytes);
            return System.Text.Encoding.UTF8.GetString(bytes);
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
                    throw new InvalidDataException("varint が長すぎます");
                }
            }
        }
    }
}
