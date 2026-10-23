using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace PaperTodo;

// ============================================================================
// 【新增功能：每日任务模块】
// 悬浮待办面板中独立于普通任务的区块：
//   - 每天 0 点自动把所有每日任务重置为未完成（完成状态按日期惰性判断）；
//   - 当天勾选完成的任务当天不再显示；次日零点自动恢复为未完成并重新显示；
//   - 任务模板永久保存（AppState.DailyTodoTemplates），当日完成记录单独存储
//     （AppState.DailyTodoCompletions：模板Id -> "yyyy-MM-dd"），跨天自动失效。
// 本文件为全新文件，不修改原项目任何既有方法。
// ============================================================================
public sealed partial class PaperWindow
{
    // ---- 每日任务区块 UI 引用（新增字段） ----
    private StackPanel? _dailyTodoListPanel;   // 每日任务列表容器
    private TextBox? _dailyTodoInput;          // 每日任务名称输入框
    private TextBlock? _dailyTodoEmptyHint;    // 空列表提示文案

    /// <summary>今日日期字符串（yyyy-MM-dd），用于惰性判断"当日已完成"。</summary>
    private static string TodayKey =>
        DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>判断某条每日任务今天是否已完成（完成记录日期 == 今天）。</summary>
    private bool IsDailyTodoDoneToday(DailyTodoTemplate template) =>
        _controller.State.DailyTodoCompletions.TryGetValue(template.Id, out var date) &&
        string.Equals(date, TodayKey, StringComparison.Ordinal);

