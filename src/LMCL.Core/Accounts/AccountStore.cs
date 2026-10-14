using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using CmlLib.Core.Auth;
using LMCL.Core.Configuration;

namespace LMCL.Core.Accounts;

public enum AccountType
{
    Offline,
    Microsoft,
    ThirdParty,
}

/// <summary>
/// 一份皮肤档案。字段和 Minecraft 服务 <c>GET /minecraft/profile</c> 返回的 skins 数组一一对应。
///
/// 会随 <see cref="Account"/> 一起写进 accounts.json（所以这里**不能**加 [JsonIgnore]）：
/// 缓存下来之后，断网也能把上一次看到的皮肤画出来，不用每次打开窗口都联网。
/// </summary>
public sealed class SkinInfo
{
    /// <summary>皮肤 ID，切换激活皮肤时用的就是它。</summary>
    public string Id { get; set; } = "";

    /// <summary>纹理 PNG 地址，形如 https://textures.minecraft.net/texture/&lt;hash&gt;。</summary>
    public string Url { get; set; } = "";

    /// <summary>手臂模型：CLASSIC（经典，4px 粗手臂）或 SLIM（纤细，3px）。</summary>
    public string Variant { get; set; } = "";

    /// <summary>ACTIVE 表示当前正穿着这一件。</summary>
    public string State { get; set; } = "";

    /// <summary>官方皮肤的别名（standard / slim 之类）；自己上传的皮肤这里是空的。</summary>
    public string Alias { get; set; } = "";

