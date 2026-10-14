using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using LMCL.App.Services;
using LMCL.Core.Diagnostics;

namespace LMCL.App.Controls;

/// <summary>日志面板。绑定的是 LogBus 这个全局总线，所以任何页面都能看到全部日志。</summary>
public partial class LogPanel : UserControl
{
    private const int MaxLines = 400;

    public sealed class Line
    {
        public required string Time { get; init; }
        public required string Message { get; init; }
        public required Brush Brush { get; init; }
        public string Raw { get; init; } = "";
    }

    private readonly ObservableCollection<Line> _lines = new();
    private bool _subscribed;

    public LogPanel()
    {
        InitializeComponent();
        List.ItemsSource = _lines;

        Loaded += (_, _) => Subscribe();
        Unloaded += (_, _) => Unsubscribe();
    }

    private void Subscribe()
    {
        if (_subscribed) return;
        _subscribed = true;

        // 面板可能是后建的，先把已有的日志回填进来
        foreach (var entry in App.Services.Log.Recent) Append(entry);
        App.Services.Log.EntryWritten += OnEntry;
        App.Services.Log.Cleared += OnCleared;
        ThemeManager.Changed += OnThemeChanged;
    }

    private void Unsubscribe()
    {
        if (!_subscribed) return;
        _subscribed = false;
        App.Services.Log.EntryWritten -= OnEntry;
        App.Services.Log.Cleared -= OnCleared;
        ThemeManager.Changed -= OnThemeChanged;

        // 顺手清空：下次 Load 会从 Recent 完整回填一遍。
        // 不清的话，每次切回主页都会把历史日志再追加一遍，越切越多。
        _lines.Clear();
    }

    private void OnEntry(LogEntry entry) =>
        Dispatcher.BeginInvoke(() => Append(entry), DispatcherPriority.Background);

    private void OnCleared() => Dispatcher.BeginInvoke(() =>
    {
        _lines.Clear();
        EmptyHint.Visibility = Visibility.Visible;
    });

    private void OnThemeChanged() => Dispatcher.BeginInvoke(RebuildBrushes);

    private void Append(LogEntry entry)
    {
        // 先判断用户当前是不是贴着底部，再插入新行——否则正在往上翻日志时会被硬拽回来
        var follow = IsAtBottom();

        _lines.Add(new Line
        {
            Time = entry.TimeText,
            Message = entry.Message,
            Brush = BrushFor(entry.Level),
            Raw = entry.ToString(),
        });

        EmptyHint.Visibility = Visibility.Collapsed;

        while (_lines.Count > MaxLines) _lines.RemoveAt(0);

        if (follow && _lines.Count > 0) List.ScrollIntoView(_lines[^1]);
    }

    /// <summary>列表是不是已经滚到底（或内容还没超出一屏）。</summary>
    private bool IsAtBottom()
    {
        var scroller = FindScrollViewer(List);
        if (scroller is null) return true;
        if (scroller.ScrollableHeight <= 0) return true;

        return scroller.VerticalOffset >= scroller.ScrollableHeight - 8;
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject? root)
    {
        if (root is null) return null;
        if (root is ScrollViewer found) return found;

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            if (FindScrollViewer(VisualTreeHelper.GetChild(root, i)) is { } hit) return hit;
        }

        return null;
    }

    private void RebuildBrushes()
    {
        var entries = App.Services.Log.Recent;
        _lines.Clear();
        foreach (var entry in entries) Append(entry);
    }

    private static Brush BrushFor(LogLevel level)
    {
        var key = level switch
        {
            LogLevel.Success => "SuccessBrush",
            LogLevel.Warning => "WarningBrush",
            LogLevel.Error => "ErrorBrush",
            LogLevel.Debug => "LogDebugBrush",
            _ => "LogInfoBrush",
        };

        return Application.Current.TryFindResource(key) as Brush
               ?? (Application.Current.TryFindResource("TextBrush") as Brush)
               ?? Brushes.Black;
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        Ui.Copy(string.Join(Environment.NewLine, _lines.Select(l => l.Raw)));
    }

    private void Clear_Click(object sender, RoutedEventArgs e) => App.Services.Log.Clear();
}
