using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace PaperTodo;

internal sealed class TodoEdgeCapsulePreviewProvider : IEdgeCapsulePreviewProvider
{
    // Keep row creation bounded; totals still describe the complete model and the paper remains
    // the place for browsing the full list.
    // 【新增功能：动态尺寸】渲染行数上限从 12 放宽到 20（普通/每日各最多 20 行），
    // 让胶囊小窗口尽量一次显示全部任务，不再频繁出现"…"截断。
    internal const int MaximumRenderedItems = 20;
    internal const int MaximumItemCharacters = 512;
    internal const double LinkedTargetButtonSizeDip = 20;

    // 【新增功能：动态尺寸】Todo 预览高度上限从全局 320 放宽到 640。
    // 不修改共享的 BuiltInMaximumHeightDip（笔记/插件预览还在用 320）；
    // 640 是 provider 侧上限，调用侧仍会按屏幕可用区域（monitor 高度 - 16）自动裁剪，
    // 所以不会撑出屏幕。
    internal const double TodoMaximumHeightDip = 640;

    // Total card width lost before todo text reaches its Grid column:
    // host close/chrome 22 + view margins 19 + items right margin 2 + row padding 7 +
    // checkbox column 24 + text margins 6 = 80 DIPs. Marker width is added separately per row.
    // 【新增功能：小窗口显示截止日期】每行截止日期列（"MM-dd" 约 30px + 边距 2）再占用 32 DIPs。
    private const double TodoTextLaneFixedReserveDip = 80 + 32;

    public static TodoEdgeCapsulePreviewProvider Instance { get; } = new();

    private TodoEdgeCapsulePreviewProvider()
    {
    }

    public EdgeCapsulePreviewDescriptor Describe(EdgeCapsulePreviewContext context)
    {
        var snapshot = CaptureSnapshot(context.Paper, context.State);
        var items = snapshot.Items;
        // ===== 【新增功能：每日任务进胶囊预览】尺寸估算把每日任务行一并计入 =====
        // 预览卡片高度随"每日任务 + 限时任务"总行数增长，避免每日任务多时被截断。
        var body = string.Join(
            Environment.NewLine,
            items.Select(item => PreviewItemText(item.Text))
                .Concat(snapshot.DailyItems.Select(template => PreviewItemText(template.Text))));
        var markerReserve = MaximumMarkerReserveDip(items);
        var textLaneReserve = TodoTextLaneFixedReserveDip + markerReserve;
        var width = EdgeCapsulePreviewMeasure.MeasureWidth(
            context.Title,
            body,
            minimum: EdgeCapsulePreviewSize.MinimumWidthDip,
            maximum: 450,
            fixedReserveWidthDip: textLaneReserve);
        // Height estimation must subtract the same fixed lane that width estimation added. Keeping
        // these two calculations on one reserve prevents the renderer from wrapping text that the
        // size estimator believed still had room beside the optional marker controls.
        var availableTextWidth = Math.Max(1, width - textLaneReserve);
        var estimatedLines = items.Count == 0 && snapshot.DailyItems.Count == 0
            ? 1
            : items.Concat(snapshot.DailyItems.Cast<object>())
                .Sum(item => Math.Clamp(
                    EdgeCapsulePreviewMeasure.EstimateWrappedLines(
                        PreviewItemText(item is PaperItem paperItem ? paperItem.Text : ((DailyTodoTemplate)item).Text),
                        availableTextWidth),
                    1,
                    3));
        // 【新增功能：动态尺寸】高度按内容行数线性增长（行高 30 贴合实际行高，防截断），
        // 上限放宽到 TodoMaximumHeightDip（640）；空态保持 120。
        // 分区标题（每日任务/限时任务）各占一行，计入估算，避免最后一行被截断。
        var sectionLabelRows = (items.Count > 0 ? 1 : 0) + (snapshot.DailyItems.Count > 0 ? 1 : 0);
        var height = items.Count == 0 && snapshot.DailyItems.Count == 0
            ? 120
            : Math.Clamp(
                62 + Math.Min(
                    MaximumRenderedItems + Math.Min(MaximumRenderedItems, snapshot.DailyItems.Count),
                    estimatedLines + sectionLabelRows) *
                    AppTypography.Scale(30),
                150,
                TodoMaximumHeightDip);
        if (items.Count == 0 && snapshot.DailyItems.Count == 0)
        {
            width = Math.Max(130, width);
        }

        return new EdgeCapsulePreviewDescriptor(
            new EdgeCapsulePreviewSize(width, height),
            size => new TodoEdgeCapsulePreviewView(context, size, snapshot));
    }

