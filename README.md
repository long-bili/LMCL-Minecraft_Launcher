# LMCL

一个用 **C# / WPF** 从零手写的 Minecraft 启动器， Windows 11 原生风格（云母材质 + Fluent 控件）。

**当前版本 2.0.1** ／ 作者 laolong

原生桌面程序 —— 没有 WebView，没有网页套壳，没有 HTML/JS，所有界面都是 XAML + 自写控件模板。

```
dotnet build LMCL.sln                  # 0 警告 0 错误
dotnet run --project src/LMCL.App      # 开发时直接跑
```

```
dotnet publish src/LMCL.App -c Release -o dist
# → dist/LMCL.exe   约 57 MB，自带 .NET 运行时
```

---

## 目录结构

| 路径 | 说明 |
| --- | --- |
| `LMCL.sln` | 解决方案 |
| `src/LMCL.Core` | 与界面无关的核心库：账号、下载安装、启动、Java 识别、镜像策略 |
| `src/LMCL.App` | WPF 界面：外壳窗口、页面、子面板、对话框、主题与控件样式 |
| `tools/LMCL.Probe` | 命令行探针，用来在不开界面的情况下验证各条链路 |
| `dist/` | `dotnet publish -c Release` 的产物（单文件 exe） |

### App 内部

```
src/LMCL.App/
├─ App.xaml / App.xaml.cs        应用入口（含 --self-check 命令行开关）
├─ app.ico                       应用图标（由 _probe/make_icon.py 生成）
├─ GlobalUsings.cs               补上 WPF 项目被省略的 System.IO
├─ Animations/
│   ├─ AniEase.cs                缓动函数族（公式对齐 PCL 的 ModAnimation）+ IEasingFunction 适配
│   └─ Animator.cs               动画入口：AaPageEnter/Exit、AaNumber、AaStack、AaItems、AaPopupOpen…
├─ Themes/
│   ├─ Fluent.Light.xaml         亮色令牌（WinUI 3 真实取值，面片半透明）
│   ├─ Fluent.Dark.xaml          暗色令牌
│   └─ Controls.xaml             控件样式（按钮 / 输入框 / 下拉 / 卡片 / 开关 / 滚动条…）
├─ Services/
│   ├─ AppServices.cs            极简服务容器（刻意不引 DI 框架）
│   ├─ ThemeManager.cs           主题切换，换 MergedDictionaries[0]
│   ├─ WindowEffects.cs          云母材质 / 窗口圆角 / 深色模式
│   ├─ ShellStatus.cs            底部状态条
│   ├─ LogBus.cs                 日志中枢
│   ├─ Ui.cs                     对话框 / 剪贴板 / 打开目录等杂务
│   ├─ IconLoader.cs             搜索结果工程图标的本地缓存
│   ├─ SkinPreviewRenderer.cs    皮肤头部 / 披风背面的像素级裁剪与最近邻放大
│   └─ SelfCheck.cs              把全部界面构造一遍，用于冒烟验证
├─ Converters/
│   └─ BoolVisibleConverter.cs   bool → Visibility
├─ Controls/
│   ├─ LogPanel.xaml             运行日志面板（贴着底部才自动滚）
│   └─ MarqueeBar.cs             不确定进度条
└─ Views/
    ├─ ShellWindow.xaml          云母窗口 + 系统标题栏 + 左侧四项导航
    ├─ AccountWindow.xaml        账号管理（离线 / 微软正版 / 外置登录）+ 皮肤披风 + 本地皮肤库
    ├─ InstallVersionDialog.xaml 安装版本：挑加载器与加载器版本（下载页点一条版本弹出）
    ├─ TextDialog.xaml           只读文本查看窗口
    ├─ MessageDialog.xaml        统一风格的消息框（信息/成功/警告/错误/询问，替掉 Win32 MessageBox）
    ├─ RemoveGameDirDialog.xaml  移除游戏目录的选择框
    └─ Pages/
        ├─ HomePage               主页：概览 → 游戏详情 → 启动面板（钉底部）
        ├─ DownloadPage           下载：左侧六分区导航（游戏版本 / 模组 / 资源包 / 数据包 / 光影 / 整合包）
        ├─ VersionsPage           版本与资源：左列表（含游戏目录切换、每项带「选择」按钮）+ 右详情（概览 / 版本设置 / 资源管理）
        ├─ LogPage                运行日志：整页日志 + 打开目录 / 导出
        ├─ SettingsPage           设置：全局设置（二级分类 + 即时保存）/ 工具 / 帮助
        ├─ InstancePage           版本设置（被 VersionsPage 嵌入）
        ├─ ResourcesPage          资源管理（被 VersionsPage 嵌入）
        ├─ ToolsPage              工具（被 SettingsPage 嵌入）
        ├─ HelpPage               帮助（被 SettingsPage 嵌入）
        └─ PageBase               页面基类：进度上报、异常兜底、嵌入模式
```

核心库里和皮肤、外观相关的新成员：

```
src/LMCL.Core/Accounts/
├─ SkinLibrary.cs              本地皮肤库：Steve / Alex 内置位 + 任意多张自定义（可导入 PNG）
├─ SkinService.cs              服务端皮肤 / 披风：读档案、切换、上传、重置、纹理缓存
├─ SkinRelay.cs                本地皮肤中继：回环 HTTP 冒充 Yggdrasil，把离线皮肤送进游戏
└─ AppPaths.SkinsDir           皮肤库目录（%APPDATA%\LMCL\skins）
```

---

## 界面怎么组织的

导航只有 **五项**：主页 / 下载 / 版本与资源 / 运行日志 / 设置。这是刻意的：

