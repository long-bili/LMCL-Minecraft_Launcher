namespace LMCL.Core.Game;

/// <summary>
/// 按「数字段」比较版本号。各加载器的版本列表顺序并不统一
/// （maven 元数据是升序、Fabric/Quilt 的 API 是降序），
/// 与其猜来源顺序，不如自己排。
///
/// 能正确处理 <c>47.4.24</c>、<c>1.20.1-47.4.24</c>、<c>21.1.72</c> 这类字符串：
/// 抽出所有连续数字段逐个比较，前缀相同时自然退化到比较后续构建号。
/// </summary>
public sealed class NumericVersionComparer : IComparer<string>
{
    public static readonly NumericVersionComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        var left = Tokenize(x);
        var right = Tokenize(y);
        var length = Math.Max(left.Count, right.Count);

        for (var i = 0; i < length; i++)
        {
            var a = i < left.Count ? left[i] : 0;
            var b = i < right.Count ? right[i] : 0;
            if (a != b) return a.CompareTo(b);
        }

        // 数字完全一致时，用原始字符串兜底，保证排序稳定
        return string.CompareOrdinal(x ?? "", y ?? "");
    }

    private static List<long> Tokenize(string? text)
    {
        var result = new List<long>();
        if (string.IsNullOrEmpty(text)) return result;

        long current = 0;
        var inNumber = false;

        foreach (var c in text!)
        {
            if (char.IsDigit(c))
            {
                inNumber = true;
                current = current * 10 + (c - '0');
                if (current > long.MaxValue / 10) current = long.MaxValue;   // 防止溢出
            }
            else if (inNumber)
            {
                result.Add(current);
                current = 0;
                inNumber = false;
            }
        }

        if (inNumber) result.Add(current);
        return result;
    }
}
