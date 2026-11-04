using System.Globalization;
using System.Text.Json;

namespace LMCL.Core.Online;

/// <summary>搜索结果里的地图摘要。</summary>
public class MapSummary
{
    public int Id { get; init; }
    public string Name { get; init; } = "";
    public string Summary { get; init; } = "";
    public string Author { get; init; } = "";
    public long Downloads { get; init; }
    public string Logo { get; init; } = "";
    public string Website { get; init; } = "";
    public IReadOnlyList<string> Versions { get; init; } = Array.Empty<string>();
}

/// <summary>一个可下载的地图文件（对应 CurseForge 的 file）。</summary>
public sealed class MapFile
{
    public int Id { get; init; }
    public string Name { get; init; } = "";
    public string FileName { get; init; } = "";
    public IReadOnlyList<string> Versions { get; init; } = Array.Empty<string>();
    public long Size { get; init; }
    public DateTime? Date { get; init; }
    /// <summary>release / beta / alpha。</summary>
    public string Type { get; init; } = "";
    public string Sha1 { get; init; } = "";
    /// <summary>下载地址；作者关了第三方分发时为空。</summary>
    public string? Url { get; init; }
}

/// <summary>地图详情：摘要 + 文件列表。</summary>
public sealed class MapDetail : MapSummary
{
    public string Description { get; init; } = "";
    public IReadOnlyList<MapFile> Files { get; init; } = Array.Empty<MapFile>();
}

/// <summary>地图搜索结果。</summary>
public sealed class MapSearchResult
{
    public int Total { get; init; }

    /// <summary>服务端每页条数（算总页数用）。</summary>
    public int PageSize { get; init; } = 20;

    public IReadOnlyList<MapSummary> Maps { get; init; } = Array.Empty<MapSummary>();
}

/// <summary>
/// 在线地图目录：CurseForge 的世界地图，经 Campfire 公共服务（持有 API key）读出。
/// 协议照 Campfire（GPL-3.0）公开源码的 MapCatalog 独立重写；服务不可用时
/// 在线浏览降级不可用，但本地导入 .zip 地图永远可用。
/// </summary>
public static class MapCatalog
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"LMCL/{AppInfo.Version}");
        client.Timeout = TimeSpan.FromSeconds(20);
        return client;
    }

    /// <summary>排序方式（与服务端参数一致）。</summary>
    public enum MapSort { Popular, Downloads, Updated, Released, Name }

    /// <summary>按关键字 / 版本 / 排序搜索地图。page 从 0 起；pageSize 由用户选（服务端尽力尊重）。</summary>
    public static async Task<MapSearchResult> SearchAsync(
        string query, string version, MapSort sort, int page,
        int pageSize = 20, CancellationToken ct = default)
    {
        var parameters = new List<string>();
        if (!string.IsNullOrWhiteSpace(query))
            parameters.Add("q=" + Uri.EscapeDataString(query.Trim()));
        if (!string.IsNullOrWhiteSpace(version))
            parameters.Add("version=" + Uri.EscapeDataString(version.Trim()));
        parameters.Add("sort=" + sort.ToString().ToLowerInvariant());
        parameters.Add("page=" + page.ToString(CultureInfo.InvariantCulture));
        if (pageSize > 0)
            parameters.Add("pageSize=" + pageSize.ToString(CultureInfo.InvariantCulture));

        using var json = await GetAsync("/v1/maps/search?" + string.Join("&", parameters), ct).ConfigureAwait(false);
        return new MapSearchResult
        {
            Total = Int(json.RootElement, "total", 0),
            PageSize = Math.Max(1, Int(json.RootElement, "pageSize", 20)),
            Maps = Items(json.RootElement, "maps").Select(FillSummary).Where(m => m.Id > 0).ToList(),
        };
    }

    /// <summary>取一份地图的详情（含可下载文件列表）。</summary>
    public static async Task<MapDetail> GetAsync(int id, CancellationToken ct = default)
    {
        using var json = await GetAsync($"/v1/maps/{id.ToString(CultureInfo.InvariantCulture)}", ct).ConfigureAwait(false);
        if (!json.RootElement.TryGetProperty("map", out var map) || map.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("地图服务返回的内容不完整。");

        var files = Items(json.RootElement, "files").Select(f => new MapFile
        {
            Id = Int(f, "id", 0),
            Name = Str(f, "name"),
            FileName = Str(f, "fileName"),
            Versions = Strs(f, "versions"),
            Size = Long(f, "size", 0),
            Date = Date(f, "date"),
            Type = Str(f, "type"),
            Sha1 = Str(f, "sha1"),
            Url = f.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null,
        }).Where(f => f.Id > 0).ToList();

        var summary = FillSummary(map);
        return new MapDetail
        {
            Id = summary.Id, Name = summary.Name, Summary = summary.Summary,
            Author = summary.Author, Downloads = summary.Downloads, Logo = summary.Logo,
            Website = summary.Website,
            Versions = summary.Versions,
            Description = Str(map, "description"),
            Files = files,
        };
    }

    private static MapSummary FillSummary(JsonElement e) => new()
    {
        Id = Int(e, "id", 0),
        Name = Str(e, "name"),
        Summary = Str(e, "summary"),
        Author = e.TryGetProperty("authors", out var a) && a.ValueKind == JsonValueKind.Array
            ? string.Join(", ", a.EnumerateArray().Select(x => x.GetString() ?? "").Where(s => s.Length > 0))
            : "",
        Downloads = Long(e, "downloads", 0),
        Logo = Str(e, "logo"),
        Website = Str(e, "website"),
        Versions = Strs(e, "versions"),
    };

    private static async Task<JsonDocument> GetAsync(string path, CancellationToken ct)
    {
        using var response = await Http.GetAsync(MapDownloader.BaseUrl + path, ct).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        JsonDocument doc;
        try { doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text); }
        catch (JsonException) { doc = JsonDocument.Parse("{}"); }

        if (response.IsSuccessStatusCode) return doc;

        var error = Str(doc.RootElement, "error");
        doc.Dispose();
        throw new InvalidOperationException((int)response.StatusCode switch
        {
            503 or 404 => "在线地图服务暂时不可用，稍后再试；本地导入 .zip 地图不受影响。",
            429 => "请求太频繁，等一会儿再搜。",
            _ => $"在线地图服务返回 HTTP {(int)response.StatusCode}" + (error.Length > 0 ? $"（{error}）" : ""),
        });
    }

    // ── JSON 小工具（与 CampfireLobby 同款） ────────────────

    private static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? "" : "";

    private static int Int(JsonElement e, string name, int fallback) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.TryGetInt32(out var n) ? n : fallback;

    private static long Long(JsonElement e, string name, long fallback) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.TryGetInt64(out var n) ? n : fallback;

    private static DateTime? Date(JsonElement e, string name)
    {
        var text = Str(e, name);
        return DateTime.TryParse(text, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var date) ? date.ToLocalTime() : null;
    }

    private static IReadOnlyList<string> Strs(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();
        return v.EnumerateArray().Select(x => x.GetString() ?? "").Where(s => s.Length > 0).ToList();
    }

    private static IEnumerable<JsonElement> Items(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray() : Enumerable.Empty<JsonElement>();
}