- **主页 = 启动优先。** 布局从上到下是「概览小卡 → **游戏详情** → **启动面板（钉在最下面）**」，最后那一步永远是按启动。一个大按钮直接启动，版本自动取「上次玩过的」，用户完全不用先挑版本。缺账号时顶部出现引导卡，**在原地就能建离线账号**（输名字回车），不用跳窗口；缺版本时一个按钮直达下载。三步骤进度条一眼看出走到哪了。游戏详情是一张两列四行的表（版本名 / 游戏版本 / 加载器 / Java / 资源目录 / 内存 / 游戏目录 / 版本目录），**内存和隔离读的是这个版本自己的配置**，不是全局默认值。
- **版本与资源 = 主从布局。** 左边列版本（顶部带**游戏目录切换下拉 + 浏览**），右边是选中版本的详情，分「概览 / 版本设置 / 资源管理」三栏。原来这三块是三个平级入口，改个内存要跳两页。列表里**每个版本图标底部都叠着一个「选择」小按钮**，点一下就地把它设为首页要启动的版本并自动跳回主页——不用为换版本专门跑到别的页面。当前首页版本在这一行改显「已选」并打「当前」徽标，一眼看得出谁在生效。
- **运行日志 = 独立一整页。** 原来它是主页底部一个 240 高的卡：一跑起来日志往下滚、把主页挤得没地方，想翻旧日志还得先滚到主页最底。现在铺满整个内容区，顶部给「打开日志目录」和「导出日志…」——出问题要发给别人看时，导出比让人自己去 `%APPDATA%` 里翻方便得多。日志面板绑的是全局日志总线，所以任何页面都能看到全部日志。
- **设置 = 三个分栏，全局设置内部再分二级。** 全局设置、工具、帮助收在一页，避免「找清缓存要点两次还得认路」；全局设置里再按「下载 / 外观 / 版本 / 性能 / Java」分类，找东西不用滚一长条。**所有改动即时保存**，没有保存按钮——改一个就落一次盘，只留一个「恢复默认设置」。
- **账号**从标题栏的账号条**点开下拉直接切换**（PCL 的做法：换账号不该开新窗口）；下拉最后一项「管理账号…」进管理窗口，做添加 / 删除 / 设为当前。

`版本设置 / 资源管理 / 工具 / 帮助` 这四页的 XAML 与逻辑一行没改，只是被宿主页面嵌入时调一次 `UseEmbeddedMode()`——它会收掉页面自己的版本选择器（避免两处选择打架）并调整边距，版本由宿主用 `SetVersion()` 喂进去。

---

## 功能

**账号**
- 离线登录（按 `OfflinePlayer:<名字>` 算 MD5，同名在哪台机器都是同一个身份）
- 外置登录 / authlib-injector（Yggdrasil `authenticate`）
  - authlib-injector.jar **自动准备**，启动时自动挂 `-javaagent`，不需要手动填参数
  - 官方发布站有 BMCLAPI 镜像，两边 `sha256` 完全一致，走镜像不降低安全性
- 微软正版登录（已开启）：用内置的官方 `client_id`，走老式 Live 授权码流
  （`login.live.com/oauth20_authorize.srf` + `oauth20_token.srf`）。
  - 因为那个内置应用在 Live 端点上**只**认桌面回调 `https://login.live.com/oauth20_desktop.srf`，
    没法在本机回环自动接码，所以流程是两步：点「打开浏览器登录」→ 在微软页面登录 →
    把地址栏里 `code=` 后面的内容（或整段网址）粘回启动器 → 点「完成登录」。
  - 不需要自己注册 Azure 应用、也不用填 client_id。
  - 登录拿到的刷新令牌只存在本机 `%APPDATA%\LMCL\accounts.json`，过期后启动游戏会自动续期。

**下载与安装**
- 下载页改成「六分区导航」：游戏版本 / 模组 / 资源包 / 数据包 / 光影 / 整合包，切换时同一屏做淡入 + 横向轻移
- 游戏版本：900+ 个版本的清单（含快照与远古版本），可按类型筛选、按关键字搜索；点一条弹出「安装版本」对话框选加载器。清单里冒出的非安装类版本（比如 `local`）会被自动挡掉，不进类型下拉
- 加载器：Forge / NeoForge / Fabric / Quilt，可列出可选版本并标记「推荐」
- 社区资源（模组 / 资源包 / 数据包 / 光影）：Modrinth 搜索 → 点开看版本与更新日志 → 装进指定实例；带上一页 / 下一页翻页，**结果条数单独显示在翻页下面**，图标下载后本地缓存
- **分类筛选**：按内容分类收窄搜索（性能优化 / 科技 / 写实风 / 空岛 …）。分类名严格取自 Modrinth 官方分类表，各资源类型各有一套，切换分区时自动重建；数据包在 Modrinth 上没有分类，那一格整个收起而不是留个空下拉
- **切分区即时刷新**：模组 / 资源包 / 数据包 / 光影 换一个就立刻重搜一屏，不用手动点「重置」
- **光影不按版本分**：光影包是全版本通用的，所以这一格不要求先选实例、也不按游戏版本 / 加载器筛。点「安装」后才弹出对话框选放进哪个版本（也可以手选任意文件夹），装完提示落到哪里
- **模组更新检查**：按已装 jar 的 SHA1 批量问 Modrinth「这是什么版本、有没有新的」，列出「旧 → 新」后一键全部更新；换版本时旧文件会被删掉，避免 mods 里同时躺着新旧两份
- 整合包：Modrinth 搜索 + `.mrpack` 本地导入（含加载器版本与 overrides）
- 版本隔离：每个版本一套独立的 mods / 资源包 / 光影 / 存档（新版本默认开，可全局改默认值、单版本覆盖）
- 安装中断可续传（已下好的文件按大小与 SHA1 跳过）