    internal static TodoEdgeCapsulePreviewSnapshot CaptureSnapshot(
        PaperData paper,
        AppState state)   // 【新增功能】每日任务在全局 AppState，快照需要一并传入
    {
        var meaningfulItems = paper.Items
            .Where(TodoRules.HasMeaningfulContent)
            .ToList();
        var total = meaningfulItems.Count;
        var done = meaningfulItems.Count(item => item.Done);

        // ===== 【新增功能：小窗口按截止时间排序】与展开面板 OrderedItems 口径一致 =====
        // 1) 未完成普通任务在前，按截止日期升序（DueDate 为 null 视作无穷大，排最后）；
        // 2) 已完成普通任务排在最后；
        // 3) 同排序键以原有 Order（拖拽顺序）为次级稳定排序。
        // 只取前 MaximumRenderedItems 条渲染，总量（total/done）仍统计完整模型。
        var selected = meaningfulItems
            .OrderBy(item => item.Done)
            .ThenBy(item => item.DueDate ?? DateTimeOffset.MaxValue)
            .ThenBy(item => item.Order)
            .Take(MaximumRenderedItems)
            .ToList();

        // ===== 【新增功能：每日任务进胶囊预览】收集当日未完成的每日任务模板 =====
        // 与展开面板同一规则（DailyTodoCompletions[id] == 今天 → 当天隐藏），
        // 按模板 Order 升序，最多渲染与普通任务相同的行数上限。
        var todayKey = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var dailyItems = state.DailyTodoTemplates
            .OrderBy(template => template.Order)
            .Where(template =>
                !(state.DailyTodoCompletions?.TryGetValue(template.Id, out var completedDate) == true &&
                  string.Equals(completedDate, todayKey, StringComparison.Ordinal)))
            .Take(MaximumRenderedItems)
            .ToList();

        return new TodoEdgeCapsulePreviewSnapshot(selected, total, done, dailyItems);
    }

    internal static string PreviewItemText(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "—";
        }

        // Bound work before trimming/measuring. A single malformed or pasted multi-megabyte item
        // must not make opening the preview proportional to its complete text length.
        var truncated = value.Length > MaximumItemCharacters;
        var bounded = truncated
            ? value[..MaximumItemCharacters]
            : value;
        var text = bounded.Trim();
        if (text.Length == 0)
        {
            return truncated ? "…" : "—";
        }
        if (!truncated)
        {
            return text;
        }
        return text.Length < MaximumItemCharacters
            ? text + "…"
            : text[..(MaximumItemCharacters - 1)] + "…";
    }

    private static double MaximumMarkerReserveDip(IReadOnlyList<PaperItem> items)
    {
        var maximum = 0.0;
        foreach (var item in items)
        {
            var reserve = 0.0;
            if (item.ReminderAt.HasValue || item.ReminderTriggered)
            {
                reserve += AppTypography.Scale(10.5) + 2;
            }
            if (HasLinkedTarget(item))
            {
                // 20 DIP button + its horizontal margin. The final 2 DIP below belongs to the
                // marker lane itself and is shared with a reminder marker on the same row.
                reserve += LinkedTargetButtonSizeDip + 2;
            }
            if (reserve > 0)
            {
                reserve += 2;
            }
            maximum = Math.Max(maximum, reserve);
        }
        return maximum;
    }

    private static bool HasLinkedTarget(PaperItem item) =>
        !string.IsNullOrWhiteSpace(item.LinkedPaperId) ||
        !string.IsNullOrWhiteSpace(item.LinkedPath);
}

