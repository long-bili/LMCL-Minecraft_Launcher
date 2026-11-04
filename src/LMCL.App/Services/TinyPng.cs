using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace LMCL.App.Services;

/// <summary>
/// 生成一张最小的合法 PNG（纯色）。**只给自检用**。
///
/// 为什么要自己造图：多自定义皮肤那个探针要真的往皮肤库里导入两张图，
/// 才能验出「导入后列表变长、删掉后变短」。仓库里不方便塞测试素材，
/// 用代码现场拼一张最简单 —— 只要 IHDR 尺寸对、能被解码器认出来就行。
/// </summary>
internal static class TinyPng
{
    /// <summary>造一张 width×height 的纯色 RGBA PNG。</summary>
    public static byte[] Create(int width, int height, byte r = 0x4A, byte g = 0x90, byte b = 0xD9)
    {
        using var stream = new MemoryStream();

        stream.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4, 4), height);
        ihdr[8] = 8;   // bit depth
        ihdr[9] = 6;   // color type: RGBA
        WriteChunk(stream, "IHDR", ihdr);

        // 每行前面有一个 filter 字节（0 = None），后面才是 RGBA 像素
        var raw = new byte[height * (1 + width * 4)];
        for (var y = 0; y < height; y++)
        {
            var rowStart = y * (1 + width * 4);
            raw[rowStart] = 0;
            for (var x = 0; x < width; x++)
            {
                var p = rowStart + 1 + x * 4;
                raw[p] = r;
                raw[p + 1] = g;
                raw[p + 2] = b;
                raw[p + 3] = 0xFF;
            }
        }

        WriteChunk(stream, "IDAT", ZlibCompress(raw));
        WriteChunk(stream, "IEND", []);
        return stream.ToArray();
    }

    /// <summary>PNG 的 IDAT 要的是 zlib 流：2 字节头 + deflate 数据 + adler32。</summary>
    private static byte[] ZlibCompress(byte[] data)
    {
        using var output = new MemoryStream();
        output.WriteByte(0x78);  // CMF
        output.WriteByte(0x01);  // FLG：无预设字典，最快压缩

        using (var deflate = new DeflateStream(output, CompressionLevel.Fastest, leaveOpen: true))
            deflate.Write(data);

        uint a = 1, b = 0;
        foreach (var value in data)
        {
            a = (a + value) % 65521;
            b = (b + a) % 65521;
        }

        var adler = (b << 16) | a;
        output.WriteByte((byte)(adler >> 24));
        output.WriteByte((byte)(adler >> 16));
        output.WriteByte((byte)(adler >> 8));
        output.WriteByte((byte)adler);

        return output.ToArray();
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        var length = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        stream.Write(length);

        var typeBytes = Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes);
        stream.Write(data);

        var crcBytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, Crc32(typeBytes, data));
        stream.Write(crcBytes);
    }

    private static uint Crc32(byte[] type, byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (var value in type) crc = Step(crc, value);
        foreach (var value in data) crc = Step(crc, value);
        return crc ^ 0xFFFFFFFF;

        static uint Step(uint crc, byte value)
        {
            crc ^= value;
            for (var i = 0; i < 8; i++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
            return crc;
        }
    }
}