**皮肤与披风**
- 账号详情页**顶栏**就是入口：头像 + 名字 + 类型 + **「皮肤 / 披风」按钮**；点进去是一整页独立的皮肤管理界面，左栏看当前外观、右栏按账号类型给不同的卡片
  - **微软正版**：`我的皮肤`（上传自己的 PNG（64×64 / 64×32）、在已拥有的皮肤间切换、刷新、恢复默认）+ `我的披风`（在已拥有的披风间切换、取消佩戴）+ `本地皮肤库`
  - **离线账号**：只有 `本地皮肤库`（Steve / Alex + 自己导入的），并说明「只存在本机，别人看不到」。离线账号没有披风，那一块整块不出现
  - **第三方账号**：顶栏那个入口**整块不显示**（材质由那台服务器决定，给个点了白点的按钮不如不给）
- **本地皮肤库支持多张自定义皮肤**：一次可以选多张 PNG 导入，每张都是独立的一张（互不覆盖），点缩略图切换、右上角小红叉删除；正在用的那张被删掉会自动改回 Steve
- 内置的 Steve / Alex **编在程序集里**（不只是放在 exe 旁边的松散文件）——单文件发布不会带上松散文件，只有这样换台电脑才渲染得出来
- 服务端档案：读 `GET /minecraft/profile`；切换皮肤走 PATCH，上传走 PUT，重置走 DELETE，取消披风走 DELETE。全部封在 `Core/Accounts/SkinService`
- 纹理全部下载到本地缓存（`%APPDATA%\LMCL\cache\textures`），断网也画得出来；预览时只取需要的像素块
  （头部 = 脸 8×8 + 帽子第二层 40,8；披风 = 背面 10×16），按整数倍最近邻放大，不插值
- **离线账号选的皮肤会真的出现在游戏里**。启动器在本机起一个回环 HTTP 服务冒充 Yggdrasil（返回用私钥签名的档案 + 皮肤 PNG），再用 authlib-injector 把客户端对 Mojang 的请求改写过去：
  - Minecraft 只认 Mojang 的验证服务器，离线账号没有档案，所以光在启动器里选是没用的——**必须伪造一份档案给它**
  - 只有离线账号走这条路。微软账号的皮肤本来就在 Mojang 那边；第三方账号已经在用 authlib-injector 了，两个 agent 只有一个生效，会同时坏掉两个功能
  - 拿不到组件或中继起不来时**只提示、不阻断启动**；进程退出即释放，不白占端口
- 本地皮肤库：Steve / Alex 两个内置位（优先从本机客户端 jar 里挖，挖不到用**编进程序集**的那两份兜底）+ **任意多张**自己导入的 PNG

**启动**
- 内存、窗口尺寸、全屏、附加 JVM / 游戏参数，全局默认 + 每版本覆盖
- 自动选 Java：优先「刚好满足要求的最小版本」，本机没有就解析已下载的 Mojang 运行时
- 直连服务器（`--server` / `--port`）、快速进入存档（`--quickPlaySingleplayer`）
- 游戏标准输出 / 错误实时回灌到界面日志（子进程 `CreateNoWindow`，不弹 CMD 黑窗）

**资源管理**：mods、资源包、光影、存档的查看、启停（`.disabled` 后缀）、导入、删除、打开目录

**工具**：启动参数查看与复制、日志分析（20+ 条已知崩溃原因）、日志导出、缓存统计与清理、快捷打开各目录、更新检查、系统信息、界面自检

**设置**（全局设置分五类，改动即时保存，另有「恢复默认设置」）：下载（源 / 线程数）、外观、版本（新版本默认独立）、性能（内存）、Java。工具与帮助收在同一页的另外两个分栏里。

**外观**这一档能调的东西：
- **主题**：亮 / 暗 / 跟随系统
- **强调色**：6 套预设，或者选「自定义」用 **RGB 滑杆自己调**——带实时预览色块和 `#RRGGBB` 文本，hover / 按下 / 淡底那几支派生色会跟着主色一起算出来，所以按钮反馈不会丢
- **背景效果**：云母（跟随桌面色调）/ 亚克力（模糊桌面）/ **自定义图片**（PNG / JPG / BMP，可选拉伸铺满 / 铺满裁切 / 完整显示）
- **通透度**：面板底色的不透明程度，调低能把背景透出来
- **背景模糊**：0~40。**自己按像素算的**（降采样 + 可分离盒式模糊），没用 XAML 的 `BlurEffect`——那个依赖像素着色器，在远程桌面 / 虚拟机 / 老显卡上会让整层背景画不出来
- **背景明暗**：0.2×~2.0×。图片模式直接烘进图里；云母 / 亚克力模式盖一层黑或白的半透明膜
- **启动后最小化**

**游戏目录**：在「版本与资源」页顶部下拉切换（自动登记用过的目录，可一键切回），「浏览…」打开文件资源管理器自由选目录，切换后所有页面即时刷新。下拉里**每一项右侧有个移除按钮**（「默认位置」没有），点开后给两个选项、后果写清楚：
- **只从列表移除** —— 磁盘上一个文件不动，以后用「浏览…」还能加回来；
- **同时删除本地的整个文件夹** —— 会先告诉你里面有多少个版本、占多大空间，确认一次后**还要再问一次**才真的删（不可恢复，不进回收站）。游戏运行中会挡下来（文件被占用）。

**动画**（公式对齐 PCL 的 `ModAnimation` 并做了加强；系统关掉「淡化菜单效果」时全部直接到位）：
- 窗口**开关**：启动淡入 + 轻微放大，关闭淡出后退出
- 页面**切换**：旧页快速缩小淡出 → 新页淡入 + 上移 + 放大回弹（三层叠加），两页之间不再硬切
- 页面**内容**：各块依次浮入（缩放弹出 + 上移 + 淡入），列表结果逐个进入
- **数字滚动**：主页统计（已装版本 / 模组 / 资源 / 内存）从 0 滚到目标值
- **控件微动效**：强调按钮悬停**流光扫过**、按下回弹微缩；开关滑块回弹缓动；导航指示条 / 页签下划线从中间展开；卡片列表项悬停抬升

