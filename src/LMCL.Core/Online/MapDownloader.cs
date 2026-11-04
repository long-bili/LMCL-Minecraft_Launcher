using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;

namespace LMCL.Core.Online;

/// <summary>
/// 地图文件下载与本地导入（下载流程照 Campfire GPL-3.0 的 MapDownloader 独立重写）：
/// · 下载：CurseForge 两个 CDN 互为备份，SHA-1 校验，缓存一周；
/// · 导入：把 zip 解压进某个 .minecraft（或版本）目录的 saves 下，
///   zip 根直接是 level.dat 时多包一层以地图名命名的文件夹。
/// </summary>
public static class MapDownloader
{
    public const string BaseUrl = "https://api.mccampfire.top";

    private const long MaxSize = 4L * 1024 * 1024 * 1024;
    private static readonly TimeSpan KeepFor = TimeSpan.FromDays(7);
    private static readonly string[] Hosts = { "edge.forgecdn.net", "mediafilez.forgecdn.net" };

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"LMCL/{AppInfo.Version}");
        return client;
    }

    private static string Root => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LMCL", "downloads", "maps");

    /// <summary>地图下载缓存目录（修复工具清缓存用）。</summary>
    public static string CacheRoot => Root;

    /// <summary>
    /// 下载一个地图文件。targetPath 为空时存进下载缓存（可复用），
    /// 指定了就直接存到用户挑的位置。返回保存路径。progress 报 0~1 的进度。
    /// </summary>
    public static async Task<string> DownloadAsync(MapFile file, IProgress<double>? progress, CancellationToken ct,
        string? targetPath = null)
    {
        if (string.IsNullOrEmpty(file.Url))
            throw new InvalidOperationException("作者没有开放这个文件的直接下载，请到它的网页手动下载。");
        if (file.Size > MaxSize)
            throw new IOException("文件超过 4 GB，下载器不支持。");

        var folder = targetPath is null
            ? Path.Combine(Root, file.Id.ToString(CultureInfo.InvariantCulture))
            : Path.GetDirectoryName(Path.GetFullPath(targetPath))!;
        var path = targetPath ?? Path.Combine(folder, SafeName(file));

        if (targetPath is null && IsComplete(path, file))
        {
            Touch(folder);
            return path;
        }

        Prune(folder);
        Directory.CreateDirectory(folder);
        var temporary = path + ".tmp";
        Exception? last = null;

        foreach (var url in Urls(file.Url))
        {
            try
            {
                await DownloadToAsync(url, temporary, file, progress, ct).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(file.Sha1)
                    && !string.Equals(Sha1(temporary), file.Sha1, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("下载的文件校验不对，可能没传完整。");
                if (File.Exists(path)) File.Delete(path);
                File.Move(temporary, path);
                return path;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException
                                       or TimeoutException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
            {
                last = ex;
            }
            finally
            {
                TryDelete(temporary);
                if (ct.IsCancellationRequested) RemoveIfEmpty(folder);
            }
        }

        RemoveIfEmpty(folder);
        throw new IOException(last?.Message ?? "下载没有成功。", last);
    }

    private static async Task DownloadToAsync(string url, string target, MapFile file,
        IProgress<double>? progress, CancellationToken ct)
    {
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? file.Size;
        if (total > MaxSize || (file.Size > 0 && total != file.Size))
            throw new InvalidDataException("服务器给的文件大小对不上，已中止。");

        long received = 0;
        progress?.Report(0);
        using var network = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

        var buffer = new byte[81920];
        int read;
        while ((read = await network.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            received += read;
            if (received > MaxSize)
                throw new InvalidDataException("服务器发的内容比文件本身大，已中止。");
            if (total > 0) progress?.Report((double)received / total);
        }

        if (total > 0 && received != total)
            throw new IOException("下载中途断了。");
    }

    /// <summary>把下载好的（或用户手头已有的）地图 zip 解压到目标 saves 目录。返回解压出的地图文件夹名。</summary>
    public static string ImportZip(string zipPath, string savesDir, string? worldName = null)
    {
        if (!File.Exists(zipPath))
            throw new FileNotFoundException("找不到地图压缩包。", zipPath);

        Directory.CreateDirectory(savesDir);

        using var archive = ZipFile.OpenRead(zipPath);
        if (archive.Entries.Count == 0)
            throw new InvalidDataException("压缩包里什么都没有。");

        // zip 根直接是 level.dat（打包时没包文件夹）→ 用地图名包一层
        bool rootWorld = archive.Entries.Any(e =>
            string.IsNullOrEmpty(e.FullName) || e.FullName == "level.dat" || e.FullName == "level.dat_old");
        var targetName = Sanitize(worldName ?? Path.GetFileNameWithoutExtension(zipPath));
        var targetDir = Path.Combine(savesDir, targetName);
        var finalDir = targetDir;
        for (var n = 1; Directory.Exists(finalDir) && Directory.EnumerateFileSystemEntries(finalDir).Any(); n++)
            finalDir = $"{targetDir} ({n})";

        Directory.CreateDirectory(finalDir);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name) && entry.FullName.EndsWith('/')) continue;
            if (entry.FullName.Contains("..")) continue; // 拒绝路径穿越

            var relative = rootWorld ? entry.FullName : TrimRoot(entry.FullName);
            if (relative.Length == 0) continue;

            var dest = Path.GetFullPath(Path.Combine(finalDir, relative));
            if (!dest.StartsWith(Path.GetFullPath(finalDir), StringComparison.OrdinalIgnoreCase))
                continue; // 路径穿越第二道闸
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            entry.ExtractToFile(dest, overwrite: true);
        }

        if (!File.Exists(Path.Combine(finalDir, "level.dat")))
            throw new InvalidDataException("解压后没找到 level.dat —— 这可能不是一张地图，或包了一层多余的文件夹。已保留解压结果，可手动挪动。");

        return Path.GetFileName(finalDir);
    }

    /// <summary>去掉 zip 条目路径的第一层文件夹（Campfire 的地图包顶层是地图文件夹本身）。</summary>
    private static string TrimRoot(string fullName)
    {
        var trimmed = fullName.Replace('\\', '/').TrimStart('/');
        var slash = trimmed.IndexOf('/');
        return slash < 0 ? trimmed : trimmed[(slash + 1)..];
    }

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return clean.Length == 0 ? "world" : clean.Length > 60 ? clean[..60] : clean;
    }

    /// <summary>清掉下载缓存（保留 keep 这一份）。返回删掉的字节数。</summary>
    public static long PruneCache(string? keepFolder = null)
    {
        long freed = 0;
        if (!Directory.Exists(Root)) return 0;
        foreach (var folder in new DirectoryInfo(Root).EnumerateDirectories())
        {
            if (keepFolder is not null && string.Equals(folder.FullName, keepFolder, StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                freed += folder.EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
                folder.Delete(true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 占用中的下次再删
            }
        }
        return freed;
    }

    private static IEnumerable<string> Urls(string url)
    {
        yield return url;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) yield break;
        if (!Hosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase)) yield break;
        foreach (var host in Hosts.Where(h => !h.Equals(uri.Host, StringComparison.OrdinalIgnoreCase)))
            yield return new UriBuilder(uri) { Host = host }.Uri.AbsoluteUri;
    }

    private static bool IsComplete(string path, MapFile file)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || (file.Size > 0 && info.Length != file.Size)) return false;
            return string.IsNullOrEmpty(file.Sha1)
                   || string.Equals(Sha1(path), file.Sha1, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void Prune(string keep)
    {
        if (!Directory.Exists(Root)) return;
        foreach (var folder in new DirectoryInfo(Root).EnumerateDirectories())
        {
            if (string.Equals(folder.FullName, keep, StringComparison.OrdinalIgnoreCase)) continue;
            if (DateTime.UtcNow - folder.LastWriteTimeUtc <= KeepFor) continue;
            try { folder.Delete(true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static void Touch(string folder)
    {
        try { Directory.SetLastWriteTimeUtc(folder, DateTime.UtcNow); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static string SafeName(MapFile file)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var name = new string((file.FileName ?? "").Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray())
            .Trim().TrimEnd('.');
        if (name.Length == 0) name = file.Id.ToString(CultureInfo.InvariantCulture) + ".zip";
        return name.Length > 100
            ? name[..(100 - Path.GetExtension(name).Length)] + Path.GetExtension(name)
            : name;
    }

    private static string Sha1(string path)
    {
        using var stream = File.OpenRead(path);
        using var sha = SHA1.Create();
        return Convert.ToHexStringLower(sha.ComputeHash(stream));
    }

    private static void RemoveIfEmpty(string folder)
    {
        try
        {
            if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
                Directory.Delete(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
