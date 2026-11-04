using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LMCL.App.Services;

/// <summary>
/// 皮肤 / 披风预览的像素级裁剪。Minecraft 的贴图是「一张 64×64 里拼了所有部件」的
///  UV 图，所以预览不是整张缩略，而是把对应的那一块抠出来、按整数倍放大——
///  半像素都不许插值，不然边缘糊成一片，像素风就没了。
///
///  头部   ：脸 (8,8,8,8) + 帽子第二层 (40,8,8,8)
///  披风   ：背面的可见部分 (1,1,10,16)，其余是几乎看不见的侧面和内侧
/// </summary>
public static class SkinPreviewRenderer
{
    /// <summary>从皮肤 PNG 文件裁一个头部预览。读不出来返回 null。</summary>
    public static BitmapSource? Head(string? pngPath, int size = 96)
    {
        var source = Load(pngPath);
        return source is null ? null : Head(source, size);
    }

    /// <summary>从一个已经解码好的皮肤位图裁头部预览。</summary>
    public static BitmapSource? Head(BitmapSource source, int size = 96)
    {
        if (source.PixelWidth < 64 || source.PixelHeight < 32) return null;

        var scale = source.PixelWidth / 64;
        var unit = 8 * scale;

        var face = CopyRect(source, 8 * scale, 8 * scale, unit, unit);
        var head = face;

        // 帽子第二层：64×32 的老皮肤没有这一块，跳过
        if (source.PixelHeight >= 8 * scale + unit)
        {
            var hat = CopyRect(source, 40 * scale, 8 * scale, unit, unit);
            head = CompositeOver(face, hat);
        }

        return Build(head, unit, unit, size, size);
    }

    /// <summary>从披风 PNG 裁背面那一块。老格式（22×17）整张就是可见部分，直接放大。</summary>
    public static BitmapSource? Cape(string? pngPath, int width = 60, int height = 96)
    {
        var source = Load(pngPath);
        return source is null ? null : Cape(source, width, height);
    }

    public static BitmapSource? Cape(BitmapSource source, int width = 60, int height = 96)
    {
        if (source.PixelWidth is < 12 or < 64 && source.PixelHeight < 16) return null;

        if (source.PixelWidth >= 64 && source.PixelHeight >= 32)
        {
            var scale = source.PixelWidth / 64;
            var crop = CopyRect(source, 1 * scale, 1 * scale, 10 * scale, 16 * scale);
            return Build(crop, 10 * scale, 16 * scale, width, height);
        }

        // 老格式整张就是可见部分
        var pixels = PixelsOf(source);
        return Build(pixels, source.PixelWidth, source.PixelHeight, width, height);
    }

    // ── 内部 ────────────────────────────────────────────────

    private static BitmapSource? Load(string? pngPath)
    {
        if (string.IsNullOrWhiteSpace(pngPath) || !File.Exists(pngPath)) return null;

        try
        {
            // 必须转成 Bgra32 再用。
            //
            // Minecraft 自带的 Steve / Alex 是**调色板 PNG**（colorType=3），WPF 解出来是
            // Indexed8：每像素 1 字节。下游的 PixelsOf 一律按「每像素 4 字节 BGRA」读，
            // 直接 CopyPixels 会读到错位的数据 —— 表现就是内置皮肤渲染成一块黑。
            // 用户自己导入的 PNG 是 RGBA，正好躲过了这个坑，所以只有内置那两张是黑的。
            var decoder = BitmapDecoder.Create(
                new Uri(pngPath, UriKind.Absolute),
                BitmapCreateOptions.IgnoreImageCache,
                BitmapCacheOption.OnLoad);

            if (decoder.Frames.Count == 0) return null;

            var frame = decoder.Frames[0];
            BitmapSource result = frame;

            if (frame.Format != PixelFormats.Bgra32)
            {
                var converted = new FormatConvertedBitmap();
                converted.BeginInit();
                converted.Source = frame;
                converted.DestinationFormat = PixelFormats.Bgra32;
                converted.EndInit();
                result = converted;
            }

            result.Freeze();
            return result;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>整张位图的 BGRA 像素。</summary>
    private static byte[] PixelsOf(BitmapSource source)
    {
        var full = new byte[source.PixelWidth * source.PixelHeight * 4];
        source.CopyPixels(full, source.PixelWidth * 4, 0);
        return full;
    }

    /// <summary>把一块矩形像素抠出来，转成 BGRA 字节数组（WPF 的 WriteableBitmap 就是这个次序）。</summary>
    private static byte[] CopyRect(BitmapSource source, int x, int y, int width, int height)
    {
        var full = PixelsOf(source);

        var result = new byte[width * height * 4];
        for (var row = 0; row < height; row++)
        {
            var srcOffset = ((y + row) * source.PixelWidth + x) * 4;
            Buffer.BlockCopy(full, srcOffset, result, row * width * 4, width * 4);
        }

        return result;
    }

    /// <summary>把帽子层按 alpha 叠到底层上。完全不透明的像素直接盖掉。</summary>
    private static byte[] CompositeOver(byte[] background, byte[] overlay)
    {
        var result = (byte[])background.Clone();

        for (var i = 0; i < result.Length; i += 4)
        {
            var cover = overlay[i + 3] / 255f;
            if (cover <= 0.004f) continue;

            var keep = 1f - cover;
            result[i] = (byte)(overlay[i] * cover + result[i] * keep);
            result[i + 1] = (byte)(overlay[i + 1] * cover + result[i + 1] * keep);
            result[i + 2] = (byte)(overlay[i + 2] * cover + result[i + 2] * keep);
            result[i + 3] = 255;
        }

        return result;
    }

    /// <summary>整数倍最近邻放大到目标尺寸。透明像素垫成不透明，免得和卡片底色打架。</summary>
    private static WriteableBitmap Build(byte[] pixels, int srcW, int srcH, int dstW, int dstH)
    {
        var scaled = new byte[dstW * dstH * 4];

        for (var y = 0; y < dstH; y++)
        {
            var sy = y * srcH / dstH;
            for (var x = 0; x < dstW; x++)
            {
                var sx = x * srcW / dstW;
                var si = (sy * srcW + sx) * 4;
                var di = (y * dstW + x) * 4;
                scaled[di] = pixels[si];
                scaled[di + 1] = pixels[si + 1];
                scaled[di + 2] = pixels[si + 2];
                scaled[di + 3] = pixels[si + 3] == 0 ? (byte)255 : pixels[si + 3];
            }
        }

        var bitmap = new WriteableBitmap(dstW, dstH, 96, 96, PixelFormats.Bgra32, null);
        bitmap.WritePixels(new Int32Rect(0, 0, dstW, dstH), scaled, dstW * 4, 0);
        bitmap.Freeze();
        return bitmap;
    }
}
