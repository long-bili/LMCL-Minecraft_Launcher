using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LMCL.App.Services;
using LMCL.App.Views.Pages.Multiplayer;
using LMCL.Core.Configuration;
using LMCL.Core.EasyTier;
using LMCL.Core.Minecraft;
using LMCL.Core.Online;

namespace LMCL.App.Views.Pages;

/// <summary>
/// 多人联机：版式照 Campfire（GPL-3.0）的 LinkPage / LobbyPage / LobbyRoomPage /
/// RecentPage / SettingsPage 重排成 LMCL 卡片风。页内五态：加入房间 → 进行中 →
/// 房主房间 / 客人房间，外加公开设置；联机大厅、最近房间、联机设置是带返回键的子页。
/// 房间状态是全局的（LobbySession），虚拟网由 MultiplayerNetwork / EasyTierService 管。
/// </summary>
public partial class MultiplayerPage : PageBase
{
    /// <summary>ShellWindow 退出时下架大厅房间；实际状态在 LobbySession。</summary>
    internal static void UnpublishOnExit() => LobbySession.UnpublishOnExit();

    // ── 状态 ────────────────────────────────────────────────

    private enum WorkKind { Host, Guest }

    private WorkKind _workKind;

    /// <summary>进行中流程的取消源；完成或取消后置空。</summary>
    private System.Threading.CancellationTokenSource? _workCts;

    /// <summary>房主房间当前用的邀请码（关房前一直有效）。</summary>
    private RoomCode? _hostCode;

    /// <summary>加入的房间邀请码。</summary>
    private RoomCode? _guestCode;

    /// <summary>大厅清单缓存（搜索 / 版本过滤在本地做）。</summary>
    private LobbyList? _lobbyList;

    private bool _lobbyLoading;

    private DateTime _lobbyCooldownUntil = DateTime.MinValue;

    private DateTime _loadedAt = DateTime.MinValue;

    /// <summary>点进详情的那间房。</summary>
    private LobbyRoom? _detailRoom;

    /// <summary>设置区回填中，别把回填当用户改动存进去。</summary>
    private bool _fillingSettings;

    /// <summary>轮询房间状态的计时器（房主 / 客人页时每 2 秒）。</summary>
    private System.Timers.Timer? _poll;

    /// <summary>本次会话里公开房间时设置的密码（服务端读不回来，自己记住以便重公开时回填）。</summary>
    private string _publishedPassword = "";

    /// <summary>本机 Minecraft 局域网世界 / 服务器的端口（自动检测或手动填）。null = 还没设。</summary>
    private int? _gamePort;

    /// <summary>手动填端口的回执：确定 / 跳过 / 取消都会在这里放结果。</summary>
    private TaskCompletionSource<int?>? _portTcs;

    public MultiplayerPage()
    {
        InitializeComponent();
    }

    public override void OnNavigatedTo()
    {
        LoadSettings();
        RefreshRoomCard();

        // 房间还开着（页面切走又切回来）→ 直接回到房间页
        if (LobbySession.Room is { } room)
        {
            if (room.IsHost) EnterHostPane(restore: true);
            else EnterGuestPane(restore: true);
        }
        else
        {
            ActivatePane("hub");
        }

        StartPolling();
    }

    public override void OnNavigatedFrom()
    {
        // 离开页面就停止轮询，但**不**停 EasyTier —— 用户可能已经联上机了
        _poll?.Stop();
        _poll?.Dispose();
        _poll = null;
    }

    // ── 导航 ────────────────────────────────────────────────

    private static readonly Dictionary<string, string> PaneTitles = new()
    {
        ["join"] = "加入房间",
        ["working"] = "多人联机",
        ["host"] = "我的房间",
        ["guest"] = "我的房间",
        ["publish"] = "公开到联机大厅",
        ["lobby"] = "联机大厅",
        ["roomdetail"] = "房间详情",
        ["recent"] = "最近房间",
        ["settings"] = "联机设置",
        ["repair"] = "修复工具",
    };

    private void ActivatePane(string key)
    {
        var hub = key == "hub";
        PaneHub.Visibility = hub ? Visibility.Visible : Visibility.Collapsed;
        PaneJoin.Visibility = key == "join" ? Visibility.Visible : Visibility.Hidden;
        PaneWorking.Visibility = key == "working" ? Visibility.Visible : Visibility.Hidden;
        PaneHost.Visibility = key == "host" ? Visibility.Visible : Visibility.Hidden;
        PaneGuest.Visibility = key == "guest" ? Visibility.Visible : Visibility.Hidden;
        PanePublish.Visibility = key == "publish" ? Visibility.Visible : Visibility.Hidden;
        PaneLobby.Visibility = key == "lobby" ? Visibility.Visible : Visibility.Hidden;
        PaneRoomDetail.Visibility = key == "roomdetail" ? Visibility.Visible : Visibility.Hidden;
        PaneRecent.Visibility = key == "recent" ? Visibility.Visible : Visibility.Hidden;
        PaneSettings.Visibility = key == "settings" ? Visibility.Visible : Visibility.Hidden;
        PaneRepair.Visibility = key == "repair" ? Visibility.Visible : Visibility.Hidden;

        BtnBack.Visibility = hub ? Visibility.Collapsed : Visibility.Visible;
        HeaderTitle.Text = hub ? "多人联机" : PaneTitles.GetValueOrDefault(key, "多人联机");

        switch (key)
        {
            case "join":
                PrepareJoinPane();
                break;
            case "lobby":
                _ = LoadLobbyAsync();
                break;
            case "recent":
                ReloadRecent();
                break;
            case "settings":
                LoadSettings();
                break;
            case "repair":
                PaneRepair.OnNavigatedTo();
                break;
        }
    }

