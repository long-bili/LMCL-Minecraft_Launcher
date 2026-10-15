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

## 第三方

- [CmlLib.Core](https://github.com/CmlLib/CmlLib.Core) 4.0.6 —— 下载、安装、组装启动命令
- CmlLib.Core.Installer.Forge 2.0.0 / NeoForge 4.0.1 —— 两者都导出同名 `NeoForgeInstaller`，用 `extern alias` 隔离
- CommunityToolkit.Mvvm 8.4.2 —— 仅用 `ObservableObject`
- [authlib-injector](https://github.com/yushijinhun/authlib-injector) —— 外置登录，运行时按需下载
- [HtmlAgilityPack](https://github.com/zzzprojects/html-agility-pack)、[SharpZipLib](https://github.com/icsharpcode/SharpZipLib)、[SevenZip](https://github.com/squid-box/SevenZipSharp) —— 随 CmlLib 引入

## 许可

本项目以 [AGPL-3.0-or-later](LICENSE) 分发。

这意味着：任何人都可以自由使用、修改和再分发；但**分发时必须以相同许可证开源全部源码**，
并且若把它作为网络服务对外提供（例如联机平台后端），同样必须向服务使用者公开源码。

- 主体代码：Copyright (c) laolong，AGPL-3.0-or-later
- 第三方库各自的许可证见上表（均为 MIT 系，与 AGPL 兼容）
