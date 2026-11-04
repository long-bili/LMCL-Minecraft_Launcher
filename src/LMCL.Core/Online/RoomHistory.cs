using System.Globalization;
using System.Text;

namespace LMCL.Core.Online;

/// <summary>这台电脑开过或加入过的一间房。</summary>
public sealed class RoomRecord
{
    public RoomRecord(string code, bool isHost, string title, DateTime lastUsed, bool favorite)
    {
        Code = code;
        IsHost = isHost;
        Title = title;
        LastUsed = lastUsed;
        Favorite = favorite;
    }

    /// <summary>邀请码，如 U/1A2B-3C4D-5E6F-7G8H。</summary>
    public string Code { get; }

    /// <summary>是不是在自己这台机器上开的房（再开一次沿用同一个码）。</summary>
    public bool IsHost { get; }

    /// <summary>上次记录时的房间标题 / 房主名。</summary>
    public string Title { get; }

    public DateTime LastUsed { get; }

    /// <summary>收藏的房间不参与「最近」裁剪，一直留在列表顶部。</summary>
    public bool Favorite { get; }
}

/// <summary>
/// 最近房间记录（版式照 Campfire GPL-3.0 的 RoomHistory 独立重写）：
/// 收藏置顶、其余按时间倒序，非收藏只留最近 10 条。
/// 落盘在 %APPDATA%\LMCL\link\rooms.txt，一行一房，Tab 分列。
/// </summary>
public static class RoomHistory
{
    private const int MaxRecent = 10;
    private const int MaxFavorites = 30;

    private static readonly object Sync = new();
    private static List<RoomRecord>? _items;

    private static string FilePath
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LMCL", "link");
            return Path.Combine(dir, "rooms.txt");
        }
    }

    /// <summary>收藏在前、时间倒序的完整列表。</summary>
    public static IReadOnlyList<RoomRecord> GetAll()
    {
        lock (Sync)
        {
            EnsureLoaded();
            return _items!.OrderByDescending(r => r.Favorite).ThenByDescending(r => r.LastUsed).ToList();
        }
    }

    /// <summary>记录「刚开 / 刚加入了一间房」。已有的同码同角色记录保留收藏标记并更新时间。</summary>
    public static void Record(bool isHost, string code, string? title)
    {
        if (string.IsNullOrEmpty(code)) return;
        lock (Sync)
        {
            EnsureLoaded();
            var known = _items!.FirstOrDefault(r => r.Code == code && r.IsHost == isHost);
            var favorite = known?.Favorite ?? false;
            if (known is not null) _items!.Remove(known);
            _items!.Add(new RoomRecord(code, isHost, Clean(title) ?? "未命名房间", DateTime.UtcNow, favorite));
            Trim();
            Save();
        }
    }

    public static void SetFavorite(RoomRecord record, bool favorite)
    {
        lock (Sync)
        {
            EnsureLoaded();
            var known = _items!.FirstOrDefault(r => r.Code == record.Code && r.IsHost == record.IsHost);
            if (known is null || known.Favorite == favorite) return;
            _items!.Remove(known);
            _items!.Add(new RoomRecord(known.Code, known.IsHost, known.Title, known.LastUsed, favorite));
            Trim();
            Save();
        }
    }

    public static void Remove(RoomRecord record)
    {
        lock (Sync)
        {
            EnsureLoaded();
            if (_items!.RemoveAll(r => r.Code == record.Code && r.IsHost == record.IsHost) == 0) return;
            Save();
        }
    }

    public static void ClearRecent()
    {
        lock (Sync)
        {
            EnsureLoaded();
            if (_items!.Count == 0) return;
            _items.RemoveAll(r => !r.Favorite);
            Save();
        }
    }

    private static void Trim()
    {
        var favorites = _items!.Where(r => r.Favorite).OrderByDescending(r => r.LastUsed).Take(MaxFavorites);
        var recent = _items!.Where(r => !r.Favorite).OrderByDescending(r => r.LastUsed).Take(MaxRecent);
        _items = favorites.Concat(recent).ToList();
    }

    private static string? Clean(string? text)
    {
        var cleaned = (text ?? "").Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();
        return cleaned.Length > 0 ? cleaned : null;
    }

    private static void EnsureLoaded()
    {
        if (_items is not null) return;
        _items = new List<RoomRecord>();
        try
        {
            if (!File.Exists(FilePath)) return;
            foreach (var line in File.ReadAllLines(FilePath, Encoding.UTF8))
            {
                // host, favorite, ticks, code, title
                var parts = line.Split('\t');
                if (parts.Length < 5) continue;
                if (!long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
                    || ticks <= 0) continue;
                if (RoomCode.Find(parts[3]) is not { } code) continue;
                _items.Add(new RoomRecord(code.Code, parts[0] == "1", parts[4],
                    new DateTime(ticks, DateTimeKind.Utc).ToLocalTime(), parts[1] == "1"));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 读不出来就当没有历史，别让启动器起不来
        }
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var lines = _items!.Select(r => string.Join('\t',
                r.IsHost ? "1" : "0", r.Favorite ? "1" : "0",
                r.LastUsed.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture), r.Code, r.Title));
            File.WriteAllText(FilePath, string.Join("\r\n", lines) + "\r\n", new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 存不进去就算了，下次再记
        }
    }
}
