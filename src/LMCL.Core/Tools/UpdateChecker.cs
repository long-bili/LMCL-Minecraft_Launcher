using System.Diagnostics;
using System.Text.Json;
using LMCL.Core.Configuration;
using LMCL.Core.Diagnostics;

namespace LMCL.Core.Tools;

public sealed record UpdateInfo(string Version, string Url, string Notes, long Size);

/// <summary>
/// 启动器自更新检查。两种清单格式都支持：
///   · 自定义简洁 JSON：{ "version": "1.2.0", "url": "...", "notes": "...", "size": 123 }
///   · GitHub Releases API：取 tag_name / body / assets[0].browser_download_url
/// </summary>
public sealed class UpdateChecker
{
    private readonly ILogSink? _log;

    public UpdateChecker(ILogSink? log = null) => _log = log;

    public async Task<UpdateInfo?> CheckAsync(
        string currentVersion, string manifestUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(manifestUrl)) return null;

        try
        {
            using var http = Http.HttpClients.Create(useMirror: false, _log);
            var json = await http.GetStringAsync(manifestUrl, ct).ConfigureAwait(false);

            using var document = JsonDocument.Parse(json);
            var info = Parse(document.RootElement);
            if (info is null) return null;

            if (!IsNewer(info.Version, currentVersion))
            {
                _log?.Info($"已是最新版本（{currentVersion}）");
                return null;
            }

            _log?.Success($"发现新版本 {info.Version}");
            return info;
        }
        catch (Exception ex)
        {
            _log?.Warn($"检查更新失败：{ex.Message}");
            return null;
        }
    }

    public static UpdateInfo? Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;

        // GitHub Releases
        if (root.TryGetProperty("tag_name", out var tag) && tag.ValueKind == JsonValueKind.String)
        {
            var version = tag.GetString()!.TrimStart('v', 'V');
            var notes = Text(root, "body");
            string? url = null;
            long size = 0;

            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                var first = assets.EnumerateArray().FirstOrDefault();
                if (first.ValueKind == JsonValueKind.Object)
                {
                    url = Text(first, "browser_download_url");
                    if (first.TryGetProperty("size", out var s) && s.TryGetInt64(out var n)) size = n;
                }
            }

            return string.IsNullOrEmpty(url) ? null : new UpdateInfo(version, url!, notes, size);
        }

        // 简洁格式
        var simpleVersion = Text(root, "version").TrimStart('v', 'V');
        var simpleUrl = Text(root, "url");
        if (string.IsNullOrEmpty(simpleVersion) || string.IsNullOrEmpty(simpleUrl)) return null;

        long simpleSize = 0;
        if (root.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out var value))
            simpleSize = value;

        return new UpdateInfo(simpleVersion, simpleUrl!, Text(root, "notes"), simpleSize);
    }

    /// <summary>下载新版本到更新目录，返回文件路径。</summary>
    public async Task<string> DownloadAsync(
        UpdateInfo info, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        AppPaths.EnsureCreated();
        Directory.CreateDirectory(AppPaths.UpdateDir);

        var fileName = Path.GetFileName(new Uri(info.Url).LocalPath);
        if (string.IsNullOrWhiteSpace(fileName)) fileName = $"LMCL-{info.Version}.zip";
        var target = Path.Combine(AppPaths.UpdateDir, fileName);

        using var http = Http.HttpClients.Create(useMirror: false, _log);
        using var response = await http.GetAsync(info.Url, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var total = response.Content.Headers.ContentLength ?? info.Size;
        await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var destination = File.Create(target);

        var buffer = new byte[81920];
        long received = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            received += read;
            if (total > 0) progress?.Report((double)received / total);
        }

        _log?.Success($"新版本已下载：{target}");
        return target;
    }

    /// <summary>打开更新文件所在目录并选中它——不自动执行，交给用户决定。</summary>
    public static void RevealInExplorer(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
            {
                UseShellExecute = true,
            });
        }
        catch { /* 打不开就只提示路径 */ }
    }

    public static bool IsNewer(string candidate, string current)
    {
        var a = ParseVersion(candidate);
        var b = ParseVersion(current);
        var length = Math.Max(a.Count, b.Count);
        for (var i = 0; i < length; i++)
        {
            var left = i < a.Count ? a[i] : 0;
            var right = i < b.Count ? b[i] : 0;
            if (left != right) return left > right;
        }
        return false;
    }

    private static List<int> ParseVersion(string version)
    {
        var result = new List<int>();
        foreach (var part in (version ?? "").Split('.', '-', '+'))
        {
            var digits = new string(part.TakeWhile(char.IsDigit).ToArray());
            if (digits.Length == 0) break;
            result.Add(int.Parse(digits));
        }
        return result;
    }

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";
}