---

## 窗口与观感

- **云母（Mica）/ 亚克力（Acrylic）材质**：`DwmSetWindowAttribute` 设 `SYSTEMBACKDROP_TYPE`（2 = 云母，3 = 亚克力），配合 `WindowChrome.GlassFrameThickness="-1"` 把 DWM 框体铺满整个窗口，再把窗口背景设成全透明，材质才真的透得进来。设置页可三选一（云母 / 亚克力 / 纯色），系统不支持时自动降级成纯色背景（并把结论写进日志）。窗口圆角交给 `CORNER_PREFERENCE`，深色模式交给 `IMMERSIVE_DARK_MODE`。
- **标题栏交给系统**：`CaptionHeight="48"` 让顶部那一条真正成为非客户区，于是**拖拽、双击最大化、Aero Snap（拖到屏幕边缘吸附）、右键系统菜单全是系统原生行为**，不用写一行代码。条里的账号条与窗口按钮靠 `IsHitTestVisibleInChrome="True"` 把点击抢回来。
- **主题令牌用 WinUI 3 的真实取值**，面片是半透明的（卡片 70% 白 / 暗色 5% 白），层次感靠叠加做出来 —— 给成不透明的 `#FBFBFB` 会把云母糊死，界面立刻退回「灰盒子」。
- 控件按 WinUI 规矩：圆角 4 / 卡片 8 / 浮出 6；按钮底面比其它三边深一档；输入框底部一条 2px 强调线聚焦才亮；导航选中项左侧一条 3px 圆角指示条。
- 已知取舍：最大化按钮是自绘的，所以鼠标悬停它**不会**弹出 Win11 的贴靠布局（Snap Layouts）。要那个只能用系统标题栏，代价是没法在标题栏放账号条。

---

## 几个关键设计

### 下载管道：三层策略，每层都有实测依据

所有网络请求共用 `MirrorHttpHandler` 这一个 `DelegatingHandler`。

**① 改写** —— 按规则表把官方地址换成 BMCLAPI 地址。资源对象、maven jar、Forge/NeoForge 安装器、fabric-meta 在 BMCLAPI 上都直连可用。

**② 例外** —— 少数路径直奔官方源，一次都不试镜像。因为 BMCLAPI 的 piston 代理会 302 到限时签名的第三方 CDN，而那个 CDN 不可靠：

| 路径 | 镜像实测 | 官方实测 |
| --- | --- | --- |
| `version_manifest_v2.json` | 3 次请求 2 次直接超时（15~22 秒无响应） | 0.9 秒 |
| `/v1/objects/`（客户端 jar、Mojang 运行时） | 38~350 KB/s 且抖动极大 | 22 MB 的客户端 jar 20 秒下完 |

`/v1/objects/` 最阴的地方是它最终**会返回 200**，所以「失败才回退官方」这种判断根本触发不了 —— 只能眼睁睁地以 40 KB/s 爬完 22 MB。修掉之后同一个安装从「跑十几分钟没完」变成 **12 秒完成**。

**③ 兜底学习** —— 剩下的路径先试镜像，如果发现它把请求转手给了带签名参数的限时地址，就改走官方源，并记住这一类路径以后别再试镜像（首次安装有四千多个资源文件，全靠这一层把 `/assets` 也拉回官方源）。

这里有个容易做错的地方：**不能把「转手」一律当成坏事**。`/maven` 转给的是 `mirrors.cernet.edu.cn` 这种正经镜像节点，那是好事，要照收。两种 302 的区别就在 URL 形态 —— 签名地址一定同时带 `s=`（签名）和 `e=`（过期时间）。

最后，任何一步失败都往下退；官方源也拿不到时会退回镜像那个慢地址 —— 慢总比拿不到强，不能把路走死。

并发默认 8 路 —— BMCLAPI 在高并发下会给 429（它的 `X-RateLimit-Limit-short` 只有 10），这是实测稳的档位。

### 安装前去重

原版资产索引里同一个物理对象会被多个名字引用（1.20.1 有 23 组），文件清单里于是出现两条 `GameFile` 指向同一个 `assets/objects/xx/hash`。并行安装器把它们分给不同线程时，会出现「一个在写、一个在校验」的同路径竞争。`DedupedGameInstaller` 在交给并行安装器之前按目标路径合并，顺带少做一次多余的 SHA1。

### 安装带重试

并行安装器一旦在某个文件上出错，会把整批任务一起废掉并抛 `AggregateException`——哪怕其余 4000 多个文件都好好的。而这类错误多半是瞬时的：杀软正在扫刚下下来的 jar、用户同时开了两个启动器、单个文件超时。`ResilientInstaller` 对 `IOException` / `UnauthorizedAccessException` / `HttpRequestException` / 单文件超时做最多 3 次退避重试；已下好的文件会被跳过，所以重试代价很小。

### Java 选择必须返回具体路径

`MLaunchOption.CheckValid()` 见到空的 `JavaPath` 会直接抛 `ArgumentNullException`。也就是说「本机没有合适的 Java、交给库去下运行时」这条路走不通 —— 库里没有任何地方会替我们补上这个值。所以 `ResolveJavaAsync` 按 用户指定 → 本机合适的 Java → 库解析出的 Mojang 运行时 → 系统默认 Java 的顺序，**一定返回一个存在且非空的路径**。

同一个坑还有第二个入口：Java 列表是缓存的，冷缓存时 `Best()` 返回 null，于是**明明本机有合适的 Java，安装时还是会白下 45 MB 的 Mojang 运行时**（`files` 步骤能看到文件总数从 3641 涨到 4044）。`LocalJavaExtractor` 现在会在缓存为空时先按需扫一次盘再判断。

### 离线账号需要占位令牌

`MLaunchOption.CheckValid()` 同样拒绝空的 `accessToken`（报 `Invalid session`），而离线账号本来就没有真令牌。`Account.ToSession()` 会补上一个固定占位值。

