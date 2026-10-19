using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace GodotOnnx.Setup;

/// <summary>資産1件の検査結果。</summary>
public sealed record EntryStatus(string Name, bool Complete, IReadOnlyList<string> Problems);

/// <summary>baseDir 直下の models/ 全体の検査結果。</summary>
public sealed record InspectResult(bool Complete, IReadOnlyList<EntryStatus> Entries);

/// <summary>ダウンロード進捗。</summary>
public sealed record DownloadProgress(string Message, long BytesDone, long BytesTotal);

/// <summary>
/// ModelCatalog に基づきモデル/辞書資産を検査・取得する (Godot 非依存)。
/// 同意 UI の提示は呼び出し側の責務。取得物はサイズと SHA-256 で検証し、
/// 不一致は破棄する (.part 一時ファイル → 検証後に原子的に配置)。
/// 辞書のような複数ファイルは tar.gz をメンバーホワイトリストで展開する。
/// </summary>
public sealed class ModelDownloader : IDisposable
{
    const int BufferSize = 1 << 20;

    readonly HttpClient http;

    public ModelDownloader(HttpClient? client = null)
    {
        if (client is not null)
        {
            http = client;
            return;
        }
        http = new HttpClient(new HttpClientHandler())
        {
            // 長時間の大容量 DL を許す (中断は CancellationToken で行う)
            Timeout = Timeout.InfiniteTimeSpan,
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("godotonnx-setupper/0.1");
    }

    public void Dispose() => http.Dispose();

    /// <summary>baseDir = models/ の親 (リポジトリルート / user:// など)。</summary>
    public static string ModelsDir(string baseDir) => Path.Combine(baseDir, "models");

    /// <summary>各エントリの整備状況 (ハッシュ照合、ネットワーク不要)。</summary>
    public static InspectResult Inspect(string baseDir)
    {
        var models = ModelsDir(baseDir);
        var entries = new List<EntryStatus>();
        foreach (var entry in ModelCatalog.Entries)
        {
            var problems = new List<string>();
            CheckEntry(models, entry, problems);
            entries.Add(new EntryStatus(entry.Name, problems.Count == 0, problems));
        }
        return new InspectResult(entries.TrueForAll(e => e.Complete), entries);
    }

    static void CheckEntry(string models, ModelCatalog.Entry entry, List<string> problems)
    {
        foreach (var f in entry.Files)
        {
            var p = Path.Combine(models, f.RelativePath);
            if (!File.Exists(p))
            {
                problems.Add($"不足: {f.RelativePath}");
            }
            else if (!FileMatches(p, f.SizeBytes, f.Sha256))
            {
                problems.Add($"内容不一致: {f.RelativePath} (期待 sha256 {f.Sha256[..12]}…)");
            }
        }
        if (entry.Archive is { } arc)
        {
            foreach (var m in arc.Members)
            {
                var p = Path.Combine(models, arc.TargetRelativeDir, m.MemberName);
                if (!File.Exists(p))
                {
                    problems.Add($"不足: {arc.TargetRelativeDir}/{m.MemberName}");
                }
                else if (!Sha256Matches(p, m.Sha256))
                {
                    problems.Add($"内容不一致: {arc.TargetRelativeDir}/{m.MemberName}");
                }
            }
        }
    }

    /// <summary>
    /// 欠落資産を baseDir/models へダウンロードする。整備済みのものはスキップ、
    /// 検証不一致のものだけ入れ替える。最後まで揃わなければ例外。
    /// </summary>
    public async Task DownloadAllAsync(
        string baseDir,
        IProgress<DownloadProgress>? progress,
        CancellationToken ct,
        IEnumerable<string>? onlyEntryNames = null)
    {
        var models = ModelsDir(baseDir);
        Directory.CreateDirectory(models);

        var wanted = FilterEntries(onlyEntryNames);
        var total = 0L;
        foreach (var e in wanted)
        {
            total += ModelCatalog.PerEntryBytes(e);
        }

        var done = 0L;
        void Report(string message)
        {
            progress?.Report(new DownloadProgress(message, done, total));
        }

        foreach (var entry in wanted)
        {
            foreach (var f in entry.Files)
            {
                var dest = Path.Combine(models, f.RelativePath);
                if (File.Exists(dest) && FileMatches(dest, f.SizeBytes, f.Sha256))
                {
                    done += f.SizeBytes;
                    Report($"整備済み (スキップ): {f.RelativePath}");
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                await DownloadVerifiedAsync(f.Urls, dest, f.SizeBytes, f.Sha256, done, total, progress, ct)
                    .ConfigureAwait(false);
                done += f.SizeBytes;
                Report($"取得完了: {f.RelativePath}");
            }

            if (entry.Archive is { } arc)
            {
                await InstallArchiveAsync(models, arc, done, total, progress, ct).ConfigureAwait(false);
                done += arc.SizeBytes;
                Report($"展開完了: {arc.TargetRelativeDir}/");
            }
        }

        Report("準備完了");
    }

    static IReadOnlyList<ModelCatalog.Entry> FilterEntries(IEnumerable<string>? onlyNames)
    {
        var all = ModelCatalog.Entries;
        if (onlyNames is null)
        {
            return all;
        }
        var needles = new HashSet<string>(onlyNames, StringComparer.OrdinalIgnoreCase);
        var list = new List<ModelCatalog.Entry>();
        foreach (var e in all)
        {
            foreach (var needle in needles)
            {
                if (e.Name.Contains(needle, StringComparison.OrdinalIgnoreCase))
                {
                    list.Add(e);
                    break;
                }
            }
        }
        if (list.Count == 0)
        {
            throw new ArgumentException($"--only に一致するエントリがありません: {string.Join(", ", onlyNames)}");
        }
        return list;
    }

    /// <summary>URL 列を順に試し、検証済みファイルを dest へ原子配置する。</summary>
    async Task DownloadVerifiedAsync(
        string[] urls,
        string dest,
        long expectedSize,
        string sha256,
        long doneBefore,
        long total,
        IProgress<DownloadProgress>? progress,
        CancellationToken ct)
    {
        var tmp = dest + ".part";
        Exception? last = null;
        foreach (var url in urls)
        {
            try
            {
                await DownloadOnceAsync(url, tmp, expectedSize, sha256, doneBefore, total, progress, ct)
                    .ConfigureAwait(false);
                File.Move(tmp, dest, overwrite: true);
                return;
            }
            catch (OperationCanceledException)
            {
                TryDelete(tmp);
                throw;
            }
            catch (Exception ex)
            {
                TryDelete(tmp);
                last = ex;
                progress?.Report(new DownloadProgress($"再試行: {url} ({ex.Message})", doneBefore, total));
            }
        }
        throw new InvalidOperationException($"取得失敗: {dest} — {last?.Message}");
    }

    async Task DownloadOnceAsync(
        string url,
        string tmpPath,
        long expectedSize,
        string sha256,
        long doneBefore,
        long total,
        IProgress<DownloadProgress>? progress,
        CancellationToken ct)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var target = File.Create(tmpPath);
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        var buffer = new byte[BufferSize];
        long written = 0;
        var lastPercent = -1;
        int read;
        while ((read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            hasher.AppendData(buffer, 0, read);
            written += read;
            var percent = total > 0 ? (int)((doneBefore + written) * 100 / total) : -1;
            if (percent != lastPercent)
            {
                lastPercent = percent;
                progress?.Report(new DownloadProgress(
                    $"{Path.GetFileName(tmpPath)} {written / 1048576}MB", doneBefore + written, total));
            }
        }

        await target.FlushAsync(ct).ConfigureAwait(false);
        await target.DisposeAsync().ConfigureAwait(false);

        if (expectedSize > 0 && written != expectedSize)
        {
            throw new InvalidDataException($"サイズ不一致: {written} / 期待 {expectedSize}");
        }
        var gotHex = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
        if (!string.Equals(gotHex, sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"SHA-256 不一致 ({gotHex[..16]}…)");
        }
    }

    /// <summary>辞書などの tar.gz を取得・展開し、メンバーを並べる。</summary>
    async Task InstallArchiveAsync(
        string models,
        ModelCatalog.ArchiveSpec arc,
        long doneBefore,
        long total,
        IProgress<DownloadProgress>? progress,
        CancellationToken ct)
    {
        var dir = Path.Combine(models, arc.TargetRelativeDir);
        var allPresent = true;
        foreach (var m in arc.Members)
        {
            var p = Path.Combine(dir, m.MemberName);
            if (!File.Exists(p) || !Sha256Matches(p, m.Sha256))
            {
                allPresent = false;
                break;
            }
        }
        if (allPresent)
        {
            progress?.Report(new DownloadProgress(
                $"整備済み (スキップ): {arc.TargetRelativeDir}/", doneBefore + arc.SizeBytes, total));
            return;
        }

        var archivePath = Path.Combine(Path.GetTempPath(), arc.ArchiveName);
        try
        {
            await DownloadVerifiedAsync(arc.Urls, archivePath, arc.SizeBytes, arc.Sha256, doneBefore, total, progress, ct)
                .ConfigureAwait(false);
            Directory.CreateDirectory(dir);
            ExtractTarGz(archivePath, dir, arc);
            foreach (var m in arc.Members)
            {
                var p = Path.Combine(dir, m.MemberName);
                if (!File.Exists(p) || !Sha256Matches(p, m.Sha256))
                {
                    throw new InvalidDataException($"展開後の検証に失敗: {m.MemberName}");
                }
            }
        }
        finally
        {
            TryDelete(archivePath);
        }
    }

    /// <summary>tar (ustar / GNU longname) の展開。メンバーはホワイトリスト一致のみ書き出す。</summary>
    static void ExtractTarGz(string archivePath, string destDir, ModelCatalog.ArchiveSpec arc)
    {
        using var fs = File.OpenRead(archivePath);
        using var gz = new GZipStream(fs, CompressionMode.Decompress);
        var header = new byte[512];
        string? longName = null;

        while (ReadBlock(gz, header))
        {
            var name = ReadAscii(header, 0, 100);
            var size = ParseOctal(header, 124, 12);
            var typeflag = (char)header[156];

            if (IsZeroBlock(header))
            {
                break; // 終端ブロック
            }

            if (typeflag == 'L')
            {
                var buf = new byte[size];
                FillExactly(gz, buf.AsSpan());
                longName = ReadNullTerminated(buf);
                SkipPadding(gz, size);
                continue;
            }

            var isRegular = typeflag == '0' || header[156] == 0;
            var entryName = longName ?? CombineUstarName(header, name);
            longName = null;

            var relative = isRegular && entryName is not null ? StripPrefix(entryName, arc.PrefixInArchive) : null;
            var member = relative is null ? null : FindMember(arc, relative);
            if (member is null)
            {
                SkipEntry(gz, size);
                continue;
            }

            var dest = Path.Combine(destDir, member.MemberName);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            using (var outFs = File.Create(dest))
            using (var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[BufferSize];
                long remaining = size;
                while (remaining > 0)
                {
                    var chunk = (int)Math.Min(buffer.Length, remaining);
                    FillExactly(gz, buffer.AsSpan(0, chunk));
                    outFs.Write(buffer, 0, chunk);
                    hasher.AppendData(buffer, 0, chunk);
                    remaining -= chunk;
                }
                var hex = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
                if (!string.Equals(hex, member.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"メンバーの SHA-256 不一致: {member.MemberName}");
                }
            }
            SkipPadding(gz, size);
        }
    }

    static ModelCatalog.MemberSpec? FindMember(ModelCatalog.ArchiveSpec arc, string relative)
    {
        foreach (var m in arc.Members)
        {
            if (string.Equals(m.MemberName, relative, StringComparison.Ordinal))
            {
                return m;
            }
        }
        return null;
    }

    /// <summary>"prefix/name" から name を取り出す (外れていれば null)。.. やルート指定は拒否。</summary>
    static string? StripPrefix(string entryName, string prefix)
    {
        if (!entryName.StartsWith(prefix + "/", StringComparison.Ordinal))
        {
            return null;
        }
        var rel = entryName[(prefix.Length + 1)..];
        if (rel.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(rel) || rel.Contains('\\'))
        {
            return null;
        }
        return rel;
    }

    static string CombineUstarName(byte[] header, string name)
    {
        var magic = ReadAscii(header, 257, 6);
        if (magic.StartsWith("ustar", StringComparison.Ordinal))
        {
            var prefix = ReadAscii(header, 345, 155);
            if (prefix.Length > 0)
            {
                return prefix + "/" + name;
            }
        }
        return name;
    }

    static bool ReadBlock(Stream s, byte[] buffer)
    {
        var filled = 0;
        while (filled < buffer.Length)
        {
            var read = s.Read(buffer, filled, buffer.Length - filled);
            if (read <= 0)
            {
                return false;
            }
            filled += read;
        }
        return true;
    }

    static void FillExactly(Stream s, Span<byte> span)
    {
        var filled = 0;
        while (filled < span.Length)
        {
            var read = s.Read(span[filled..]);
            if (read <= 0)
            {
                throw new EndOfStreamException("tar データが早期終端しました");
            }
            filled += read;
        }
    }

    static void SkipEntry(Stream s, long size)
    {
        var remaining = size;
        var buffer = new byte[BufferSize];
        while (remaining > 0)
        {
            var chunk = (int)Math.Min(buffer.Length, remaining);
            FillExactly(s, buffer.AsSpan(0, chunk));
            remaining -= chunk;
        }
        SkipPadding(s, size);
    }

    static void SkipPadding(Stream s, long size)
    {
        var pad = 512 - (int)(size % 512);
        if (pad == 512)
        {
            return;
        }
        var dummy = new byte[pad];
        FillExactly(s, dummy.AsSpan(0, pad));
    }

    static bool IsZeroBlock(byte[] block)
    {
        foreach (var b in block)
        {
            if (b != 0)
            {
                return false;
            }
        }
        return true;
    }

    static long ParseOctal(byte[] header, int offset, int length)
    {
        var value = 0L;
        for (var i = offset; i < offset + length; i++)
        {
            var b = header[i];
            if (b == 0 || b == (byte)' ')
            {
                break;
            }
            if (b < (byte)'0' || b > (byte)'7')
            {
                continue;
            }
            value = (value << 3) + (b - (byte)'0');
        }
        return value;
    }

    static string ReadAscii(byte[] header, int offset, int length)
    {
        var end = offset;
        while (end < offset + length && header[end] != 0)
        {
            end++;
        }
        return Encoding.ASCII.GetString(header, offset, end - offset).TrimEnd();
    }

    static string ReadNullTerminated(byte[] data)
    {
        var end = Array.IndexOf(data, (byte)0);
        if (end < 0)
        {
            end = data.Length;
        }
        return Encoding.ASCII.GetString(data, 0, end).TrimEnd();
    }

    public static string Sha256File(string path)
    {
        using var stream = File.OpenRead(path);
        using var hasher = SHA256.Create();
        return Convert.ToHexString(hasher.ComputeHash(stream)).ToLowerInvariant();
    }

    static bool Sha256Matches(string path, string expected)
    {
        try
        {
            return string.Equals(Sha256File(path), expected, StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException)
        {
            return false;
        }
    }

    static bool FileMatches(string path, long expectedSize, string expectedSha256)
    {
        try
        {
            var probe = new FileInfo(path);
            for (var i = 0; probe.LinkTarget is not null && i < 8; i++)
            {
                // シンボリックリンクは実体のサイズを測る (FileInfo はリンク自身の長さを返す)
                var next = Path.Combine(Path.GetDirectoryName(probe.FullName)!, probe.LinkTarget);
                probe = new FileInfo(Path.GetFullPath(next));
            }
            if (probe.Length != expectedSize)
            {
                return false;
            }
            return Sha256Matches(path, expectedSha256);
        }
        catch (IOException)
        {
            return false;
        }
    }

    static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // 破棄できない一時ファイルは無視 (次回の取得で上書きされる)
        }
    }
}