internal sealed record TodoEdgeCapsulePreviewSnapshot(
    IReadOnlyList<PaperItem> Items,
    int Total,
    int Done,
    // ===== 【新增功能：每日任务进胶囊预览】当日未完成的每日任务模板 =====
    // 与展开面板同规则：当天已完成的模板不进快照（当天隐藏），次日自动恢复。
    IReadOnlyList<DailyTodoTemplate> DailyItems);

internal sealed class TodoEdgeCapsulePreviewView : EdgeCapsuleLivePreviewView
{
    // 【新增功能：小窗口显示截止日期】过期未完成任务的日期标红（与展开面板 OverdueTextBrush 同色：
    // 浅色 #C62828 / 深色 #FF8A80），未过期用弱色。
    private static Brush OverdueBrush => Theme.IsDark
        ? new SolidColorBrush(Color.FromRgb(0xFF, 0x8A, 0x80))
        : new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28));

    private readonly TextBlock _title;
    private readonly TextBlock _summary;
    private readonly StackPanel _items;
    private readonly TodoEdgeCapsulePreviewViewport _viewport;
    private bool _rebuilding;
    private TodoEdgeCapsulePreviewSnapshot? _initialSnapshot;

    public TodoEdgeCapsulePreviewView(
        EdgeCapsulePreviewContext context,
        EdgeCapsulePreviewSize size,
        TodoEdgeCapsulePreviewSnapshot initialSnapshot)
        : base(context, size)
    {
        _initialSnapshot = initialSnapshot;
        Margin = new Thickness(10, 9, 9, 10);
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition());

        var heading = new Grid
        {
            Margin = new Thickness(2, 0, 1, 7)
        };
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        heading.ColumnDefinitions.Add(new ColumnDefinition());

        _title = new TextBlock
        {
            FontFamily = AppTypography.UiFontFamily,
            FontSize = AppTypography.Scale(13),
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = Math.Max(48, size.WidthDip - 86)
        };
        _title.SetResourceReference(TextBlock.ForegroundProperty, "TextBrushKey");
        heading.Children.Add(_title);

        _summary = new TextBlock
        {
            Margin = new Thickness(6, 0, 0, 0),
            FontFamily = AppTypography.UiFontFamily,
            FontSize = AppTypography.Scale(11),
            VerticalAlignment = VerticalAlignment.Center
        };
        _summary.SetResourceReference(TextBlock.ForegroundProperty, "WeakTextBrushKey");
        Grid.SetColumn(_summary, 1);
        heading.Children.Add(_summary);
        Children.Add(heading);

        _items = new StackPanel
        {
            Margin = new Thickness(0, 0, 2, 0)
        };
        _viewport = new TodoEdgeCapsulePreviewViewport(_items);
        Grid.SetRow(_viewport, 1);
        Children.Add(_viewport);

        InitializeLiveContent();
    }

    protected override void RebuildContent()
    {
        var snapshot = _initialSnapshot ??
            TodoEdgeCapsulePreviewProvider.CaptureSnapshot(Context.Paper, Context.State);
        _initialSnapshot = null;
        var meaningful = snapshot.Items;

        _title.Text = Context.Title;
        _title.ToolTip = Context.Title;
        _summary.Text = $"{snapshot.Done}/{snapshot.Total}";

        _rebuilding = true;
        try
        {
            _items.Children.Clear();
            // ===== 【新增功能：每日任务进胶囊预览】分区渲染 =====
            // 与展开面板一致：上方「每日任务」（当天已完成的隐藏），下方「限时任务」；
            // 两者都空时才显示空态占位。
            var hasDaily = snapshot.DailyItems.Count > 0;
            var hasRegular = meaningful.Count > 0;
            if (!hasDaily && !hasRegular)
            {
                var empty = new TextBlock
                {
                    Text = "—",
                    Margin = new Thickness(8, 18, 8, 8),
                    FontFamily = AppTypography.UiFontFamily,
                    FontSize = AppTypography.Scale(16),
                    HorizontalAlignment = HorizontalAlignment.Center
                };
                empty.SetResourceReference(TextBlock.ForegroundProperty, "WeakTextBrushKey");
                _items.Children.Add(empty);
            }
            else
            {
                if (hasDaily)
                {
                    _items.Children.Add(BuildSectionLabel("每日任务"));
                    foreach (var template in snapshot.DailyItems)
                    {
                        _items.Children.Add(BuildDailyRow(template));
                    }
                }
                if (hasRegular)
                {
                    _items.Children.Add(BuildSectionLabel("限时任务"));
                    foreach (var item in meaningful)
                    {
                        _items.Children.Add(BuildRow(item));
                    }
                }
            }
        }
        finally
        {
            _rebuilding = false;
        }

        _viewport.SetSourceTruncated(snapshot.Total > meaningful.Count);
    }

    // ===== 【新增功能：每日任务进胶囊预览】分区标题（胶囊内黑色文字）=====
    private FrameworkElement BuildSectionLabel(string text)
    {
        var label = new TextBlock
        {
            Text = text,
            Margin = new Thickness(2, 4, 2, 1),
            FontFamily = AppTypography.UiFontFamily,
            FontSize = AppTypography.Scale(11),
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };
        label.SetResourceReference(TextBlock.ForegroundProperty, "TextBrushKey");
        return label;
    }

    // ===== 【新增功能：每日任务进胶囊预览】胶囊预览的每日任务行 =====
    // 复选框 + 任务名称（无删除线、无提醒/链接标记）；
    // 勾选 → Context.SetDailyTodoDone → 记录当天完成 + 当天隐藏（次日零点自动恢复）。
    private FrameworkElement BuildDailyRow(DailyTodoTemplate template)
    {
        var row = new Border
        {
            Margin = new Thickness(0, 1, 0, 1),
            Padding = new Thickness(3, 3, 4, 3),
            CornerRadius = new CornerRadius(5),
            Background = Brushes.Transparent
        };
        row.MouseEnter += (_, _) =>
            row.SetResourceReference(Border.BackgroundProperty, "HoverBrushKey");
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());

        var check = new CheckBox
        {
            IsChecked = false,
            Width = 20,
            Height = 20,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 1, 0, 0),
            Cursor = Cursors.Hand,
            Focusable = false,
            FocusVisualStyle = null,
            Style = Context.ReadTodoCheckStyle()
        };
        EdgeCapsulePreviewInteraction.SetConsumesPointer(check, true);
        check.Click += (_, _) =>
        {
            if (_rebuilding)
            {
                return;
            }

            var requested = check.IsChecked == true;
            if (!Context.SetDailyTodoDone(template.Id, requested))
            {
                _rebuilding = true;
                check.IsChecked = false;
                _rebuilding = false;
                return;
            }
        };
        grid.Children.Add(check);

        var text = new TextBlock
        {
            Text = TodoEdgeCapsulePreviewProvider.PreviewItemText(template.Text),
            Margin = new Thickness(1, 0, 5, 0),
            FontFamily = AppTypography.FontFamilyFor(content: true, bold: false),
            FontSize = AppTypography.Scale(12),
            FontWeight = FontWeights.Normal,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };
        text.SetResourceReference(TextBlock.ForegroundProperty, "TextBrushKey");
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        row.Child = grid;
        return row;
    }

    private FrameworkElement BuildRow(PaperItem item)
    {
        var row = new Border
        {
            Margin = new Thickness(0, 1, 0, 1),
            Padding = new Thickness(3, 3, 4, 3),
            CornerRadius = new CornerRadius(5),
            Background = Brushes.Transparent
        };
        row.MouseEnter += (_, _) =>
            row.SetResourceReference(Border.BackgroundProperty, "HoverBrushKey");
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // 【新增】截止日期列
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });   // marker 列（后移）

        var check = new CheckBox
        {
            IsChecked = item.Done,
            Width = 20,
            Height = 20,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 1, 0, 0),
            Cursor = Cursors.Hand,
            Focusable = false,
            FocusVisualStyle = null,
            Style = Context.ReadTodoCheckStyle()
        };
        EdgeCapsulePreviewInteraction.SetConsumesPointer(check, true);
        check.Click += (_, _) =>
        {
            if (_rebuilding)
            {
                return;
            }

            var requested = check.IsChecked == true;
            if (!Context.SetTodoDone(item.Id, requested))
            {
                _rebuilding = true;
                check.IsChecked = item.Done;
                _rebuilding = false;
                return;
            }
        };
        grid.Children.Add(check);

        var text = new TextBlock
        {
            Text = TodoEdgeCapsulePreviewProvider.PreviewItemText(item.Text),
            Margin = new Thickness(1, 0, 5, 0),
            FontFamily = AppTypography.FontFamilyFor(content: true, bold: false),
            FontSize = AppTypography.Scale(12),
            FontWeight = FontWeights.Normal,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };
        text.SetResourceReference(
            TextBlock.ForegroundProperty,
            item.Done ? "WeakTextBrushKey" : "TextBrushKey");
        if (item.Done)
        {
            text.TextDecorations = TextDecorations.Strikethrough;
        }
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        // ===== 【新增功能：小窗口显示截止日期】普通任务行的截止日期（MM-dd，过期标红）=====
        // 与展开面板规则一致：未完成且当前时间已过截止日期 → 红色；其余弱色。
        // 每日任务没有截止日期，不受影响。
        if (item.DueDate is { } dueDate)
        {
            var dueLabel = new TextBlock
            {
                Text = dueDate.LocalDateTime.ToString("MM-dd", CultureInfo.InvariantCulture),
                Margin = new Thickness(0, 0, 2, 0),
                FontFamily = AppTypography.UiFontFamily,
                FontSize = AppTypography.Scale(10.5),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = dueDate.ToString("yyyy-MM-dd")
            };
            var overdue = !item.Done && item.DueDate < DateTimeOffset.Now;
            if (overdue)
            {
                dueLabel.Foreground = OverdueBrush;   // 过期 → 红
            }
            else
            {
                dueLabel.SetResourceReference(TextBlock.ForegroundProperty, "WeakTextBrushKey");
            }
            Grid.SetColumn(dueLabel, 2);
            grid.Children.Add(dueLabel);
        }

        var marker = BuildItemMarker(item);
        Grid.SetColumn(marker, 3);   // marker 列后移到列 3
        grid.Children.Add(marker);

        row.Child = grid;
        return row;
    }

    private FrameworkElement BuildItemMarker(PaperItem item)
    {
        var markers = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(1, 0, 1, 0),
            VerticalAlignment = VerticalAlignment.Center
        };

        if (item.ReminderAt.HasValue || item.ReminderTriggered)
        {
            markers.Children.Add(CreateMarkerText("◷"));
        }

        string? linkedMarker = null;
        if (!string.IsNullOrWhiteSpace(item.LinkedPaperId))
        {
            linkedMarker = "↗";
        }
        else if (!string.IsNullOrWhiteSpace(item.LinkedPath))
        {
            linkedMarker = "⌁";
        }

        if (linkedMarker != null)
        {
            var glyph = CreateMarkerText(linkedMarker);
            glyph.Margin = new Thickness(0);
            var link = new Button
            {
                Content = glyph,
                Width = TodoEdgeCapsulePreviewProvider.LinkedTargetButtonSizeDip,
                Height = TodoEdgeCapsulePreviewProvider.LinkedTargetButtonSizeDip,
                Margin = new Thickness(1, 0, 1, 0),
                Padding = new Thickness(0),
                Background = Brushes.Transparent,
                BorderBrush = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                Focusable = false,
                FocusVisualStyle = null,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center
            };
            EdgeCapsulePreviewInteraction.SetConsumesPointer(link, true);
            link.MouseEnter += (_, _) =>
            {
                link.SetResourceReference(
                    Control.BackgroundProperty,
                    "HoverBrushKey");
                glyph.SetResourceReference(
                    TextBlock.ForegroundProperty,
                    "LinkBrushKey");
            };
            link.MouseLeave += (_, _) =>
            {
                link.Background = Brushes.Transparent;
                glyph.SetResourceReference(
                    TextBlock.ForegroundProperty,
                    "WeakTextBrushKey");
            };
            link.Click += (_, e) =>
            {
                Context.OpenTodoLinkedTarget(item.Id);
                e.Handled = true;
            };
            markers.Children.Add(link);
        }

        return markers;
    }

    private static TextBlock CreateMarkerText(string text)
    {
        var marker = new TextBlock
        {
            Text = text,
            Margin = new Thickness(1, 0, 1, 0),
            FontFamily = AppTypography.SymbolFontFamily,
            FontSize = AppTypography.Scale(10.5),
            VerticalAlignment = VerticalAlignment.Center
        };
        marker.SetResourceReference(
            TextBlock.ForegroundProperty,
            "WeakTextBrushKey");
        return marker;
    }
}