游戏启动后会拿这个占位值去问官方「这个令牌是谁」，当然拿到 401，于是往日志里写一条 `Failed to verify authentication` + `InvalidCredentialsException` —— 这一条是**正常的**，紧接着就能看到 `Setting user: 你的名字`，单机与离线服都不受影响。「工具 → 日志分析」专门识别了这一条并给出说明，免得用户以为出了问题。

`--userType` 的三个取值是有讲究的：`legacy` 走老式 Yggdrasil、`mojang` 走标准 Yggdrasil（外置登录属于这类）、`msa` 是微软。外置登录填成 `legacy` 会让客户端去连官方验证服务器，等于白做。

### 启动：关标准输入必须在 Start() 之后

`Process.StandardInput` 在进程真正起来之前根本没有实体，提前访问会直接抛
`InvalidOperationException("StandardIn has not been redirected")`。曾经把
`process.StandardInput.Close()` 写在 `process.Start()` 前面，结果是**任何版本都启动失败** ——
而且因为异常被上层兜住变成一句错误提示，静态看代码完全看不出问题，只有真启动一次才会暴露。

### 整合包的安装顺序

加载器安装器按自己的规矩给版本目录起名（`forge-47.4.27-1.20.1` 之类），而整合包要求「实例名 = 包名」。所以顺序必须是：**先装加载器 → 改名 → 最后才把包里的文件解进去**。反过来做的话目标目录已经存在，改名会被安全地拒绝。

### 社区资源：一套抽象覆盖所有资源类型（对齐 PCL 的 ResourceSearcher）

早期 Mod 和整合包各写了一个搜索方法， facets 拼装、字段解析、分页逻辑全是两份。
重构后统一成三层，对应 PCL 的 `ResourceProject` / `ResourceVersion` / `ResourceSearcher`：

- **`ResourceProject`**：一次搜索拿到的「这是什么」——平台、类型（Mod / 整合包 / 数据包 / 光影 / 资源包）、
  标题、作者、下载量、图标、支持的游戏版本与加载器、中文化标签。
- **`ResourceVersion`**：一个具体版本——文件名、下载地址、大小、更新日志。
- **`ResourceSearchRequest` / `ResourceSearchResult`**：输入输出。分页位置（offset / total）记在
  result 里，「加载更多」就是拿着同一个 result 再搜一次；`total` 的字段名是
  `total_hits`，不是 `total`（Modrinth v2 就这么返回的，别被文档骗了）。

`DownloadPage` 的模组页和整合包页现在共用这一套，只是 `Type` 不同。只接 Modrinth 是刻意收窄的：
CurseForge 官方 API 要 key，没 key 只能爬网页，随时会坏。

### 动画系统：公式对齐 PCL 的 ModAnimation

`Animator` 的 `Aa*` 命名和缓动公式都是从 PCL 的 `ModAnimation.vb` 搬的
（`t^p` 起步、`1-(1-t)^p` 收尾、`OutBack` 的 `1-(1-t)^p·cos(1.5πt)` 回弹都原样复刻），
实现换成 WPF 原生 `DoubleAnimation` + 自定义 `IEasingFunction`，不引第三方库。
系统关掉「淡化菜单效果」时全部动画直接到位（`SystemParameters.ClientAreaAnimation`）。

在这个基础上做了**加强**，让动画更「华丽」：

- **页面过渡是两段式**：旧页先快速缩小淡出（140ms），播完再换新页，新页淡入 + 上移 + 从 1.03 缩放回 1（`OutCar`，340ms），三层叠加。用导航代号做竞态守卫——连续点多个导航项时，过期的退出回调会被丢弃，不会切错页。
- **所有动画的起点都显式给 `From`**。退场用的是 `HoldEnd` 填充，会留一个 hold 值；如果进场只靠「从当前值到目标」，起点会被上次的 hold 值带偏（表现就是本该从下方滑入的页面从上方滑进来）。显式 `From` 一并解决了虚拟化列表回收容器复用时的残留。
- **变换对象必须复用，不能每次追加。** `AaTranslateY` / `AaScale` 会给元素挂一个 `TranslateTransform` / `ScaleTransform` 塞进 `TransformGroup`。而页面是**缓存复用**的——同一个页面会被反复进/退场。如果每次都往组里**新加**一个变换，旧变换的 `HoldEnd` 值会一直留在组里参与合成，切几次之后整页就累积偏移（实测每切一次向下偏约 18px，「连续切左边栏页面越切越往下」就是这么来的）。所以取变换时先在组里**找已有的复用**，进场用显式 `From` 把它的值重新拉回起点，从根上杜绝叠加。
- **数字滚动** `AaNumber`：主页统计从 0 缓动滚到目标值，`DispatcherTimer` 按 16ms 刷帧、缓动取 `AniEase.GetValue`；同一个 `TextBlock` 重复调用会先停掉上一轮。
- **XAML 层微动效**：强调按钮悬停**流光扫过** + 按下回弹微缩、开关滑块 `BackEase` 回弹、导航指示条 / 页签下划线从中间展开、卡片列表项悬停抬升——这些走 `ControlTemplate` 的 `EnterActions/ExitActions`，不占 C# 代码。

时长大多在 140–340ms——动画是让人看出「东西变了」，不是让人等它。

### 账号交互：照着 PCL 的路子重做了一遍

原来的账号窗口是「左边列表 + 右边三个下划线页签」，问题是三处：

1. **打开就是一片空白。** 页签的选中事件被 `_buildingTabs` 重入保护挡掉了，构造函数里那句
   `Tabs.SelectedIndex = 0` 没触发任何面板切换，三个面板全停在 XAML 的 `Collapsed` 上。
   修法是构造完显式调一次 `SyncAddPanels()`——**可见性不能只依赖事件**。
