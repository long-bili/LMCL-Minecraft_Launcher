using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Windows.Media.Imaging;
using LMCL.Core.Configuration;

namespace LMCL.App.Services;

/// <summary>
/// 搜索结果里的工程图标。Modrinth 给的是 https 地址，直接绑到 Image 上每条都要联网一次，
/// 断网还是一片空白；所以按地址哈希落一份到本地缓存，之后再取只在硬盘上读。
/// </summary>
public static class IconLoader
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };

    /// <summary>取一张图标。取不到（没地址 / 网不通 / 不是图）一律返回 null，界面上留空底。</summary>
    public static async Task<BitmapImage?> LoadAsync(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;

        try
        {
            var file = CachePath(url);

            if (!File.Exists(file) || new FileInfo(file).Length == 0)
            {
                var bytes = await Http.GetByteArrayAsync(url).ConfigureAwait(false);
                if (bytes.Length == 0) return null;

                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                var temp = file + ".tmp";
                await File.WriteAllBytesAsync(temp, bytes).ConfigureAwait(false);
                File.Move(temp, file, overwrite: true);
            }

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            // OnLoad：读完就断开文件句柄；IgnoreImageCache：同一路径被覆盖后不会继续显示旧图
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bitmap.UriSource = new Uri(file, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            // 图标是装饰，取不到不该影响列表本身
            return null;
        }
    }

    private static string CachePath(string url)
    {
        var name = url.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
        if (name.Length is 0 or > 128 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            name = Convert.ToHexString(SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(url))).ToLowerInvariant();

        return Path.Combine(AppPaths.CacheDir, "icons", name);
    }
}
