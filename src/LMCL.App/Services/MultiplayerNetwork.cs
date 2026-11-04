using LMCL.Core.EasyTier;

namespace LMCL.App.Services;

/// <summary>
/// 进出虚拟网的共用流程（创建房间 / 加入房间 / 大厅加入三条路都走这里）：
/// 组件没装先下载 → 起虚拟网（房主固定锚点 IP、客人 DHCP 自动分配——这个差别不能省）。
/// 结果用返回值交回，界面文案由调用方自己铺。
/// </summary>
public static class MultiplayerNetwork
{
    private static AppServices S => App.Services;

    /// <summary>进网结果。Ok = 虚拟网已在跑；Error = 给用户看的一句原因。</summary>
    public sealed record JoinResult(bool Ok, string? Error);

    /// <summary>确保组件就位并启动虚拟网。status 在后台线程回调，调用方负责调度回 UI。</summary>
    public static async Task<JoinResult> StartAsync(
        string? name, string secret, bool isHost, string playerName,
        Action<string>? status = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            return new JoinResult(false, "邀请码没认出来。它长这样：U/1A2B-3C4D-5E6F-7G8H");

        // 网络名会被拼进命令行参数，收掉危险字符（正常邀请码到不了这里，纯防御）
        if (name.IndexOfAny(['"', '\\', '\n', '\r']) >= 0)
            return new JoinResult(false, "房间标识里有非法字符。");

        if (!EasyTierService.IsInstalled)
        {
            status?.Invoke("正在下载联机组件…");
            var installed = await S.EasyTier.EnsureInstalledAsync(
                new Progress<double>(r => status?.Invoke($"正在下载联机组件… {r * 100:0}%")))
                .ConfigureAwait(true);
            if (!installed)
                return new JoinResult(false, "联机组件下载失败，可稍后重试（网络原因）。");
        }

        status?.Invoke("正在启动虚拟网卡…（系统会弹管理员授权，请点「是」）");

        // 玩家名传给 --hostname：成员列表里显示「这是谁」。顺手持久化。
        if (playerName.Length > 0 && S.Config.Current.MpPlayerName != playerName)
        {
            S.Config.Current.MpPlayerName = playerName;
            S.Config.Save();
        }

        if (!S.EasyTier.Start(name, secret, isHost, playerName))
        {
            var reason = S.EasyTier.LastError;
            return new JoinResult(false,
                reason is null
                    ? "EasyTier 没能启动。常见原因：拒绝了管理员授权。"
                    : $"EasyTier 没能启动：{reason}");
        }

        return new JoinResult(true, null);
    }
}