// A fixed top excerpt, not a hidden ScrollViewer. No offset, keyboard scrolling, wheel scrolling
// or BringIntoView scrolling exists; clipping also keeps off-card controls out of pointer hit tests.
internal sealed class TodoEdgeCapsulePreviewViewport : Panel
{
    private readonly StackPanel _items;
    private readonly TextBlock _overflowIndicator;
    private readonly RectangleGeometry _itemsClip = new();
    private bool _sourceTruncated;

    public TodoEdgeCapsulePreviewViewport(StackPanel items)
    {
        ClipToBounds = true;
        _items = items;
        _items.Clip = _itemsClip;
        _overflowIndicator = new TextBlock
        {
            Text = "…",
            FontFamily = AppTypography.UiFontFamily,
            FontSize = AppTypography.Scale(14),
            TextAlignment = TextAlignment.Center,
            IsHitTestVisible = false
        };
        _overflowIndicator.SetResourceReference(TextBlock.ForegroundProperty, "WeakTextBrushKey");
        Children.Add(_items);
        Children.Add(_overflowIndicator);
    }

    public void SetSourceTruncated(bool truncated)
    {
        _sourceTruncated = truncated;
        InvalidateArrange();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var naturalSize = new Size(availableSize.Width, double.PositiveInfinity);
        _items.Measure(naturalSize);
        _overflowIndicator.Measure(naturalSize);
        return new Size(
            Math.Min(availableSize.Width, Math.Max(_items.DesiredSize.Width, _overflowIndicator.DesiredSize.Width)),
            Math.Min(availableSize.Height, _items.DesiredSize.Height));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var overflow = _sourceTruncated || _items.DesiredSize.Height > finalSize.Height;
        var indicatorHeight = overflow ? Math.Min(finalSize.Height, _overflowIndicator.DesiredSize.Height) : 0;
        var visibleHeight = finalSize.Height - indicatorHeight;
        _itemsClip.Rect = new Rect(0, 0, finalSize.Width, visibleHeight);
        _items.Arrange(new Rect(0, 0, finalSize.Width, _items.DesiredSize.Height));
        _overflowIndicator.Opacity = overflow ? 1 : 0;
        _overflowIndicator.Arrange(new Rect(0, visibleHeight, finalSize.Width, indicatorHeight));
        return finalSize;
    }
}