2. **换账号必须开窗口。** 这是和 PCL 差得最远的一点：PCL 的账号选择器就在启动页侧栏，
   点一下换一个。现在标题栏那个账号条点开就是个下拉菜单，列出所有账号（当前的打勾），
   点一个当场换过去；「管理账号…」才是进窗口。
3. **点列表就静默换了当前账号。** 这个窗口叫「管理」，用户点列表十有八九只是想看看。
   现在左边点一下只换「正在查看谁」，真要换当前账号得按「设为当前账号」。

窗口本身也重排成左右两栏：左边账号列表（当前项带「当前」徽标）+ 底部「添加账号」，
右边在**详情**和**添加**两种模式间切换。添加方式用胶囊分段控件（离线账号 / 外置登录）
而不是下划线页签——这里选的是「用哪种方式登录」，是一组互斥的做法，不是同一份内容的几个视图。
添加成功后**不再关闭窗口**，而是切回详情并选中刚加的那个（以前点完「添加」窗口就没了，
来不及看结果）。

主界面下拉和窗口内「设为当前」是两条不同语义的路径，这是有意的：前者是「我现在要用这个」，
后者是「我想改一下记录」。

### 几个 WPF 的显示陷阱

- **`ComboBox` 收起时显示的是 `SelectionBoxItem.ToString()`**，`DisplayMemberPath` 只管展开的列表。所以列表项的包装类型都重写了 `ToString()`。
- **`ListBox` 不给 `ItemTemplate` 就会直接显示 `ToString()`**，界面上会出现 `LMCL.App.Views.Pages.VersionRow` 这种类型名。版本列表就踩过这个。
- 主题资源要用**绝对** `pack://…;component/…` URI 锚定到自身程序集 —— 相对 URI 会按*入口*程序集解析，换个宿主（离屏渲染工具）就找不到资源。
- **标签上写的 `Visibility` 是局部值，优先级高于 `Style` 里的 `DataTrigger`。**
  账号菜单里那个「当前账号打勾」的勾一开始怎么都不显示，就是因为标签上写了
  `Visibility="Collapsed"`；把那句挪进 `Style` 的 `Setter` 才生效。
- **`Popup` 的 `Placement="Bottom"` 是按目标的右边缘右对齐的**，实测 `popup.left = 目标.right - popup.宽 + HorizontalOffset`。
  按「左对齐、自己减 (菜单宽 − 目标宽)」去算会多减一个自身宽度，菜单整整偏出去一格。
  既然本来就右对齐，`HorizontalOffset` 留 0 即可。
- **浮出层不能沿用卡片的半透明底色。** 卡片用 70% 白是为了透出云母，但下拉菜单是悬在内容上面的，
  沿用同一支画刷会让底下的按钮透上来。另立了一支不透明的 `FlyoutBrush`。
- **对话框不能沿用主窗口的 `WindowBrush`。** 主窗口为了透出云母把它改成了 `alpha=0`，
  这个改动是**全局**的；对话框如果自己没开云母还照用这个键，整个窗口就是全透明，
  DWM 合成出来一片白 —— 暗色主题下白底白字，看着像界面渲染崩了。
  现在统一走 `WindowEffects.ApplyDialogBackdrop()`：云母开起来了才透明，没开就用不透明的 `DialogBrush`。
- 顺带一条排查经验：**「看起来半透明」和「真的半透明」要分开验证。** 截图受缩放、
  窗口层级、Popup 归属关系影响很大，肉眼在缩略图上判断经常出错；
  用 `_probe/pixel_probe.py` 直接读某块区域的像素值最快定案。
- **别依赖像素着色器类的效果（`BlurEffect` / `DropShadowEffect`）。**
  它们要硬件加速，在远程桌面 / 虚拟机 / 老显卡上会**整个元素不渲染**——
  不是效果弱一点，是那一层直接消失、露出下面的底色。背景模糊就踩过：
  `BlurRadius > 0` 时背景变一块纯色 `#202020`，本机（有加速）完全看不出来。
  现在改成自己按像素算（`ThemeManager.PrepareBackground`）。
  **测模糊要用高频图案**（16px 棋盘格），渐变图模糊前后几乎一样，用它测等于没测。
- **`Slider.ValueChanged` 在 XAML 解析期就会触发**，这时排在它后面的控件
  （比如显示数值的 TextBlock）还是 null，直接赋值会 `NullReferenceException`。
  重入保护标志的**初值要给 `true`**，构造函数末尾才放开。
- **连续值的设置别每次 `ValueChanged` 都存盘**：拖一次滑杆会触发几十次。
  拖动时只实时预览，松手（`Thumb.DragCompleted`）才保存。
- **`HttpListener.GetContextAsync()` 别配 `WaitAsync` / 取消令牌用**。取消时它只放弃等待，
  **内层任务还挂着没人观察**；之后 `Close()` 让它失败，终结器线程把它重抛成
  AggregateException —— 表现是「某个动作之后主界面弹未处理的错误」。
  要停就 `Stop()` / `Close()`，让 `GetContextAsync` 自己在 `await` 处抛出来被接住。
  另外 fire-and-forget 的任务（`_ = Task.Run(...)`）要么自己把异常吃干净，
  要么存下任务引用在合适时机 `Wait()` 一下。
- **在 `ComboBoxItem` 里放按钮，`Click` 里的 `e.Handled` 救不了「顺带选中」**。
  鼠标按下的瞬间 ComboBox 就把这一项选中了，`SelectionChanged` 比 `Click` 先触发。
  在按钮的 `PreviewMouseLeftButtonDown`（隧道阶段，更早）立一个标志，
  让选中处理逻辑跳过这一次。
- **消息框是自绘的，别再用 `MessageBox`**。全项目的提示都走 `Ui.Info/Success/Warn/Error/Confirm`
  → 自绘的 `MessageDialog`（云母、圆角、深色、强调色都跟主界面一致）。
  危险操作用 `Ui.ConfirmDanger`（主按钮红字，暗色主题下红底配白字看不清）。