    private void HubTile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string key }) return;
        switch (key)
        {
            case "create": _ = StartHostAsync(); break;
            case "join": ActivatePane("join"); break;
            default: ActivatePane(key); break;
        }
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        // 房间详情回大厅；公开设置回房间；其余回入口
        if (PaneRoomDetail.Visibility == Visibility.Visible)
            ActivatePane("lobby");
        else if (PanePublish.Visibility == Visibility.Visible)
            ActivatePane(LobbySession.Room?.IsHost == true ? "host" : "hub");
        else
            ActivatePane("hub");
    }

    private void GoLobby_Click(object sender, RoutedEventArgs e) => ActivatePane("lobby");

    private void ResumeRoom_Click(object sender, RoutedEventArgs e)
    {
        if (LobbySession.Room is { } room)
        {
            if (room.IsHost) EnterHostPane(restore: true);
            else EnterGuestPane(restore: true);
        }
    }

    // ── 加入房间（PanJoin） ─────────────────────────────────

    private void PrepareJoinPane()
    {
        // 剪贴板里如果有邀请码，直接替用户填上
        try
        {
            if (JoinCodeBox.Text.Length == 0 && RoomCode.Find(Clipboard.GetText()) is { } code)
                JoinCodeBox.Text = code.Code;
        }
        catch (Exception)
        {
            // 剪贴板可能被别的进程占着，无所谓
        }
        JoinCodeBox_TextChanged(null!, null!);
    }

    private void JoinCodeBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (BtnJoinConfirm is null) return;
        var parsed = TryParseCode(JoinCodeBox.Text);
        BtnJoinConfirm.IsEnabled = parsed is not null;
        LabJoinCodeState.Text = JoinCodeBox.Text.Length == 0
            ? "支持粘贴含邀请码的整段消息。"
            : parsed is not null
                ? $"已识别邀请码 {parsed.Code}"
                : "还没认出邀请码。它长这样：U/1A2B-3C4D-5E6F-7G8H";
    }

    private void JoinCodeBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && BtnJoinConfirm.IsEnabled) _ = StartGuestAsync(TryParseCode(JoinCodeBox.Text));
    }

    private void Paste_Click(object sender, RoutedEventArgs e)
    {
        try { JoinCodeBox.Text = Clipboard.GetText(); }
        catch (Exception) { /* 剪贴板占用就算了 */ }
    }

    private void JoinConfirm_Click(object sender, RoutedEventArgs e)
        => _ = StartGuestAsync(TryParseCode(JoinCodeBox.Text));

    private static RoomCode? TryParseCode(string text) =>
        string.IsNullOrWhiteSpace(text) ? null : RoomCode.Find(text);

    // ── 进行中（PanWorking） ────────────────────────────────

    private sealed class StepRow : INotifyPropertyChanged
    {
        private string _detail = "";
        private string _state = "Pending";

        public StepRow(string title) => Title = title;

        public event PropertyChangedEventHandler? PropertyChanged;

        public string Title { get; }

        public string Detail { get => _detail; set => Set(ref _detail, value); }

        /// <summary>Pending / Active / Done（XAML 触发器按这个切换图标）。</summary>
        public string State { get => _state; set => Set(ref _state, value); }

        private void Set<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    private List<StepRow> _steps = new();

    private StepRow Step(int index) => _steps[index];

    private void ShowWorking(WorkKind kind)
    {
        _workKind = kind;
        _steps = kind == WorkKind.Host
            ? new List<StepRow> { new("准备联机组件"), new("创建房间"), new("连接联机网络"), new("等待对局域网开放") }
            : new List<StepRow> { new("准备联机组件"), new("连接联机网络"), new("寻找房主"), new("进入房间") };
        WorkSteps.ItemsSource = _steps;
        WorkProgress.Value = 0;
        WorkProgress.Visibility = Visibility.Collapsed;
        WorkHint.Visibility = Visibility.Collapsed;
        WorkPortPanel.Visibility = Visibility.Collapsed;
        BtnWorkCancel.IsEnabled = true;
        BtnWorkCancel.Content = "取消";
        ActivatePane("working");
    }

    private void FailWorking(string message, string? hint = null)
    {
        LabWorkHint.Text = hint is null ? message : $"{message}\n\n{hint}";
        WorkHint.Visibility = Visibility.Visible;
        BtnWorkCancel.Content = "返回";
        _workCts?.Dispose();
        _workCts = null;
    }

    private void WorkCancel_Click(object sender, RoutedEventArgs e)
    {
        var wasWorking = _workCts is not null;
        _workCts?.Cancel();
        _workCts?.Dispose();
        _workCts = null;

        if (wasWorking && S.EasyTier.IsRunning)
        {
            // 半路上取消：把刚起的虚拟网也收掉
            _ = Task.Run(S.EasyTier.Stop);
        }
        ActivatePane("hub");
    }

    /// <summary>玩家名：设置里有用设置的，否则用当前账号名；两者都没有就去设置页。</summary>
    private string? ResolvePlayerName()
    {
        var name = S.Config.Current.MpPlayerName is { Length: > 0 } saved
            ? saved
            : S.Accounts.Selected?.Name ?? "";
        if (name.Length == 0)
        {
            Ui.Info("创建或加入房间前，先在「联机设置 → 房间」里填一个玩家名称。");
            ActivatePane("settings");
            return null;
        }
        return name;
    }

    /// <summary>房主：建码 → 起虚拟网。fixedCode 传「重新开房」时沿用旧码。</summary>
    private async Task StartHostAsync(string? fixedCode = null)
    {
        if (_workCts is not null) return;

        if (LobbySession.Room is not null)
        {
            Ui.Info("已经在房间里了。请先关闭当前房间，再开新的。");
            return;
        }

        var playerName = ResolvePlayerName();
        if (playerName is null) return;

        var ct = (_workCts = new System.Threading.CancellationTokenSource()).Token;
        var code = fixedCode is null ? null : RoomCode.Find(fixedCode);
        ShowWorking(WorkKind.Host);

        try
        {
            // 步骤 1：联机组件
            var step0 = Step(0);
            step0.State = "Active";
            if (!EasyTierService.IsInstalled)
            {
                WorkProgress.Visibility = Visibility.Visible;
                var installed = await S.EasyTier.EnsureInstalledAsync(new Progress<double>(r =>
                {
                    step0.Detail = $"{r * 100:0}%";
                    WorkProgress.Value = r;
                })).ConfigureAwait(true);
                if (!installed)
                {
                    FailWorking("联机组件下载失败，可稍后重试（网络原因）。",
                        "请检查网络连接。如果正在使用代理或加速器，可以尝试关闭后重试。");
                    return;
                }
            }
            step0.Detail = "已就绪";
            step0.State = "Done";
            if (ct.IsCancellationRequested) return;

            // 步骤 2：生成邀请码
            var step1 = Step(1);
            step1.State = "Active";
            code ??= RoomCode.Create();
            step1.Detail = code.Code;
            step1.State = "Done";
            if (ct.IsCancellationRequested) return;

            // 步骤 3：虚拟网
            var step2 = Step(2);
            step2.State = "Active";
            var result = await MultiplayerNetwork.StartAsync(
                code.NetworkName, code.NetworkSecret, isHost: true, playerName,
                s => step2.Detail = s).ConfigureAwait(true);
            if (!result.Ok)
            {
                FailWorking(result.Error ?? "EasyTier 没能启动。",
                    "常见原因：拒绝了管理员授权。重新创建房间并允许授权即可。");
                return;
            }
            step2.Detail = "已连接";
            step2.State = "Done";

            // 步骤 4：等本机的 Minecraft 世界对局域网开放（照 Campfire 的 Step.World）。
            // 先自动检测 3 秒；检测不到就把手动输入亮在底部，等房主填（或跳过）。
            var step3 = Step(3);
            step3.State = "Active";
            step3.Detail = "正在检测…";
            int? detected = null;
            try
            {
                detected = await LanWorldFinder.WaitForWorldAsync(TimeSpan.FromSeconds(3), ct).ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }

            if (detected is { } port)
            {
                _gamePort = port;
                step3.Detail = $"端口 {port}";
            }
            else
            {
                step3.Detail = "未检测到";
                LabWorkHint.Text = "没有自动找到对局域网开放的世界。";
                WorkHint.Visibility = Visibility.Visible;
                WorkPortPanel.Visibility = Visibility.Visible;
                WorkPortBox.Clear();
                WorkPortBox.Focus();

                // 等房主填端口或跳过；取消建房也要能唤醒这个等待
                var tcs = new TaskCompletionSource<int?>(TaskCreationOptions.RunContinuationsAsynchronously);
                _portTcs = tcs;
                using var reg = ct.Register(() => tcs.TrySetResult(null));
                var chosen = await tcs.Task.ConfigureAwait(true);
                _portTcs = null;
                WorkPortPanel.Visibility = Visibility.Collapsed;
                WorkHint.Visibility = Visibility.Collapsed;
                if (ct.IsCancellationRequested) return;

                if (chosen is { } manual)
                {
                    _gamePort = manual;
                    step3.Detail = $"端口 {manual}";
                }
                else
                {
                    step3.Detail = "未设置，可稍后在房间页填";
                }
            }
            if (ct.IsCancellationRequested) return;
            step3.State = "Done";

            _hostCode = code;
            var title = $"{playerName} 的房间";
            RoomHistory.Record(isHost: true, code.Code, title);
            LobbySession.SetRoom(code.Code, isHost: true, title);
            if (S.Config.Current.MpAutoCopyCode) Ui.Copy(code.Code);

            EnterHostPane(restore: false);
        }
        catch (OperationCanceledException)
        {
            ActivatePane("hub");
        }
        catch (Exception ex)
        {
            FailWorking($"创建房间失败：{ex.Message}");
        }
        finally
        {
            if (_workCts is not null)
            {
                _workCts.Dispose();
                _workCts = null;
            }
        }
    }

    /// <summary>客人：换码 → 起虚拟网。</summary>
    private async Task StartGuestAsync(RoomCode? code)
    {
        if (code is null || _workCts is not null) return;

        if (LobbySession.Room is not null)
        {
            Ui.Info("已经在房间里了。请先退出当前房间，再加入其他房间。");
            return;
        }

        var playerName = ResolvePlayerName();
        if (playerName is null) return;

        var ct = (_workCts = new System.Threading.CancellationTokenSource()).Token;
        ShowWorking(WorkKind.Guest);

        try
        {
            var step0 = Step(0);
            step0.State = "Active";
            if (!EasyTierService.IsInstalled)
            {
                WorkProgress.Visibility = Visibility.Visible;
                var installed = await S.EasyTier.EnsureInstalledAsync(new Progress<double>(r =>
                {
                    step0.Detail = $"{r * 100:0}%";
                    WorkProgress.Value = r;
                })).ConfigureAwait(true);
                if (!installed)
                {
                    FailWorking("联机组件下载失败，可稍后重试（网络原因）。",
                        "请检查网络连接。如果正在使用代理或加速器，可以尝试关闭后重试。");
                    return;
                }
            }
            step0.Detail = "已就绪";
            step0.State = "Done";
            if (ct.IsCancellationRequested) return;

            var step1 = Step(1);
            step1.State = "Active";
            var result = await MultiplayerNetwork.StartAsync(
                code.NetworkName, code.NetworkSecret, isHost: false, playerName,
                s => step1.Detail = s).ConfigureAwait(true);
            if (!result.Ok)
            {
                FailWorking(result.Error ?? "EasyTier 没能启动。",
                    "常见原因：拒绝了管理员授权。重新加入并允许授权即可。");
                return;
            }
            step1.Detail = "已连接";
            step1.State = "Done";

            // 步骤 3「寻找房主」：等第一个同伴出现在虚拟网里
            var step2 = Step(2);
            step2.State = "Active";
            step2.Detail = "最多等 30 秒";
            var found = false;
            for (var i = 0; i < 15 && !ct.IsCancellationRequested; i++)
            {
                var peers = await S.EasyTier.GetPeersAsync().ConfigureAwait(true);
                if (peers.Count > 0) { found = true; break; }
                step2.Detail = $"{i * 2} 秒";
                await Task.Delay(2000, ct).ConfigureAwait(true);
            }
            if (!found && ct.IsCancellationRequested) return;
            step2.Detail = found ? "已找到房主" : "未发现房主，可能还没开房";
            step2.State = "Done";

            var step3 = Step(3);
            step3.State = "Done";

            _guestCode = code;
            RoomHistory.Record(isHost: false, code.Code, "邀请码加入");
            LobbySession.SetRoom(code.Code, isHost: false, "邀请码加入的房间");

            EnterGuestPane(restore: false, foundHost: found);
        }
        catch (OperationCanceledException)
        {
            _ = Task.Run(S.EasyTier.Stop);
            ActivatePane("hub");
        }
        catch (Exception ex)
        {
            FailWorking($"加入房间失败：{ex.Message}");
        }
        finally
        {
            if (_workCts is not null)
            {
                _workCts.Dispose();
                _workCts = null;
            }
        }
    }

    // ── 房主 / 客人房间页 ───────────────────────────────────

    private void EnterHostPane(bool restore)
    {
        _hostCode = RoomCode.Find(LobbySession.Room?.Code ?? "");
        LabHostCode.Text = LobbySession.Room?.Code ?? "";
        LabHostPort.Text = _gamePort is { } port ? port.ToString() : "未检测到";
        HostPortPanel.Visibility = _gamePort is null ? Visibility.Visible : Visibility.Collapsed;
        LabHostLinks.Text = "—";
        LabHostNat.Text = "—";
        HostPlayersList.ItemsSource = Array.Empty<PlayerRow>();
        CardHostPlayers.Visibility = Visibility.Collapsed;
        ActivatePane("host");
        _ = RefreshRoomStatsAsync();
    }

    private void EnterGuestPane(bool restore, bool foundHost = true)
    {
        LabGuestAddress.Text = EasyTierService.HostVirtualIp;
        LabGuestGuide.Text = foundHost
            ? "已加入房间。在游戏里「多人游戏 → 直接连接」，填入房主告知的虚拟 IP 和端口即可进入。"
            : "已连上联机网络，正在寻找房主…连上后从「直接连接」进入房间。";
        LabGuestLatency.Text = "—";
        LabGuestPath.Text = "—";
        LabGuestNat.Text = "—";
        GuestPlayersList.ItemsSource = Array.Empty<PlayerRow>();
        CardGuestPlayers.Visibility = Visibility.Collapsed;
        ActivatePane("guest");
        _ = RefreshRoomStatsAsync();
    }

    private sealed class PlayerRow
    {
        public PlayerRow(string name, string address, string? tag)
        {
            Name = name.Length > 0 ? name : "?";
            Initial = char.IsSurrogate(Name, 0) ? Name[..2] : Name[..1].ToUpperInvariant();
            Address = address;
            Tag = tag;
        }

        public string Name { get; }
        public string Initial { get; }
        public string Address { get; }
        public string? Tag { get; }
        public bool HasTag => Tag is not null;
        public string Detail => Address;
    }

    /// <summary>轮询：虚拟 IP、同伴、NAT、公开状态。房主和客人共用。</summary>
    private async Task RefreshRoomStatsAsync()
    {
        try
        {
            var isHost = LobbySession.Room?.IsHost == true;

            var peers = await S.EasyTier.GetPeersAsync().ConfigureAwait(true);
            var nat = await S.EasyTier.GetNatInfoAsync().ConfigureAwait(true);
            var nodeOk = await EasyTierService.IsSharedNodeReachableAsync().ConfigureAwait(true);

            var natText = nat is null ? "未知"
                : !nat.HasPublicIp ? "无公网出口（严格 NAT）"
                : nat.IsSymmetric ? "Symmetric NAT" : "正常";

            if (isHost)
            {
                LabHostLinks.Text = peers.Count.ToString();
                LabHostNat.Text = natText;
                HostPlayersList.ItemsSource = PlayersWithSelf(peers, selfTag: "房主");
                CardHostPlayers.Visibility = Visibility.Visible;
                HintHostNodes.Visibility = nodeOk ? Visibility.Collapsed : Visibility.Visible;

                // 公开状态
                var mounted = LobbySession.IsMounted;
                DotLobby.Fill = mounted ? Brushes.ForestGreen : (Brush)FindResource("DividerBrush");
                LabLobby.Text = mounted
                    ? (_publishedPassword.Length > 0 ? "已公开到联机大厅 · 需要密码" : "已公开到联机大厅")
                    : "未公开到联机大厅";
                LabRoomSettings.Text = mounted
                    ? (_publishedPassword.Length > 0 ? "已公开 · 需要密码" : "已公开")
                    : "未公开";
                BtnLobbyPublish.Content = mounted ? "取消公开" : "公开到联机大厅";
            }
            else
            {
                LabGuestAddress.Text = EasyTierService.HostVirtualIp;
                var first = peers.FirstOrDefault();
                LabGuestLatency.Text = first is { LatencyMs: >= 0 } p ? $"{p.LatencyMs:0} ms" : "…";
                LabGuestPath.Text = peers.Count == 0 ? "等待房主" : $"{peers.Count + 1} 人在线";
                LabGuestNat.Text = natText;
                GuestPlayersList.ItemsSource = PlayersWithSelf(peers, selfTag: "你");
                CardGuestPlayers.Visibility = peers.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                if (peers.Count > 0)
                    LabGuestGuide.Text = "已加入房间。在游戏里「多人游戏 → 直接连接」，填入房主告知的虚拟 IP 和端口即可进入。";
            }
        }
        catch
        {
            // 轮询失败不打扰：下一轮再来
        }
    }

    private IReadOnlyList<PlayerRow> PlayersWithSelf(IReadOnlyList<EasyTierPeer> peers, string selfTag)
    {
        var rows = new List<PlayerRow>();
        var selfName = S.Config.Current.MpPlayerName is { Length: > 0 } saved ? saved : "我";
        rows.Add(new PlayerRow(selfName, S.EasyTier.IsRunning ? "本机" : "", selfTag));
        foreach (var p in peers)
            rows.Add(new PlayerRow(p.DisplayName, p.LatencyMs >= 0 ? $"{p.LatencyMs:0} ms" : "", null));
        return rows;
    }

    // ── 手动端口（工作面板 + 房主页共用一套校验） ───────────

    private void WorkPort_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) _ = UsePortAsync();
    }

    private void UsePort_Click(object sender, RoutedEventArgs e) => _ = UsePortAsync();

    /// <summary>工作面板里确定端口：先探一下这个端口是不是 Minecraft，是才算数。</summary>
    private async Task UsePortAsync()
    {
        if (_portTcs is null) return;
        if (!TryParsePort(WorkPortBox.Text, out var port)) return;

        BtnUsePort.IsEnabled = false;
        LabWorkHint.Text = $"正在检查端口 {port}…";
        var ok = await LanWorldFinder.IsServerAsync(port, TimeSpan.FromSeconds(2)).ConfigureAwait(true);
        BtnUsePort.IsEnabled = true;

        if (!ok)
        {
            LabWorkHint.Text = $"端口 {port} 上没有找到 Minecraft 局域网世界。对局域网开放后，端口号会显示在游戏的聊天栏里，请核对后重新输入。";
            return;
        }
        _portTcs.TrySetResult(port);
    }

    private void SkipPort_Click(object sender, RoutedEventArgs e)
        => _portTcs?.TrySetResult(null);

    /// <summary>房主页里的手动端口：校验通过就立刻生效。</summary>
    private async void SetHostPort_Click(object sender, RoutedEventArgs e) => await SetHostPortAsync();

    private void HostPort_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) _ = SetHostPortAsync();
    }

    private async Task SetHostPortAsync()
    {
        if (!TryParsePort(HostPortBox.Text, out var port)) return;

        BtnSetHostPort.IsEnabled = false;
        var ok = await LanWorldFinder.IsServerAsync(port, TimeSpan.FromSeconds(2)).ConfigureAwait(true);
        BtnSetHostPort.IsEnabled = true;

        if (!ok)
        {
            Ui.Warn($"端口 {port} 上没有找到 Minecraft 局域网世界。\n\n对局域网开放后，端口号会显示在游戏的聊天栏里，请核对后重新输入。");
            return;
        }

        _gamePort = port;
        LabHostPort.Text = port.ToString();
        HostPortPanel.Visibility = Visibility.Collapsed;
    }

    private static bool TryParsePort(string text, out int port)
    {
        port = 0;
        if (!int.TryParse(text.Trim(), out var value) || value is < 1 or > 65535)
        {
            Ui.Warn("端口要是 1–65535 之间的数字。");
            return false;
        }
        port = value;
        return true;
    }

    private void CopyCode_Click(object sender, RoutedEventArgs e)    {
        if (_hostCode is not { } code) return;
        Ui.Copy(code.Code);
        LabHostTip.Text = "邀请码已复制；请将其发送给其他玩家，并在房间开启期间保持 LMCL 与游戏运行。";
    }

    private void CopyAddress_Click(object sender, RoutedEventArgs e)
        => Ui.Copy(EasyTierService.HostVirtualIp);

    private void RoomSettings_Click(object sender, RoutedEventArgs e) => ActivatePane("publish");

    private void OpenDiagnostics_Click(object sender, RoutedEventArgs e) => ActivatePane("repair");

    private void LobbyPublish_Click(object sender, RoutedEventArgs e)
    {
        if (LobbySession.IsMounted)
        {
            // 取消公开：房间继续开着，只是从大厅摘下来
            LobbySession.Unpublish();
            _ = RefreshRoomStatsAsync();
            return;
        }

        // 预填：房间名 = 「xx 的世界」，介绍 / 密码沿用上次公开时填的
        var playerName = S.Config.Current.MpPlayerName is { Length: > 0 } saved ? saved : "";
        PubTitleBox.Text = S.Config.Current.MpLobbyTitle is { Length: > 0 } t ? t
            : playerName.Length > 0 ? $"{playerName} 的世界" : "";
        PubDescBox.Text = S.Config.Current.MpLobbyDescription ?? "";
        PubPasswordBox.Text = _publishedPassword;
        ChkPubRegion.IsChecked = S.Config.Current.MpShowRegion;
        ActivatePane("publish");
    }

    private void PublishField_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) _ = PublishAsync();
    }

    private async Task PublishAsync()
    {
        var code = _hostCode ?? RoomCode.Find(LobbySession.Room?.Code ?? "");
        if (code is null)
        {
            Ui.Warn("还没有房间可公开。先创建房间。");
            return;
        }

        var title = PubTitleBox.Text.Trim();
        if (title.Length == 0)
        {
            Ui.Warn("请填写房间名称。");
            PubTitleBox.Focus();
            return;
        }

        var config = S.Config.Current;
        config.MpLobbyTitle = title;
        config.MpLobbyDescription = PubDescBox.Text.Trim();
        config.MpShowRegion = ChkPubRegion.IsChecked == true;
        S.Config.Save();

        BtnPublishConfirm.IsEnabled = false;
        BtnPublishConfirm.Content = "正在公开";
        try
        {
            var (id, token) = await CampfireLobby.PublishAsync(
                code, title, config.MpPlayerName is { Length: > 0 } p ? p : "LMCL 玩家",
                version: "", map: "", maxPlayers: 0, description: config.MpLobbyDescription,
                showRegion: config.MpShowRegion,
                password: PubPasswordBox.Text.Trim()).ConfigureAwait(true);

            _publishedPassword = PubPasswordBox.Text.Trim();
            LobbySession.Mount(id, token);
            ActivatePane("host");
            _ = RefreshRoomStatsAsync();
        }
        catch (Exception ex)
        {
            // 公开失败不影响联机本身 —— 邀请码开黑照常能用
            Ui.Warn($"无法公开房间：\n\n{ex.Message}");
        }
        finally
        {
            BtnPublishConfirm.IsEnabled = true;
            BtnPublishConfirm.Content = "公开房间";
        }
    }

    private void PublishConfirm_Click(object sender, RoutedEventArgs e) => _ = PublishAsync();

    private void Favorite_Click(object sender, RoutedEventArgs e)
    {
        var room = LobbySession.Room;
        if (room is null) return;

        var record = RoomHistory.GetAll().FirstOrDefault(r => r.Code == room.Code)
                     ?? new RoomRecord(room.Code, room.IsHost, room.Title, DateTime.Now, false);
        RoomHistory.SetFavorite(record, true);

        if (sender is Button b)
        {
            b.Content = "已收藏";
            b.IsEnabled = false;
        }
    }

    /// <summary>关闭（房主）/ 退出（客人）房间：断开虚拟网**之后**立刻把页面刷回入口。</summary>
    private async void CloseRoom_Click(object sender, RoutedEventArgs e)
    {
        if (!Ui.Confirm("退出后会和虚拟网断开。\n\n" +
                        "如果你正在游戏里联机，连接会中断；房主退出则其他人也会掉线。\n\n" +
                        "确定要退出吗？", "退出房间"))
        {
            return;
        }

        // 先锁按钮，别在断开期间又点一次
        BtnCloseRoom.IsEnabled = BtnGuestLeave.IsEnabled = BtnLeaveRoom.IsEnabled = false;

        LobbySession.Unmount();
        _hostCode = null;
        _guestCode = null;
        _publishedPassword = "";
        _gamePort = null;

        // Stop 内部可能要用管理员权限收尾，放后台等它真的结束再刷新，
        // 不然页面回去了虚拟网还挂着，状态就是错的
        var stop = Task.Run(S.EasyTier.Stop);
        try { await stop.ConfigureAwait(true); }
        catch { /* 停不掉也算退出了 */ }

        BtnCloseRoom.IsEnabled = BtnGuestLeave.IsEnabled = BtnLeaveRoom.IsEnabled = true;
        RefreshRoomCard();
        ActivatePane("hub");
    }

    private void LeaveRoom_Click(object sender, RoutedEventArgs e) => CloseRoom_Click(sender, e);

    // ── 联机大厅（PanLobby，照 Campfire LobbyPage） ─────────

    private System.Windows.Threading.DispatcherTimer? _searchDebounce;

    private void LobbySearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (LobbyRows is null) return;
        // 250ms 防抖，跟 Campfire 一样边打字边过滤
        if (_searchDebounce is null)
        {
            _searchDebounce = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _searchDebounce.Tick += SearchDebounce_Tick;
        }
        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    private void SearchDebounce_Tick(object? sender, EventArgs e)
    {
        _searchDebounce?.Stop();
        RenderLobbyRooms();
    }

    private void LobbyVersion_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (LobbyRows is not null) RenderLobbyRooms();
    }

    private void LobbyRefresh_Click(object sender, RoutedEventArgs e)
    {
        if (DateTime.UtcNow < _lobbyCooldownUntil) return;
        _ = LoadLobbyAsync();
    }

    private void LobbyCreate_Click(object sender, RoutedEventArgs e) => _ = StartHostAsync();

    private async Task LoadLobbyAsync()
    {
        if (_lobbyLoading) return;
        _lobbyLoading = true;
        if (BtnLobbyRefresh is not null) BtnLobbyRefresh.IsEnabled = false;
        SetLobbyState("loading", null);

        try
        {
            var list = await CampfireLobby.ListAsync().ConfigureAwait(true);
            _lobbyList = list;
            _lobbyCooldownUntil = DateTime.UtcNow.AddSeconds(10);
            _loadedAt = DateTime.UtcNow;

            if (LobbyNotice is not null)
            {
                if (list.Notice.Length > 0)
                {
                    LabLobbyNotice.Text = list.Notice;
                    LobbyNotice.Visibility = Visibility.Visible;
                }
                else
                {
                    LobbyNotice.Visibility = Visibility.Collapsed;
                }
            }

            FillLobbyVersions();
            RenderLobbyRooms();
        }
        catch (Exception ex)
        {
            SetLobbyState("error", ex.Message);
        }
        finally
        {
            _lobbyLoading = false;
            UpdateRefreshButton();
        }
    }

    private void UpdateRefreshButton()
    {
        if (BtnLobbyRefresh is null) return;
        var left = _lobbyCooldownUntil - DateTime.UtcNow;
        BtnLobbyRefresh.Content = left > TimeSpan.Zero ? $"刷新（{(int)left.TotalSeconds}）" : "刷新";
        BtnLobbyRefresh.IsEnabled = left <= TimeSpan.Zero && !_lobbyLoading;
    }

    /// <summary>大厅状态行：加载 / 空 / 没匹配 / 出错 / 关闭（文案照 Campfire）。</summary>
    private void SetLobbyState(string state, string? detail)
    {
        if (LabLobbyState is null) return;
        var (text, retry) = state switch
        {
            "loading" => ("正在读取房间列表", false),
            "empty" => ("暂时没有公开的房间。创建房间后，可将其公开到这里。", false),
            "nomatch" => ("没有符合条件的房间。", false),
            "closed" => ("联机大厅暂未开放，仍可凭邀请码联机。", false),
            "error" => ($"无法连接到联机大厅，请检查网络后重试。{detail}", true),
            _ => ("", false),
        };
        PanLobbyState.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        LabLobbyState.Text = text;
        BtnLobbyRetry.Visibility = retry ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>把清单里出现过的版本聚成「全部版本 + 各版本」。</summary>
    private void FillLobbyVersions()
    {
        if (LobbyVersionBox is null) return;
        _fillingSettings = true;
        try
        {
            var selected = LobbyVersionBox.SelectedIndex > 0 ? LobbyVersionBox.SelectedItem as string : null;
            var versions = (_lobbyList?.Rooms ?? Array.Empty<LobbyRoom>())
                .Select(r => r.Version).Where(v => v.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(v => Version.TryParse(v, out var parsed) ? parsed : new Version(0, 0))
                .ToList();
            LobbyVersionBox.ItemsSource = new[] { "全部版本" }.Concat(versions).ToList();
            LobbyVersionBox.SelectedIndex = selected is null ? 0
                : Math.Max(0, versions.FindIndex(v => string.Equals(v, selected, StringComparison.OrdinalIgnoreCase)) + 1);
        }
        finally
        {
            _fillingSettings = false;
        }
    }

    private void RenderLobbyRooms()
    {
        if (LobbyRows is null || _lobbyList is null) return;

        var query = LobbySearchBox.Text.Trim();
        var version = LobbyVersionBox.SelectedIndex > 0 ? LobbyVersionBox.SelectedItem as string : null;

        var rooms = _lobbyList.Rooms.Where(r =>
                (version is null || string.Equals(r.Version, version, StringComparison.OrdinalIgnoreCase)) &&
                (query.Length == 0 ||
                 r.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                 r.Host.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                 r.Map.Contains(query, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        LobbyRows.Items.Clear();
        foreach (var room in rooms)
            LobbyRows.Items.Add(BuildLobbyRow(room));

        LabRoomsHeader.Text = _lobbyList.Enabled && _lobbyList.Rooms.Count > 0
            ? $"公开房间 · {_lobbyList.Rooms.Count} 个"
            : "公开房间";

        if (!_lobbyList.Enabled) SetLobbyState("closed", null);
        else if (_lobbyList.Rooms.Count == 0) SetLobbyState("empty", null);
        else if (rooms.Count == 0) SetLobbyState("nomatch", null);
        else PanLobbyState.Visibility = Visibility.Collapsed;
    }

    private string RoomPills(LobbyRoom room)
    {
        var pills = new List<string>();
        if (room.Locked) pills.Add("需要密码");
        pills.AddRange(room.Tags);
        if (room.Voice) pills.Add("语音");
        return string.Join("　", pills);
    }

    /// <summary>大厅房间行（照 Campfire 的 RoomRow：图标 + 文案 + 右侧人数）。</summary>
    private Button BuildLobbyRow(LobbyRoom room)
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new Border { Style = (Style)FindResource("RoomIconCircle") };
        icon.Child = new TextBlock
        {
            Text = room.Title.Length > 0 ? room.Title[..1].ToUpperInvariant() : "?",
            FontSize = 16,
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)FindResource("TextSecondaryBrush"),
        };
        grid.Children.Add(icon);

        var title = new StackPanel { Orientation = Orientation.Horizontal };
        if (room.Locked)
            title.Children.Add(new TextBlock
            {
                Text = "\uE72E",
                FontFamily = new FontFamily("Segoe Fluent Icons,Segoe MDL2 Assets"),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)FindResource("WarningBrush"),
            });
        title.Children.Add(new TextBlock
        {
            Text = room.Title,
            FontSize = 14,
            Margin = new Thickness(6, 0, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = (Brush)FindResource("TextBrush"),
        });

        var hostText = $"房主 {(room.Host.Length > 0 ? room.Host : "未署名")}";
        var people = new TextBlock
        {
            Margin = new Thickness(0, 3, 0, 0),
            FontSize = 12,
            Foreground = (Brush)FindResource("TextSecondaryBrush"),
            Text = room.Map.Length > 0 ? $"{hostText}　·　地图 {room.Map}" : hostText,
        };

        var pills = new TextBlock
        {
            Margin = new Thickness(0, 5, 0, 0),
            FontSize = 11,
            Foreground = (Brush)FindResource("TextSecondaryBrush"),
            Text = RoomPills(room),
        };

        var text = new StackPanel { Margin = new Thickness(12, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(title);
        text.Children.Add(people);
        text.Children.Add(pills);
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var side = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        side.Children.Add(new TextBlock
        {
            Text = room.Players.ToString(),
            FontSize = 15,
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = HorizontalAlignment.Right,
            Foreground = (Brush)FindResource("TextBrush"),
        });
        side.Children.Add(new TextBlock
        {
            Text = "玩家",
            FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Right,
            Foreground = (Brush)FindResource("TextSecondaryBrush"),
        });
        Grid.SetColumn(side, 2);
        grid.Children.Add(side);

        var row = new Button
        {
            Style = (Style)FindResource("EntryRow"),
            Content = grid,
            Tag = room,
            ToolTip = room.Title,
        };
        row.Click += (_, _) => OpenRoomDetail(room);
        return row;
    }

    private void OpenRoomDetail(LobbyRoom room)
    {
        _detailRoom = room;
        RoomDetailIcon.Text = room.Title.Length > 0 ? room.Title[..1].ToUpperInvariant() : "?";
        RoomDetailLocked.Visibility = room.Locked ? Visibility.Visible : Visibility.Collapsed;
        RoomDetailTitle.Text = room.Title;
        RoomDetailPills.Text = RoomPills(room);
        RoomDetailHost.Text = room.Host.Length > 0 ? room.Host : "未署名";
        RoomDetailVersion.Text = room.Version.Length > 0 ? room.Version : "版本未知";
        RoomDetailPlayers.Text = room.MaxPlayers > 0 ? $"{room.Players}/{room.MaxPlayers}" : room.Players.ToString();
        RoomDetailMap.Text = room.Map.Length > 0 ? room.Map : "—";
        RoomDetailPlace.Text = "—";
        RoomDetailOpened.Text = "—";
        if (room.Description.Length > 0)
        {
            RoomDetailDesc.Text = room.Description;
            RoomDetailDesc.Visibility = Visibility.Visible;
        }
        else
        {
            RoomDetailDesc.Visibility = Visibility.Collapsed;
        }
        RoomDetailPasswordCard.Visibility = room.Locked ? Visibility.Visible : Visibility.Collapsed;
        RoomDetailPasswordBox.Clear();
        ActivatePane("roomdetail");
    }

    private void RoomPassword_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) _ = JoinDetailRoomAsync();
    }

    private void DetailJoin_Click(object sender, RoutedEventArgs e) => _ = JoinDetailRoomAsync();

    private async Task JoinDetailRoomAsync()
    {
        if (_detailRoom is not { } room) return;

        if (LobbySession.Room is not null)
        {
            Ui.Info("请先离开或关闭当前房间，再加入其他房间。");
            return;
        }

        if (room.Locked && RoomDetailPasswordBox.Text.Length == 0)
        {
            Ui.Warn("请输入房间密码。");
            RoomDetailPasswordBox.Focus();
            return;
        }

        BtnDetailJoin.IsEnabled = false;
        try
        {
            var code = await CampfireLobby.GetCodeAsync(room.Id, room.Locked ? RoomDetailPasswordBox.Text : null)
                .ConfigureAwait(true);
            await StartGuestAsync(code).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Ui.Warn($"无法加入：\n\n{ex.Message}\n\n如果房间刚关闭，刷新大厅再试试。");
        }
        finally
        {
            BtnDetailJoin.IsEnabled = true;
        }
    }

    // ── 最近房间（PaneRecent，照 Campfire RecentPage） ──────

    private sealed class RecentRowVM
    {
        public RecentRowVM(RoomRecord model) => Model = model;

        public RoomRecord Model { get; }

        public string Code => Model.Code;

        public string Title => Model.Title.Length > 0 ? Model.Title : "未命名房间";

        public string Role => Model.IsHost ? "我开的" : "加入的";

        public bool IsFavorite => Model.Favorite;

        public string TimeLabel
        {
            get
            {
                var days = (int)(DateTime.Now - Model.LastUsed).TotalDays;
                return days switch
                {
                    0 => "今天",
                    1 => "昨天",
                    < 30 => $"{days} 天前",
                    _ => Model.LastUsed.ToString("yyyy-MM-dd"),
                };
            }
        }
    }

    private void ReloadRecent()
    {
        var all = RoomHistory.GetAll().ToList();
        var favorites = all.Where(r => r.Favorite).ToList();
        var recents = all.Where(r => !r.Favorite).ToList();

        FavRows.Items.Clear();
        foreach (var record in favorites) FavRows.Items.Add(BuildRecentRow(record));
        RecentRows.Items.Clear();
        foreach (var record in recents) RecentRows.Items.Add(BuildRecentRow(record));

        LabFavCount.Text = favorites.Count > 0 ? $"{favorites.Count}" : "";
        LabRecentCount.Text = recents.Count > 0 ? $"{recents.Count}" : "";
        LabNoFav.Visibility = favorites.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        LabNoRecent.Visibility = recents.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static FontFamily IconFontFamily => new("Segoe Fluent Icons,Segoe MDL2 Assets");

    /// <summary>最近房间行：图标 + 标题角色 + 邀请码时间 + 加入/重新开房 + 星标 + 删除。</summary>
    private Button BuildRecentRow(RoomRecord record)
    {
        var vm = new RecentRowVM(record);

        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new Border { Style = (Style)FindResource("RoomIconCircle"), Width = 38, Height = 38 };
        icon.Child = new TextBlock
        {
            Text = record.IsHost ? "\uE710" : "\uE724",
            FontFamily = IconFontFamily,
            FontSize = 14,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)FindResource("AccentBrush"),
        };
        grid.Children.Add(icon);

        var title = new StackPanel { Orientation = Orientation.Horizontal };
        title.Children.Add(new TextBlock
        {
            Text = vm.Title,
            FontSize = 13.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("TextBrush"),
        });
        title.Children.Add(new Border
        {
            Margin = new Thickness(8, 0, 0, 0),
            Padding = new Thickness(5, 0, 5, 0),
            CornerRadius = new CornerRadius(3),
            VerticalAlignment = VerticalAlignment.Center,
            Background = (Brush)FindResource("DividerBrush"),
            Child = new TextBlock
            {
                Text = vm.Role,
                FontSize = 11,
                Foreground = (Brush)FindResource("TextSecondaryBrush"),
            },
        });

        var text = new StackPanel
        {
            Margin = new Thickness(12, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        text.Children.Add(title);
        text.Children.Add(new TextBlock
        {
            Margin = new Thickness(0, 3, 0, 0),
            FontSize = 11.5,
            Foreground = (Brush)FindResource("TextSecondaryBrush"),
            Text = $"{vm.Code}　·　{vm.TimeLabel}",
        });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

        var join = new Button
        {
            Style = (Style)FindResource("SubtleButton"),
            Content = record.IsHost ? "重新开房" : "加入",
            MinWidth = 72,
            Margin = new Thickness(0, 0, 6, 0),
        };
        join.Click += (_, _) => _ = ReenterRoomAsync(record);
        actions.Children.Add(join);

        var star = new Button
        {
            Style = (Style)FindResource("RoundIconBtn"),
            Margin = new Thickness(2, 0, 2, 0),
            Content = new TextBlock
            {
                Text = record.Favorite ? "\uE735" : "\uE734",
                FontFamily = IconFontFamily,
                FontSize = 14,
                Foreground = record.Favorite
                    ? (Brush)FindResource("AccentBrush")
                    : (Brush)FindResource("TextSecondaryBrush"),
            },
            ToolTip = record.Favorite ? "取消收藏" : "收藏",
        };
        star.Click += (_, _) =>
        {
            RoomHistory.SetFavorite(record, !record.Favorite);
            ReloadRecent();
        };
        actions.Children.Add(star);

        var remove = new Button
        {
            Style = (Style)FindResource("RoundIconBtn"),
            Margin = new Thickness(2, 0, 0, 0),
            Content = new TextBlock
            {
                Text = "\uE74D",
                FontFamily = IconFontFamily,
                FontSize = 14,
                Foreground = (Brush)FindResource("TextSecondaryBrush"),
            },
            ToolTip = "删除",
        };
        remove.Click += (_, _) =>
        {
            RoomHistory.Remove(record);
            ReloadRecent();
        };
        actions.Children.Add(remove);

        Grid.SetColumn(actions, 2);
        grid.Children.Add(actions);

        return new Button
        {
            Style = (Style)FindResource("EntryRow"),
            Content = grid,
            ToolTip = vm.Title,
        };
    }

    /// <summary>从最近房间再进：房主沿用原码重新开房，客人走加入流程。</summary>
    private async Task ReenterRoomAsync(RoomRecord record)
    {
        var code = RoomCode.Find(record.Code);
        if (code is null)
        {
            Ui.Warn("这条记录里的邀请码认不出来了。");
            return;
        }

        if (LobbySession.Room is not null)
        {
            Ui.Info("已经在房间里了。请先退出当前房间。");
            return;
        }

        if (record.IsHost) await StartHostAsync(record.Code).ConfigureAwait(true);
        else await StartGuestAsync(code).ConfigureAwait(true);
    }

    private void ClearRecent_Click(object sender, RoutedEventArgs e)
    {
        if (!Ui.Confirm("常用房间与最近房间的记录都会被删除，此操作无法撤销。", "清除房间记录")) return;
        RoomHistory.ClearRecent();
        ReloadRecent();
    }

    // ── 联机设置（PaneSettings，照 Campfire SettingsPage） ──

    /// <summary>内置中转服务器行（照 Campfire 的 ServerRow：勾选 + 地区提供者 + 延迟）。</summary>
    private sealed class ServerRow
    {
        public required SharedNode Node;
        public required CheckBox Check;
        public required TextBlock Caption;
        public required TextBlock Latency;
    }

    private readonly List<ServerRow> _serverRows = new();

    private void BuildServerRows()
    {
        if (PanServers is null || PanServers.Children.Count > 0) return;

        foreach (var node in SharedNodes.All)
        {
            var grid = new Grid { Margin = new Thickness(0, 10, 0, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var check = new CheckBox { HorizontalAlignment = HorizontalAlignment.Left };
            check.Click += ServerCheck_Changed;
            check.Tag = node.Id;
            grid.Children.Add(check);

            // 勾选框右边的说明：地区 · 提供者（照 Campfire，不展示地址）
            var caption = new TextBlock
            {
                Margin = new Thickness(26, 2, 0, 0),
                FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = (Brush)FindResource("TextSecondaryBrush"),
            };
            Grid.SetRow(caption, 1);
            grid.Children.Add(caption);

            var latency = new TextBlock
            {
                Margin = new Thickness(12, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 12,
            };
            Grid.SetColumn(latency, 1);
            grid.Children.Add(latency);

            _serverRows.Add(new ServerRow { Node = node, Check = check, Caption = caption, Latency = latency });
            PanServers.Children.Add(grid);
        }

        SharedNodeTester.Changed += ServerTester_Changed;
        RefreshServerRows();
    }

    private void ServerTester_Changed(object? sender, EventArgs e)
    {
        Dispatcher.Invoke(RefreshServerRows);
    }

    /// <summary>勾选状态 + 延迟文案刷新（照 Campfire 的 UpdateServers：绿 ≤150ms / 橙更慢 / 红 不通）。</summary>
    private void RefreshServerRows()
    {
        if (PanServers is null) return;

        var enabled = S.Config.Current.MpEnabledNodes;
        foreach (var row in _serverRows)
        {
            row.Check.IsChecked = enabled is null || enabled.Contains(row.Node.Id, StringComparer.Ordinal);
            row.Caption.Text = row.Node.Limited
                ? $"{row.Node.Region}　·　{row.Node.Provider}（限速）"
                : $"{row.Node.Region}　·　{row.Node.Provider}";

            if (SharedNodeTester.TryGetLatency(row.Node, out var latency))
            {
                row.Latency.Text = latency is null
                    ? "连不上"
                    : $"{Math.Round(latency.Value)} ms";
                row.Latency.SetResourceReference(TextBlock.ForegroundProperty,
                    latency is null ? "WarningBrush" : "TextBrush");
            }
            else
            {
                row.Latency.Text = SharedNodeTester.IsTesting ? "测试中…" : "未测试";
                row.Latency.SetResourceReference(TextBlock.ForegroundProperty, "TextTertiaryBrush");
            }
        }

        if (LabNodesTest is not null)
        {
            LabNodesTest.Text = SharedNodeTester.IsTesting
                ? "正在测试本机与各服务器之间的延迟…"
                : SharedNodeTester.TestedAt is { } at
                    ? $"测试于 {at:HH:mm}"
                    : "勾掉不需要的服务器可以少一条转发路径；双方须至少启用一个相同的服务器。";
        }
    }

    private void ServerCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_fillingSettings || (sender as FrameworkElement)?.Tag is not string id) return;

        // 从「默认全选」切到显式清单：先按当前界面勾选状态铺底
        var current = S.Config.Current.MpEnabledNodes
                      ?? _serverRows.Where(r => r.Check.IsChecked == true).Select(r => r.Node.Id).ToList();

        if (((CheckBox)sender).IsChecked == true)
        {
            if (!current.Contains(id, StringComparer.Ordinal)) current.Add(id);
        }
        else
        {
            current.RemoveAll(i => string.Equals(i, id, StringComparison.Ordinal));
        }

        S.Config.Current.MpEnabledNodes = new List<string>(current);
        S.Config.Save();
    }

    private void LoadSettings()
    {
        if (SetPlayerNameBox is null) return;
        _fillingSettings = true;
        try
        {
            var config = S.Config.Current;
            SetPlayerNameBox.Text = config.MpPlayerName ?? "";
            ChkAutoCopy.IsChecked = config.MpAutoCopyCode;
            ChkShowRegion.IsChecked = config.MpShowRegion;
            SetNodesBox.Text = string.Join("\n", S.Config.Current.MpSharedNodes ?? new List<string>());
        }
        finally
        {
            _fillingSettings = false;
        }
        BuildServerRows();
    }

    private void PlayerName_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_fillingSettings) return;
        S.Config.Current.MpPlayerName = SetPlayerNameBox.Text.Trim();
        S.Config.Save();
    }

    private void RoomFlag_Changed(object sender, RoutedEventArgs e)
    {
        if (_fillingSettings || ChkAutoCopy is null) return;
        S.Config.Current.MpAutoCopyCode = ChkAutoCopy.IsChecked == true;
        S.Config.Current.MpShowRegion = ChkShowRegion.IsChecked == true;
        S.Config.Save();
    }

    private void Nodes_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_fillingSettings) return;
        var nodes = SetNodesBox.Text
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(n => n.Contains("://")).ToList();
        S.Config.Current.MpSharedNodes = nodes;
        S.Config.Save();
        LabNodesTest.Text = nodes.Count > 0 ? $"已保存 {nodes.Count} 个自定义节点" : "已恢复为公共节点";
    }

    private async void TestNodes_Click(object sender, RoutedEventArgs e)
    {
        if (SharedNodeTester.IsTesting) return;

        // 组件没装也能测（纯 TCP 连接计时），不用等下载
        BtnTestNodes.IsEnabled = false;
        await SharedNodeTester.TestAsync(SharedNodes.All).ConfigureAwait(true);
        BtnTestNodes.IsEnabled = true;
    }

    private void OpenData_Click(object sender, RoutedEventArgs e) => Ui.OpenFolder(AppPaths.LauncherRoot);

    private void OpenLogs_Click(object sender, RoutedEventArgs e) => Ui.OpenFolder(AppPaths.LogDir);

    private void RemoveComponent_Click(object sender, RoutedEventArgs e)
    {
        if (!Ui.ConfirmDanger("移除后下次联机会重新下载（约 14 MB）。确定移除联机组件吗？", "移除联机组件")) return;

        Task.Run(() =>
        {
            try
            {
                if (S.EasyTier.IsRunning) S.EasyTier.Stop();
                if (Directory.Exists(AppPaths.EasyTierDir))
                    Directory.Delete(AppPaths.EasyTierDir, recursive: true);
            }
            catch (Exception ex)
            {
                Ui.Error($"移除失败：\n\n{ex.Message}");
            }
        });
    }

    // ── 入口页房间卡 ────────────────────────────────────────

    private void RefreshRoomCard()
    {
        if (RoomCard is null) return;
        if (LobbySession.Room is not { } room)
        {
            RoomCard.Visibility = Visibility.Collapsed;
            return;
        }

        RoomCard.Visibility = Visibility.Visible;
        RoomCardIcon.Text = room.IsHost ? "\uE710" : "\uE724";
        RoomCardTitle.Text = room.Title;
        RoomCardCode.Text = room.Code;
        RoomCardState.Text = LobbySession.IsMounted ? "已公开到联机大厅" : (room.IsHost ? "房主" : "成员");
    }

    // ── 轮询 ────────────────────────────────────────────────

    private void StartPolling()
    {
        if (_poll is not null) return;
        _poll = new System.Timers.Timer(2000);
        var deadTicks = 0;
        _poll.Elapsed += async (_, _) =>
        {
            if (LobbySession.Room is null) { deadTicks = 0; return; }

            // 房间还登记着但虚拟网已经没了（EasyTier 崩了 / 被外部关掉）：
            // 连续几拍都这样就判定房间结束，立刻把页面刷回入口，别让用户
            // 对着一个已经死掉的房间页
            if (!S.EasyTier.IsRunning && _workCts is null)
            {
                if (++deadTicks < 3) return;
                deadTicks = 0;
                LobbySession.Unmount();
                _hostCode = null;
                _guestCode = null;
                _publishedPassword = "";
                _gamePort = null;
                await Dispatcher.InvokeAsync(() =>
                {
                    RefreshRoomCard();
                    ActivatePane("hub");
                });
                return;
            }
            deadTicks = 0;

            if (PaneHost.Visibility != Visibility.Visible && PaneGuest.Visibility != Visibility.Visible) return;
            await Dispatcher.InvokeAsync(() => _ = RefreshRoomStatsAsync());
        };
        _poll.Start();
    }

    // ── 自检 ────────────────────────────────────────────────

    /// <summary>
    /// 自检：各面板要能铺出来、导航要真的切得动、离线逻辑（编解码 / 房间记录 /
    /// 行模型）要能跑。网络行为（大厅清单、发布）照规矩在自检里跳过。
    /// </summary>
    internal bool LayoutReady()
    {
        try
        {
            OnNavigatedTo();

            if (PaneJoin is null || PaneWorking is null || PaneHost is null || PaneGuest is null) return false;
            if (PanePublish is null || PaneLobby is null || PaneRoomDetail is null) return false;
            if (PaneRecent is null || PaneSettings is null || PaneRepair is null || PaneHub is null) return false;
            if (JoinCodeBox is null || WorkSteps is null || LabHostCode is null || LobbyRows is null) return false;
            if (BtnBack is null || HeaderTitle is null) return false;
            // 端口检测区（工作面板 + 房主页）要铺齐
            if (WorkPortPanel is null || WorkPortBox is null || HostPortPanel is null || LabHostPort is null) return false;
            if (WorkPortPanel.Visibility != Visibility.Collapsed) return false;

            // 二级导航逐个切：子页要真的亮出来、返回键要出现
            foreach (var key in new[] { "join", "lobby", "recent", "settings", "repair" })
            {
                ActivatePane(key);
                if (BtnBack.Visibility != Visibility.Visible) return false;
                if (PaneHub.Visibility != Visibility.Collapsed) return false;
            }
            ActivatePane("hub");
            if (PaneHub.Visibility != Visibility.Visible || BtnBack.Visibility != Visibility.Collapsed) return false;
            if (HeaderTitle.Text != "多人联机") return false;

            // 邀请码编解码
            var code = RoomCode.Create();
            var parsed = RoomCode.Find(code.Code);
            if (parsed is null || parsed.NetworkName != code.NetworkName || parsed.NetworkSecret != code.NetworkSecret) return false;
            if (RoomCode.Find($"来开黑！房间 {code.Code}，速度！") is not { } noisy || noisy.Code != code.Code) return false;

            // 步骤行 / 玩家行 / 最近房间行的文案
            var step = new StepRow("测试");
            step.State = "Done";
            if (step.State != "Done") return false;
            var player = new PlayerRow("Steve", "12 ms", "房主");
            if (!player.HasTag || player.Initial.Length == 0) return false;
            var recent = new RecentRowVM(new RoomRecord(code.Code, true, "", DateTime.Now, true));
            if (recent.TimeLabel != "今天" || recent.Role != "我开的") return false;

            // 大厅行 / 最近房间行真的建得出来
            var lobbyRow = BuildLobbyRow(new LobbyRoom(
                "id", "测试房间", "小明", "1.21.4", "我的世界",
                new[] { "生存" }, 2, 8, Locked: true, Voice: false, Description: ""));
            if (lobbyRow.Tag is not LobbyRoom) return false;
            var recentRow = BuildRecentRow(new RoomRecord(code.Code, false, "自检", DateTime.Now, false));
            if (recentRow.Content is not Grid) return false;

            // 房间历史落盘
            RoomHistory.Record(isHost: false, code.Code, "自检房间");
            var recorded = RoomHistory.GetAll().Any(r => r.Code == code.Code);
            RoomHistory.Remove(new RoomRecord(code.Code, false, "", DateTime.Now, false));
            if (recorded && RoomHistory.GetAll().Any(r => r.Code == code.Code)) return false;

            // 中转服务器行要按内置清单一张不落地铺出来
            if (_serverRows.Count != SharedNodes.All.Count) return false;
            if (PanServers is null || PanServers.Children.Count != SharedNodes.All.Count) return false;

            // 局域网世界检测的离线逻辑：监听表读得出来（可能为空），
            // 对一个基本不会有人听的端口做 ping 要稳稳地返回 false
            //（LayoutReady 是同步的，这里端口会被拒绝连接、几乎立即返回）
            if (LanWorldFinder.GetJavaListeningPorts() is null) return false;
            if (LanWorldFinder.IsServerAsync(1, TimeSpan.FromMilliseconds(400)).GetAwaiter().GetResult()) return false;

            // 轮询计时器要真的在跑，收尾不留后台计时器
            if (_poll is null || !_poll.Enabled) return false;
            OnNavigatedFrom();
            return _poll is null;
        }
        catch
        {
            return false;
        }
    }
}