    /// <summary>
    /// 【新增功能】构建"每日任务"区块：标题 + 新增表单（仅任务名称）+ 任务列表。
    /// 区块独立展示，与限时任务分区。
    /// </summary>
    private UIElement BuildDailyTodoSection()
    {
        // ===== 【新增功能：卡片化（方案A+B混搭·极简浅蓝+白）】每日任务区块包成圆角卡片 =====
        // 卡片底 Theme.CardBrush（浅色纯白/深色深蓝灰）+ 淡蓝描边，圆角与限时卡片一致；
        // 区块内部不再画分隔线，靠卡片内边距与留白分区（极简）。
        var section = new StackPanel
        {
            Margin = new Thickness(0, 0, 0, 0)
        };

        // 区块标题（【新增功能】字改为黑色，与限时任务区块标题一致；深色主题浅色文字）
        var header = new TextBlock
        {
            Text = "每日任务",
            FontWeight = FontWeights.SemiBold,
            FontSize = AppTypography.Scale(12.5),
            Foreground = TextBrush,
            Margin = new Thickness(0, 0, 0, 4)
        };
        section.Children.Add(header);

        _dailyTodoInput = null;   // 由 BuildChromeTextBox 的 out 参数填充
        // 新增每日任务表单：仅任务名称输入框 + 添加按钮
        // 【新增功能：卡片化】输入框改圆角（BuildChromeTextBox），添加按钮改圆角主蓝（BuildAccentButton）
        var form = new Grid { Margin = new Thickness(0, 0, 0, 2) };
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var inputHost = BuildChromeTextBox(out _dailyTodoInput);
        // 回车快速添加
        _dailyTodoInput.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                AddDailyTodoFromInput();
                e.Handled = true;
            }
        };
        Grid.SetColumn(inputHost, 0);
        form.Children.Add(inputHost);

        // 【新增功能：卡片化】圆角主蓝添加按钮（悬停变深色/亮色），与限时任务添加按钮一致
        var addButton = BuildAccentButton("添加", "添加每日任务", AddDailyTodoFromInput);
        Grid.SetColumn(addButton, 1);
        form.Children.Add(addButton);

        section.Children.Add(form);

        // 每日任务列表（当天已完成的隐藏）
        _dailyTodoListPanel = new StackPanel();
        section.Children.Add(_dailyTodoListPanel);

        _dailyTodoEmptyHint = new TextBlock
        {
            Text = "今天暂无每日任务，输入名称后点击添加",
            FontSize = AppTypography.Scale(11),
            Foreground = WeakTextBrush,
            Opacity = 0.7,
            Margin = new Thickness(2, 2, 0, 2)
        };
        section.Children.Add(_dailyTodoEmptyHint);

        RebuildDailyTodoRows();

        // 外层卡片容器
        var card = new Border
        {
            Background = Theme.CardBrush,
            BorderBrush = Theme.CardBorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(Theme.IsPixelSkin ? 0 : RadiusControl),
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(4, 4, 4, 0),
            Child = section
        };
        return card;
    }

    /// <summary>从输入框添加一条每日任务（只保存名称，无截止日期）。</summary>
    private void AddDailyTodoFromInput()
    {
        if (_dailyTodoInput == null)
        {
            return;
        }

        var text = _dailyTodoInput.Text.Trim();
        if (text.Length == 0)
        {
            return;
        }

        var template = new DailyTodoTemplate
        {
            Text = text,
            Order = _controller.State.DailyTodoTemplates.Count
        };
        _controller.State.DailyTodoTemplates.Add(template);
        _controller.MarkDirty();

        _dailyTodoInput.Clear();
        RebuildDailyTodoRows();
        // 【新增功能：添加后定位可见】每日卡片在面板顶部，滚到顶让新加的每日任务立即可见
        // （避免面板滚动条停留在下方时误以为"添加了但没显示"）
        _todoScrollViewer?.ScrollToTop();
    }

    /// <summary>
    /// 【新增功能】刷新每日任务列表：当天已完成的隐藏，未完成的按顺序显示。
    /// 跨天/勾选/增删后调用。
    /// </summary>
    private void RebuildDailyTodoRows()
    {
        if (_dailyTodoListPanel == null)
        {
            return;
        }

        _dailyTodoListPanel.Children.Clear();

        var templates = _controller.State.DailyTodoTemplates
            .OrderBy(t => t.Order)
            .Where(t => !IsDailyTodoDoneToday(t))
            .ToList();

        if (_dailyTodoEmptyHint != null)
        {
            _dailyTodoEmptyHint.Visibility = templates.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        foreach (var template in templates)
        {
            _dailyTodoListPanel.Children.Add(BuildDailyTodoRow(template));
        }
    }

    /// <summary>构建单条每日任务行：复选框 + 任务名称 + 删除按钮。</summary>
    private UIElement BuildDailyTodoRow(DailyTodoTemplate template)
    {
        // 【新增功能：卡片化】行改成圆角 Border（圆角 4）：行悬停浅蓝底圆角高亮，
        // 删除按钮默认弱化（Opacity 0.55），行悬停时恢复清晰（次要控件弱化，极简）。
        var row = new Border
        {
            Margin = new Thickness(0, 1, 0, 1),
            Padding = new Thickness(2, 1, 2, 1),
            CornerRadius = new CornerRadius(Theme.IsPixelSkin ? 0 : 4),
            Background = Brushes.Transparent
        };
        // 【新增功能：卡片化】行悬停用 RowHoverBrush（比卡片底深一档，与限时任务行一致）
        row.MouseEnter += (_, _) => row.Background = Theme.RowHoverBrush;
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var check = new CheckBox
        {
            IsChecked = false,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Cursor = Cursors.Hand,
            Focusable = false,
            Style = CurrentTodoCheckBoxStyle()
        };
        // 勾选完成 → 记录今天日期 → 当天从列表隐藏（次日零点自动恢复）
        check.Checked += (_, _) =>
        {
            _controller.State.DailyTodoCompletions[template.Id] = TodayKey;
            _controller.MarkDirty();
            RebuildDailyTodoRows();
        };
        Grid.SetColumn(check, 0);
        grid.Children.Add(check);

        var text = new TextBlock
        {
            Text = template.Text,
            FontSize = AppTypography.Scale(12.5),
            Foreground = TextBrush,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(2, 1, 0, 1)
        };
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        // ===== 【新增功能：每日任务备注】备注编辑框（行内第二行，默认折叠）=====
        // 点击行内备注图标展开/收起；展开时聚焦，收起/失焦时保存到 template.Notes（空则置 null）。
        // 胶囊小窗口不渲染备注，备注只在展开面板可见。
        var notesBox = new TextBox
        {
            Visibility = Visibility.Collapsed,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 38,
            MaxHeight = 90,
            Margin = new Thickness(26, 2, 24, 2),
            Padding = new Thickness(4, 2, 4, 2),
            FontSize = AppTypography.Scale(11.5),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalContentAlignment = VerticalAlignment.Top,
            Background = Theme.TodoPaperBrush,
            BorderBrush = WeakTextBrush,
            BorderThickness = new Thickness(1),
            Foreground = TextBrush,
            CaretBrush = TextBrush,
            ToolTip = "任务备注（不会显示在胶囊小窗口）"
        };
        notesBox.GotKeyboardFocus += (_, _) => notesBox.BorderBrush = Theme.AccentBrush;
        notesBox.LostKeyboardFocus += (_, _) =>
        {
            notesBox.BorderBrush = WeakTextBrush;
            if (notesBox.Visibility == Visibility.Visible)
            {
                var trimmed = notesBox.Text.Trim();
                if (!string.Equals(trimmed, template.Notes ?? "", StringComparison.Ordinal))
                {
                    template.Notes = trimmed.Length == 0 ? null : trimmed;
                    _controller.MarkDirty();
                }
            }
        };

        // 删除每日任务（模板与当日完成记录一并删除）
        var deleteButton = new Button
        {
            Content = "✕",
            Width = 20,
            Height = 18,
            FontSize = AppTypography.Scale(10),
            Margin = new Thickness(2, 0, 0, 0),
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "删除该每日任务",
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent,
            Foreground = WeakTextBrush,
            Opacity = 0.55   // 【新增功能：卡片化】次要控件弱化，行悬停时恢复清晰
        };
        row.MouseEnter += (_, _) => deleteButton.Opacity = 1.0;
        row.MouseLeave += (_, _) => deleteButton.Opacity = 0.55;
        deleteButton.Click += (_, _) =>
        {
            _controller.State.DailyTodoTemplates.Remove(template);
            _controller.State.DailyTodoCompletions.Remove(template.Id);
            for (var i = 0; i < _controller.State.DailyTodoTemplates.Count; i++)
            {
                _controller.State.DailyTodoTemplates[i].Order = i;
            }
            _controller.MarkDirty();
            RebuildDailyTodoRows();
        };

        // ===== 【新增功能：每日任务备注】行内备注图标按钮 =====
        // 无备注时弱化（0.55，悬停恢复）；有备注时主蓝高亮常显；
        // 点击展开/收起行内备注编辑框（展开时载入当前备注并聚焦）。
        var notesButton = new Button
        {
            Content = "✎",
            Width = 20,
            Height = 18,
            FontSize = AppTypography.Scale(10),
            Margin = new Thickness(2, 0, 0, 0),
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "备注",
            Background = Brushes.Transparent,
            BorderBrush = Brushes.Transparent
        };
        if (!string.IsNullOrWhiteSpace(template.Notes))
        {
            notesButton.Foreground = Theme.AccentBrush;
            notesButton.Opacity = 1.0;
        }
        else
        {
            notesButton.Foreground = WeakTextBrush;
            notesButton.Opacity = 0.55;
        }
        notesButton.MouseEnter += (_, _) => notesButton.Opacity = 1.0;
        notesButton.MouseLeave += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(template.Notes))
            {
                notesButton.Opacity = 0.55;
            }
        };
        notesButton.Click += (_, _) =>
        {
            if (notesBox.Visibility != Visibility.Visible)
            {
                notesBox.Text = template.Notes ?? "";
                notesBox.Visibility = Visibility.Visible;
                notesBox.Focus();
            }
            else
            {
                notesBox.Visibility = Visibility.Collapsed;   // 收起（内容已在 LostFocus 时保存）
            }
        };

        // 备注按钮 + 删除按钮 合并放入操作列（列 2）
        var actionStack = new StackPanel { Orientation = Orientation.Horizontal };
        actionStack.Children.Add(notesButton);
        actionStack.Children.Add(deleteButton);
        Grid.SetColumn(actionStack, 2);
        grid.Children.Add(actionStack);

        // ===== 【新增功能：每日任务备注】行主体（grid）与备注编辑框（notesBox）纵向叠加 =====
        var contentGrid = new Grid();
        contentGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        contentGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(grid, 0);
        contentGrid.Children.Add(grid);
        Grid.SetRow(notesBox, 1);
        contentGrid.Children.Add(notesBox);

        row.Child = contentGrid;
        return row;
    }
}