- **`FindResource` / `DynamicResource` 取错了键是静默失败**——不抛异常，只是取回一个默认值。
  所以自检里凡是用到动态资源的部分，都要断言「取回来的值确实是对的」，
  不能只断言「没抛异常」（消息框那条探针就是这么写的，也验证过它真能抓错）。

### 排查「启动器里改了但游戏里没变」

这一类问题的排查顺序（皮肤踩过一次，同样适用于别的「启动器设置要影响游戏」的功能）：

1. **先确认启动参数真的带上了**——`LMCL.Probe launchcmd` 打印完整命令行，
   看 `-javaagent` 在不在、指向对不对。
2. **再看服务端有没有收到请求**。皮肤中继会把每个进来的请求写进日志：
   一条都没有 = 客户端压根没来问，问题在上游（agent 没挂上，或者停在主菜单没进世界）；
   有请求但没效果 = 返回值没被接受，照着路径继续查。
3. **别用默认值做验证**。测皮肤千万别用 Steve：游戏内置默认外观就是 Steve，
   「生效了」和「完全没工作」在屏幕上长得一模一样。换 Alex 或自己导入一张花图。
4. **皮肤是玩家实体生成时才向服务端要的**，停在主菜单不会触发档案查询——必须真的进世界。

还有一条：**JVM 只认最后一个 `-javaagent`**。外置登录和皮肤中继都要用它，
同时挂两个会**静默地**只生效一个（另一个功能直接坏掉，且没有任何报错）。
所以这两条在 `BuildOption` 里是互斥的（`if` / `else if`），别顺手改成并列的 `if`。

---

## 命令行

```
LMCL.exe --self-check       界面自检，全过退出码 0
LMCL.exe --preview-skin     直接打开账号管理并停在微软账号的皮肤页
LMCL.exe --preview-msgbox [n]   依次预览消息框；带 n 只弹第 n 条（--preview-msgbox 3 = 警告）
```

`--preview-skin` 是给开发用的：本机不一定有正版账号，没有的话
「上传皮肤 / 切换披风」那几张卡平时根本看不到。这个开关铺一份**假的、纯本地的**账号数据，
只为把那一页的版式调出来截图核对（`_probe/probe_msa_skin.py` 就是配它用的）。

`--preview-msgbox` 也是开发用的：消息框平时都跟在某个操作后面，
想一次看全五种语气（信息 / 成功 / 警告 / 错误 / 询问，外加危险确认）得手动造五种场景。
带序号只弹一条，截图核对观感用。

`--self-check` 把全部界面（外壳窗口、5 个页面、4 个嵌入子面板、账号窗口的详情/添加两种模式、文本窗口、选择安装位置窗口、消息框）依次构造一遍再退出，**退出码 0 表示全通过（当前 24 项）**。报告写到 `%APPDATA%\LMCL\logs\last-self-check.log`，界面上「设置 → 工具 → 界面自检」是同一个东西。

页面是懒加载的，所以「能启动」只证明 `ShellWindow.xaml` 解析得动，证明不了别的页面没写崩 —— 这个开关就是为此存在的。

自检还能带**断言**，不只是「构造没抛异常」。除了账号窗口那几条（「详情面板可见」「三个添加面板恰好一个为真」「第三方账号没有皮肤入口」「皮肤页该亮的卡片都亮了」），还有：

- **消息框 · 五种语气**：逐个 `Apply` 后断言图标字形、图标颜色、按钮组合真的跟着变了，最后复原。这些全靠 `FindResource` 取动态资源键，键名写错一个字母**不会抛异常**，只会静默退化成灰色问号——所以必须比对取回来的实际值（这条探针也验证过真能抓错：把资源键改坏，自检立刻失败）。
- **多自定义皮肤**：真的往皮肤库导入两张小 PNG，断言「加两张列表变长、删一张变短、id 互不相同」，跑完自己清理。多皮肤的核心就是列表长度随导入变化，光看界面构造查不出「导入了但列表没刷新」。
- **皮肤页版式**：断言皮肤页左栏（预览）和右栏（清单）的矩形**不相交**。早先那几个「标题 + 右侧按钮」的 `Grid` 漏写了列定义，两个子元素落在同一格、按钮直接压在说明文字上，而 XAML 编译和自检都不报错 —— 这种错只能靠坐标断言查。
- **分区导航**（下载页）：把六个分区逐个点一遍，断言面板真的切过去了 —— 专治「点了没反应」（曾经因为一个启用标志初值写反，所有点击都被前置守卫挡掉）。
- **筛选与更新面板**（下载页）：逐个分区切过去，断言「分类」下拉按当前资源类型换了一套选项、数据包那格确实收起、光影那三栏确实收起来、更新面板能亮起来。这条探针在开发时就抓出过一个真 bug：整合包分区走的是另一条分支，没重建分类下拉，下拉里一直是上个分区的分类。

**注意自检的边界**：它能证明「构造得出来、状态对」，但证明不了「看着对、点得动」。光影那一轮就吃过亏 —— 列表搜得出来、自检全绿，点进详情却写着「没有支持 xx / Forge 的版本」、安装键是灰的。凡是涉及版式、点击路径、真实下载的改动，都要用 `_probe/lmcl_ui.py` 驱动真窗口截图确认一遍（见下）。

## 数据位置

| 内容 | 位置 |
| --- | --- |
| 游戏目录 | 默认 `%APPDATA%\.minecraft`，可在设置里改 |
| 启动器设置 / 账号 | `%APPDATA%\LMCL\config.json`、`accounts.json` |
| 日志、缓存、authlib-injector | `%APPDATA%\LMCL\logs`、`cache`、`authlib-injector.jar` |

