using System.Windows.Threading;
using LMCL.Core.Online;

namespace LMCL.App.Services;

/// <summary>
/// 大厅挂载会话：本机正挂在大厅里的房间（id + token）+ 心跳续期。
/// 做成静态是因为创建房间的窗口关掉之后房间还开着，心跳和退出时的下架都得有人管。
/// </summary>
public static class LobbySession
{
    /// <summary>当前房间（虚拟网里的那间，无论有没有挂大厅）。弹窗关了也还在。</summary>
    public sealed record RoomInfo(string Code, bool IsHost, string Title);

    /// <summary>当前挂载：null = 没挂。</summary>
    public static (string Id, string Token)? Ticket { get; private set; }

    /// <summary>本机当前的房间：null = 不在房间里。</summary>
    public static RoomInfo? Room { get; private set; }

    private static DispatcherTimer? _heartbeat;

    /// <summary>房间创建 / 加入成功后登记，入口页的「当前房间」卡靠它显示。</summary>
    public static void SetRoom(string code, bool isHost, string title) =>
        Room = new RoomInfo(code, isHost, title);

    /// <summary>挂上大厅后开始心跳；重复挂载会先覆盖旧的。</summary>
    public static void Mount(string id, string token)
    {
        Ticket = (id, token);
        StartHeartbeat();
    }

    /// <summary>有没有正在心跳的挂载（公开房间的标志）。</summary>
    public static bool IsMounted => Ticket is not null;

    private static void StartHeartbeat()
    {
        StopHeartbeat();

        _heartbeat = new DispatcherTimer { Interval = TimeSpan.FromSeconds(240) };
        _heartbeat.Tick += async (_, _) =>
        {
            if (Ticket is not { } ticket) { StopHeartbeat(); return; }
            try
            {
                var seconds = await CampfireLobby.HeartbeatAsync(ticket.Id, ticket.Token, players: 1)
                    .ConfigureAwait(true);
                _heartbeat!.Interval = TimeSpan.FromSeconds(seconds);
            }
            catch
            {
                // 一次心跳失败不撤销挂载；连续失败服务端过几分钟自己会摘掉
            }
        };
        _heartbeat.Start();
    }

    private static void StopHeartbeat()
    {
        _heartbeat?.Stop();
        _heartbeat = null;
    }

    /// <summary>从大厅下架，但房间（虚拟网）继续开着 —— 「取消公开」走这里。</summary>
    public static void Unpublish()
    {
        StopHeartbeat();
        if (Ticket is not { } ticket) return;
        Ticket = null;
        _ = Task.Run(() => CampfireLobby.CloseAsync(ticket.Id, ticket.Token));
    }

    /// <summary>退出房间：下架大厅 + 清掉房间状态。</summary>
    public static void Unmount()
    {
        Unpublish();
        Room = null;
    }

    /// <summary>
    /// 关启动器时下架大厅房间。挂在房间里的房间不下架的话，
    /// 别人会看到一个永远加入不了的「幽灵房间」。
    /// </summary>
    public static void UnpublishOnExit()
    {
        Room = null;
        Unpublish();
    }
}