    [JsonIgnore]
    public bool IsActive => State.Equals("ACTIVE", StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public bool IsSlim => Variant.Equals("SLIM", StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public string VariantLabel => IsSlim ? "纤细（Alex）" : "经典（Steve）";
}

/// <summary>
/// 一份披风档案，对应档案里 capes 数组的一项。
/// 披风只能从「已拥有」里选，普通账号没法上传——所以这里只有查看和切换，没有上传。
/// </summary>
public sealed class CapeInfo
{
    public string Id { get; set; } = "";

    public string Url { get; set; } = "";

    /// <summary>ACTIVE 表示当前正戴着这一件。</summary>
    public string State { get; set; } = "";

    /// <summary>官方给的披风标识（Migrator / MapMaker / Realms …）。</summary>
    public string Alias { get; set; } = "";

    [JsonIgnore]
    public bool IsActive => State.Equals("ACTIVE", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 披风在界面上显示的名字。官方只给英文别名，这里翻成中文；
    /// 认不出来就退回别名本身，再没有就统一叫「披风」。
    /// </summary>
    [JsonIgnore]
    public string DisplayName => Alias.ToLowerInvariant() switch
    {
        "migrator" => "迁移者披风",
        "mapmaker" => "地图制作者披风",
        "realms" => "Realms 披风",
        "founder" => "创始人披风",
        "mojang" or "mojangstudios" => "Mojang 披风",
        "mojangclassic" => "Mojang 经典披风",
        "translator" or "translator-chinese" or "chinese_translator" => "中文翻译者披风",
        "crowdin" or "crowdin-translator" => "Crowdin 翻译者披风",
        "dannybstyle" => "DannyB 披风",
        "julianclark" => "Julian Clark 披风",
        "cheapsh0t" => "Cheapsh0t 披风",
        "mrmeess" or "mr_mess" => "MrMess 披风",
        "prismarine" => "海晶石披风",
        "birthday" => "生日披风",
        "purpleheart" => "紫心披风",
        "common" => "普通披风",
        "pan" => "Pan 披风",
        "year15" => "15 周年披风",
        "follower" => "追随者披风",
        _ => string.IsNullOrEmpty(Alias) ? "披风" : Alias,
    };
}

public sealed class Account
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>游戏内显示的名字。</summary>
    public string Name { get; set; } = "";

    /// <summary>32 位无连字符的 UUID（游戏里就是这样用的）。</summary>
    public string Uuid { get; set; } = "";

    public string AccessToken { get; set; } = "";

    public string RefreshToken { get; set; } = "";

    public string Xuid { get; set; } = "";

    public AccountType Type { get; set; } = AccountType.Offline;

    /// <summary>第三方验证服务器的地址（authlib-injector）。</summary>
    public string Server { get; set; } = "";

    public DateTime? ExpiresAt { get; set; }

    public DateTime LastUsed { get; set; } = DateTime.Now;

    // ── 外观（皮肤 / 披风）─────────────────────────────────
    //
    // 只有微软正版账号有这套东西：离线账号压根没有档案，第三方账号的材质挂在
    // authlib-injector 那套验证体系上，走的不是 Minecraft 服务，这里读不到。
    // 所以下面这些字段对 Offline / ThirdParty 恒为空。

    /// <summary>当前激活皮肤的纹理地址。</summary>
    public string? SkinUrl { get; set; }

    /// <summary>当前激活皮肤的模型：CLASSIC / SLIM。</summary>
    public string? SkinVariant { get; set; }

    /// <summary>当前激活披风的纹理地址；没戴披风时为 null。</summary>
    public string? CapeUrl { get; set; }

    /// <summary>这个账号拥有的全部皮肤（含未激活的）。null 表示从没拉取过档案。</summary>
    public List<SkinInfo>? Skins { get; set; }

    /// <summary>这个账号拥有的全部披风（含未激活的）。null 表示从没拉取过档案。</summary>
    public List<CapeInfo>? Capes { get; set; }

    /// <summary>这个账号能不能管理皮肤 / 披风。目前只有微软正版账号可以。</summary>
    [JsonIgnore]
    public bool SupportsAppearance => Type == AccountType.Microsoft;

    // ── 本地皮肤库 ──────────────────────────────────────────
    //
    // 离线账号没有服务端档案，材质挂在哪纯看本地：这里记的是「这个账号想用本地
    // 皮肤库里哪一张」。微软账号也有这么一份，用来在没拉到档案时先显示个样子。
    // 它们是 Identity 性质的数据，必须跟着 accounts.json 持久化，所以不能 [JsonIgnore]。

    /// <summary>绑定的本地皮肤库条目 ID（official-steve-offline / official-alex-… / custom-…）。</summary>
    public string? LocalSkinId { get; set; }

    /// <summary>本地皮肤对应的手臂模型：CLASSIC / SLIM。空 = 跟着条目自己。</summary>
    public string? LocalSkinArmModel { get; set; }

    [JsonIgnore]
    public string TypeLabel => Type switch
    {
        AccountType.Offline => "离线登录",
        AccountType.Microsoft => "微软正版",
        AccountType.ThirdParty => "第三方验证",
        _ => "未知",
    };

    [JsonIgnore]
    public bool IsExpired => ExpiresAt is { } time && time <= DateTime.Now;

    /// <summary>是否还能直接用（离线账号永远有效）。</summary>
    [JsonIgnore]
    public bool CanUseDirectly => Type != AccountType.Offline && !IsExpired && !string.IsNullOrEmpty(AccessToken);

    /// <summary>
    /// 离线账号的占位令牌。
    ///
    /// CmlLib 的 <c>MLaunchOption.CheckValid()</c> 见到空的 accessToken 会直接抛
    /// ArgumentException("Invalid session")，而离线账号本来就没有真令牌——
    /// 缺了它离线启动会 100% 失败。游戏自己不看这个值：单机从不联网校验，
    /// 离线服也不校验，所以给个固定的占位串最省事。
    /// </summary>
    public const string OfflineToken = "00000000000000000000000000000000";

    /// <summary>
    /// 投影成库要的会话对象。这里会把离线账号缺的字段补齐，
    /// 保证交出去的 MSession 一定能通过 CheckValid()。
    /// </summary>
    public MSession ToSession()
    {
        var offline = Type == AccountType.Offline;

        return new MSession
        {
            Username = Name,

            // 离线身份靠名字算 UUID；同一个名字在哪台机器上都是同一个身份，
            // 不能像库的 CreateOfflineSession 那样每次随机一个，否则存档会对不上人。
            UUID = offline && string.IsNullOrEmpty(Uuid) ? OfflineUuid.ForName(Name) : Uuid,

            AccessToken = offline ? OfflineToken : AccessToken,

            // 三个取值是有讲究的：legacy 走老式 Yggdrasil、mojang 走标准 Yggdrasil
            //（外置登录属于这类）、msa 是微软。
            // 外置登录填成 legacy 会让客户端去连官方验证服务器，等于白做。
            UserType = Type switch
            {
                AccountType.Microsoft => "msa",
                AccountType.ThirdParty => "mojang",
                _ => "legacy",
            },

            Xuid = Xuid,
        };
    }
}

/// <summary>Java 的 UUID.nameUUIDFromBytes("OfflinePlayer:" + name) 等价实现。</summary>
public static class OfflineUuid
{
    private static readonly MD5 Md5 = MD5.Create();

    public static string ForName(string name)
    {
        byte[] hash;
        lock (Md5) hash = Md5.ComputeHash(Encoding.UTF8.GetBytes("OfflinePlayer:" + name));

        hash[6] = (byte)((hash[6] & 0x0F) | 0x30);   // version 3
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);   // IETF variant

        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

/// <summary>账号列表的持久化。</summary>
public sealed class AccountStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    public List<Account> Accounts { get; private set; } = new();

    public string? SelectedId { get; private set; }

    public event EventHandler? Changed;

    public Account? Selected =>
        Accounts.FirstOrDefault(a => a.Id == SelectedId) ?? Accounts.FirstOrDefault();

    public void Load()
    {
        try
        {
            if (!File.Exists(AppPaths.AccountsFile)) return;
            var model = JsonSerializer.Deserialize<AccountsFile>(
                File.ReadAllText(AppPaths.AccountsFile), Json);
            if (model is null) return;
            Accounts = model.Accounts ?? new List<Account>();
            SelectedId = model.SelectedId;
        }
        catch
        {
            Accounts = new List<Account>();
            SelectedId = null;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Save()
    {
        AppPaths.EnsureCreated();
        var model = new AccountsFile { Accounts = Accounts, SelectedId = SelectedId };
        var temp = AppPaths.AccountsFile + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(model, Json));
        File.Move(temp, AppPaths.AccountsFile, overwrite: true);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public Account Add(Account account)
    {
        Accounts.RemoveAll(a => a.Type == account.Type
                                && a.Name.Equals(account.Name, StringComparison.OrdinalIgnoreCase));
        Accounts.Add(account);
        SelectedId = account.Id;
        Save();
        return account;
    }

    public void Select(string id)
    {
        if (Accounts.All(a => a.Id != id)) return;
        SelectedId = id;
        var account = Accounts.First(a => a.Id == id);
        account.LastUsed = DateTime.Now;
        Save();
    }

    public void Remove(string id)
    {
        Accounts.RemoveAll(a => a.Id == id);
        if (SelectedId == id) SelectedId = Accounts.FirstOrDefault()?.Id;
        Save();
    }

    public void Update(Account account)
    {
        var index = Accounts.FindIndex(a => a.Id == account.Id);
        if (index >= 0) Accounts[index] = account;
        Save();
    }

    private sealed class AccountsFile
    {
        public List<Account>? Accounts { get; set; }
        public string? SelectedId { get; set; }
    }
}