启动器不在注册表里留东西，整个游戏目录拷走就能换机器。

---

## 开发辅助

`tools/LMCL.Probe` 是不开界面也能验证链路的命令行工具：

```
LMCL.Probe paths          路径换算
LMCL.Probe java           Java 识别与版本号解析
LMCL.Probe mirror         镜像改写规则的逐条断言（改下载策略时必跑）
LMCL.Probe remote         拉取远程版本清单
LMCL.Probe files          列出安装会涉及的全部文件、重复路径、缺失项
LMCL.Probe injector       外置登录：下载 authlib-injector 并校验拼出的启动参数
LMCL.Probe vanilla        完整安装一个原版
LMCL.Probe launchcmd      组装启动命令并核对关键参数（不起进程）
LMCL.Probe launch         真启动一次游戏，读回客户端日志再收掉进程
LMCL.Probe loaders        列出四类加载器的可选版本
LMCL.Probe forge|neo|fabric|quilt   安装对应加载器
LMCL.Probe versionlist    列出已安装版本
LMCL.Probe skinrelay      本地皮肤中继：起一个真中继，验根文档 / 档案 / **签名** / PNG
```

游戏目录默认是仓库外的 `_probe/mc`（不会碰你真正的 `.minecraft`），用环境变量 `PCLITE_GAMEDIR`
可以换。`launch` 还会读：

| 变量 | 作用 |
|---|---|
| `PCLITE_USER` | 离线名字 |
| `PCLITE_WAIT` | 观察秒数（默认 45） |
| `PCLITE_SKIN` | 用哪张本地皮肤（`official-alex-offline` 等）。**测皮肤一定要换一张不是 Steve 的**，见下 |
| `PCLITE_WORLD` | 进哪个存档（`--quickPlaySingleplayer`）。**测皮肤必须进世界**：皮肤是玩家实体生成时才去要的，停在主菜单客户端压根不查档案 |
| `PCLITE_ACCT` | `offline`（默认）/ `third`，用来验证外置登录和皮肤中继不会互相覆盖 |

注意 `vanilla` / `forge` 这些步骤会真下载几百 MB 到那个目录里；仓库里不带游戏数据，需要时自己跑。

仓库外的 `_probe/` 里还有几个一次性验证工具：`Render`（离屏渲染每个界面，亮暗两套）、
`shot.py`（按窗口类名抓取正在运行的窗口）、`make_icon.py`（生成 app.ico）、
`ApiProbe`（反射打印 CmlLib 的真实公开 API）。

其中 **`lmcl_ui.py`** 是用来「像用户那样」验界面的：它按 WPF 窗口类名
（`HwndWrapper[LMCL;…]`）定位主窗口，可以点任意窗口内坐标、也可以把窗口截成 PNG。

```
python _probe/lmcl_ui.py click <x> <y>       # 点窗口内 (x,y)
python _probe/lmcl_ui.py shot out.png        # 给主窗口截图
```

它比 `shot.py` / `click.py` 更适合这个项目：后两个按**标题**匹配窗口，
而「LMCL」这个标题同时会命中浏览器标签页（本项目的会话页面标题里也有 LMCL），
于是 `click.py` 会因为「找到 2 个窗口」直接拒绝执行。`lmcl_ui.py` 用窗口类名，
天然唯一；而且它取面积最大的那个 WPF 窗口，所以对话框开着时也不会抓错。

改完版式、点击路径、下载流程之后，推荐这样验一遍：

```
1. 启动 LMCL.exe
2. python lmcl_ui.py click 60 116     # 点左侧「下载」
3. python lmcl_ui.py click 285 251    # 点「光影」
4. python lmcl_ui.py shot out.png     # 看图确认
```

## 已知限制

- 窗口的最大化按钮是自绘的，所以不触发 Win11 的贴靠布局（Snap Layouts）。
- Forge / NeoForge 的安装依赖官方安装器，需要本机有合适的 Java 才能跑处理器，装不了会自动去下 Mojang 运行时。
- NeoForge 的版本映射有个例外：MC `1.20.1` 是以 `net.neoforged:forge` 47.1.x 发布的，按常规规则查不到版本，会返回空并给出说明。
- 整合包（`.mrpack`）的安装顺序逻辑已写对，但没有跑过真实整合包的完整流程。
- 微软正版登录需要走两步（打开浏览器登录 → 把地址栏里的授权码粘回启动器），属正常设计，不是缺陷。

## 第三方

- [CmlLib.Core](https://github.com/CmlLib/CmlLib.Core) 4.0.6 —— 下载、安装、组装启动命令
- CmlLib.Core.Installer.Forge 2.0.0 / NeoForge 4.0.1 —— 两者都导出同名 `NeoForgeInstaller`，用 `extern alias` 隔离
- CommunityToolkit.Mvvm 8.4.2 —— 仅用 `ObservableObject`
- [authlib-injector](https://github.com/yushijinhun/authlib-injector) —— 外置登录，运行时按需下载
- [HtmlAgilityPack](https://github.com/zzzprojects/html-agility-pack)、[SharpZipLib](https://github.com/icsharpcode/SharpZipLib)、[SevenZip](https://github.com/squid-box/SevenZipSharp) —— 随 CmlLib 引入

界面部分在动画与信息结构上参考了 [Plain Craft Launcher 2](https://github.com/Hex-Dragon/PCL2) 的交互习惯，未使用其任何源码与资源。

## 许可

本项目以 [AGPL-3.0-or-later](LICENSE) 分发。

这意味着：任何人都可以自由使用、修改和再分发；但**分发时必须以相同许可证开源全部源码**，
并且若把它作为网络服务对外提供（例如联机平台后端），同样必须向服务使用者公开源码。

- 主体代码：Copyright (c) laolong，AGPL-3.0-or-later
- 第三方库各自的许可证见上表（均为 MIT 系，与 AGPL 兼容）
