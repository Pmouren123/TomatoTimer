using System.ComponentModel;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using TomatoTimerPro.Controllers.Pages.Records;
using TomatoTimerPro.Tools.Markdown;
using TomatoTimerPro.Views.Components.Sketch;

namespace TomatoTimerPro.Views.Pages.Records;

public partial class RecordsPage : UserControl
{
    private const double TimelineCardStride = 100d;
    private sealed record DateTimelineCard(DateOnly Day, bool HasJournal, bool IsSelected, bool IsToday)
    {
        private static readonly Brush SelectedBackground = new SolidColorBrush(Color.FromRgb(237, 196, 172));
        private static readonly Brush TodayBackground = new SolidColorBrush(Color.FromRgb(218, 235, 204));
        private static readonly Brush TodaySelectedBackground = new SolidColorBrush(Color.FromRgb(181, 214, 164));
        private static readonly Brush NormalBackground = new SolidColorBrush(Color.FromRgb(255, 248, 232));
        private static readonly Brush SelectedBorder = new SolidColorBrush(Color.FromRgb(180, 138, 105));
        private static readonly Brush TodayBorder = new SolidColorBrush(Color.FromRgb(105, 145, 88));
        private static readonly Brush TodaySelectedBorder = new SolidColorBrush(Color.FromRgb(72, 116, 67));
        private static readonly Brush NormalBorder = new SolidColorBrush(Color.FromRgb(210, 193, 159));
        public static Brush NormalText { get; } = new SolidColorBrush(Color.FromRgb(81, 67, 56));
        public static Brush NormalCaption { get; } = new SolidColorBrush(Color.FromRgb(140, 118, 87));
        public static Brush NormalMonth { get; } = new SolidColorBrush(Color.FromRgb(160, 139, 108));
        public static Brush TodayText { get; } = new SolidColorBrush(Color.FromRgb(52, 102, 56));
        public static Brush TodayCaption { get; } = new SolidColorBrush(Color.FromRgb(74, 119, 75));
        private static readonly System.Globalization.CultureInfo Chinese = System.Globalization.CultureInfo.GetCultureInfo("zh-CN");
        public string DayNumber => Day.Day.ToString();
        public string MonthText => Day.ToString("yyyy年M月", Chinese);
        public string Weekday => Day.ToString("ddd", Chinese);
        public string WeekdayLabel => IsToday ? $"今天 · {Weekday}" : Weekday;
        public string Tooltip => $"{Day:yyyy年MM月dd日}" + (HasJournal ? " · 已写日志" : " · 暂无日志");
        public Brush Background => IsToday
            ? (IsSelected ? TodaySelectedBackground : TodayBackground)
            : (IsSelected ? SelectedBackground : NormalBackground);
        public Brush BorderBrush => IsToday
            ? (IsSelected ? TodaySelectedBorder : TodayBorder)
            : (IsSelected ? SelectedBorder : NormalBorder);
        public Visibility JournalVisibility => HasJournal ? Visibility.Visible : Visibility.Collapsed;
    }

    private sealed class TimelineCardView
    {
        private readonly SketchBorder _paper;
        private readonly TextBlock _weekday;
        private readonly TextBlock _dayNumber;
        private readonly TextBlock _month;
        private readonly Ellipse _journalDot;
        private readonly TextBlock _journalLabel;
        public Button Button { get; }

        public TimelineCardView(RoutedEventHandler click)
        {
            Button = new Button
            {
                Padding = new Thickness(0),
                Margin = new Thickness(0, 0, 8, 0),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Stretch,
                Cursor = Cursors.Hand
            };
            Button.Click += click;
            _weekday = CreateConstrainedTextBlock(13, DateTimelineCard.NormalCaption);
            _dayNumber = CreateConstrainedTextBlock(28, DateTimelineCard.NormalText, new FontFamily("Segoe Print"), FontWeights.Bold, new Thickness(0, 5, 0, 2));
            _month = CreateConstrainedTextBlock(12, DateTimelineCard.NormalMonth);
            _journalDot = new Ellipse { Width = 9, Height = 9, Fill = new SolidColorBrush(Color.FromRgb(217, 121, 98)), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 0) };
            _journalLabel = CreateConstrainedTextBlock(11, new SolidColorBrush(Color.FromRgb(157, 98, 77)), margin: new Thickness(0, 3, 0, 0));
            _journalLabel.Text = "有日志";
            _paper = new SketchBorder { Width = 92, Height = 128, Padding = new Thickness(9, 10, 9, 10), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(9), ClipToBounds = true };
            _paper.Child = new StackPanel { Children = { _weekday, _dayNumber, _month, _journalDot, _journalLabel } };
            Button.Content = _paper;
        }

        private static TextBlock CreateConstrainedTextBlock(double fontSize, Brush foreground, FontFamily? fontFamily = null, FontWeight? fontWeight = null, Thickness? margin = null)
        {
            var block = new TextBlock
            {
                Width = 74,
                FontSize = fontSize,
                FontWeight = fontWeight ?? FontWeights.Normal,
                Foreground = foreground,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = margin ?? new Thickness(0)
            };
            if (fontFamily is not null) block.FontFamily = fontFamily;
            return block;
        }

        public void Update(DateTimelineCard card)
        {
            Button.Tag = card.Day;
            Button.ToolTip = card.Tooltip;
            AutomationProperties.SetName(Button, card.Tooltip);
            _paper.Background = card.Background;
            _paper.BorderBrush = card.BorderBrush;
            _weekday.Text = card.WeekdayLabel;
            _weekday.Foreground = card.IsToday ? DateTimelineCard.TodayCaption : DateTimelineCard.NormalCaption;
            _dayNumber.Foreground = card.IsToday ? DateTimelineCard.TodayText : DateTimelineCard.NormalText;
            _dayNumber.Text = card.DayNumber;
            _month.Foreground = card.IsToday ? DateTimelineCard.TodayCaption : DateTimelineCard.NormalMonth;
            _month.Text = card.MonthText;
            _journalDot.Visibility = card.JournalVisibility;
            _journalLabel.Visibility = card.JournalVisibility;
        }
    }

    private RecordsPageController? _controller;
    private bool _syncingVisual;
    private bool _previewMode;
    private bool _sidebarCollapsed;
    private bool _dateChooserState;
    private bool _timelineRefreshQueued;
    private string? _timelineSignature;
    private readonly List<TimelineCardView> _timelinePool = [];
    private IReadOnlyList<DateTimelineCard> _timelineCards = [];

    public RecordsPage()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += (_, _) => SyncFromController();
        SetEditorMode(false);
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_controller is not null)
        {
            _controller.PropertyChanged -= OnControllerPropertyChanged;
            _controller.History.CollectionChanged -= OnHistoryChanged;
        }
        _controller = e.NewValue as RecordsPageController;
        if (_controller is not null)
        {
            _controller.PropertyChanged += OnControllerPropertyChanged;
            _controller.History.CollectionChanged += OnHistoryChanged;
        }
        SyncFromController();
    }

    private void OnControllerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RecordsPageController.EditorText) && !_syncingVisual)
            Dispatcher.BeginInvoke(new Action(SyncFromController));
        if (e.PropertyName is nameof(RecordsPageController.DateChooserVisible) or nameof(RecordsPageController.SelectedDate))
            Dispatcher.BeginInvoke(new Action(ApplyDateChooserState));
        if (e.PropertyName == nameof(RecordsPageController.HasLogEditor))
            Dispatcher.BeginInvoke(new Action(() => SetEditorMode(_previewMode)));
    }

    private void OnHistoryChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_controller?.DateChooserVisible == true) QueueDateTimelineRefresh();
    }

    private void SyncFromController()
    {
        if (_controller is null || _syncingVisual) return;
        var text = _controller.EditorText ?? string.Empty;
        _syncingVisual = true;
        try { DailyLogEditor.Document = MarkdownVisualDocument.ToFlowDocument(text); }
        finally { _syncingVisual = false; }
        UpdatePreview(text);
        SetEditorMode(_previewMode);
        ApplyDateChooserState();
    }

    private void ApplyDateChooserState()
    {
        if (_controller is null) return;
        var visible = _controller.DateChooserVisible;
        _dateChooserState = visible;
        DateChooserPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        EditorWorkspace.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
        // The date timeline is the outer navigation surface. Once a date is
        // chosen, the editor takes the full card width and the old time pointer
        // is removed from the editing surface.
        TimelineCard.Visibility = Visibility.Collapsed;
        TimelineColumn.Width = new GridLength(0);
        TimelineGap.Width = new GridLength(0);
        CollapseSidebarButton.Visibility = Visibility.Collapsed;
        ExpandSidebarButton.Visibility = Visibility.Collapsed;
        if (visible)
        {
            QueueDateTimelineRefresh();
            // AnimatedPageHost already fades the page in. Keeping a second
            // animation on this panel causes a visible flash when navigation
            // and the date-axis refresh complete in adjacent render passes.
            DateChooserPanel.BeginAnimation(UIElement.OpacityProperty, null);
            DateChooserPanel.Opacity = 1;
        }
    }

    private void QueueDateTimelineRefresh()
    {
        if (_timelineRefreshQueued || !IsLoaded) return;
        _timelineRefreshQueued = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _timelineRefreshQueued = false;
            RefreshDateTimeline();
        }), System.Windows.Threading.DispatcherPriority.Render);
    }

    private void RefreshDateTimeline()
    {
        if (_controller is null || DateTimelineHost is null) return;
        var today = DateOnly.FromDateTime(DateTime.Today);
        var selected = DateOnly.FromDateTime((_controller.SelectedDate ?? DateTime.Today).Date);
        var days = new HashSet<DateOnly>(Enumerable.Range(-30, 61).Select(offset => today.AddDays(offset)));
        var journalDays = _controller.History
            .Where(entry => Math.Abs(entry.Day.DayNumber - today.DayNumber) <= 180)
            .Select(entry => entry.Day)
            .ToHashSet();
        var signature = string.Join(",", journalDays.OrderBy(day => day).Select(day => day.DayNumber)) + $"|{selected.DayNumber}";
        if (StringComparer.Ordinal.Equals(_timelineSignature, signature))
        {
            ScrollToDate(selected);
            return;
        }
        _timelineSignature = signature;
        days.UnionWith(journalDays);
        days.Add(selected);
        var cards = days.OrderBy(day => day)
            .Select(day => new DateTimelineCard(day, journalDays.Contains(day), day == selected, day == today))
            .ToArray();
        _timelineCards = cards;
        for (var index = 0; index < cards.Length; index++)
        {
            if (index >= _timelinePool.Count)
            {
                var created = new TimelineCardView(OnDateTimelineItemClick);
                _timelinePool.Add(created);
                // Add each visual to the host only once. Subsequent refreshes
                // update the existing card in place instead of removing and
                // re-adding children, which avoids a full layout pass.
                DateTimelineHost.Children.Add(created.Button);
            }
            var view = _timelinePool[index];
            view.Update(cards[index]);
            view.Button.Visibility = Visibility.Visible;
        }

        // Keep the pool warm. Extra cards stay in the visual tree but are
        // collapsed, so a later refresh can reuse them without allocating new
        // WPF elements or changing the host's child collection.
        for (var index = cards.Length; index < _timelinePool.Count; index++)
        {
            var view = _timelinePool[index];
            view.Button.Tag = null;
            view.Button.Visibility = Visibility.Collapsed;
        }
        Dispatcher.BeginInvoke(new Action(() => ScrollToDate(selected)), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void ScrollToDate(DateOnly selected)
    {
        var index = _timelineCards.TakeWhile(card => card.Day < selected).Count();
        var estimated = Math.Max(0, index * 100d - DateTimelineScroller.ViewportWidth / 2 + 46);
        DateTimelineScroller.ScrollToHorizontalOffset(estimated);
    }

    private void OnDateTimelineMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (DateTimelineScroller.ScrollableWidth <= 0) return;
        ScrollDateTimelineByDelta(-e.Delta * 1.15);
        e.Handled = true;
    }

    private void OnDateTimelineLeftClick(object sender, RoutedEventArgs e) => ScrollDateTimelinePage(-1);
    private void OnDateTimelineRightClick(object sender, RoutedEventArgs e) => ScrollDateTimelinePage(1);

    private void ScrollDateTimelinePage(int direction)
    {
        if (DateTimelineScroller.ScrollableWidth <= 0 || direction == 0) return;
        var visibleCards = Math.Max(1, (int)Math.Floor(DateTimelineScroller.ViewportWidth / TimelineCardStride));
        var currentCard = Math.Max(0, (int)Math.Round(DateTimelineScroller.HorizontalOffset / TimelineCardStride));
        var targetCard = direction > 0
            ? ((currentCard / visibleCards) + 1) * visibleCards
            : Math.Max(0, ((currentCard - 1) / visibleCards) * visibleCards);
        ScrollDateTimelineTo(targetCard * TimelineCardStride);
    }

    private void ScrollDateTimelineByDelta(double delta)
    {
        ScrollDateTimelineTo(DateTimelineScroller.HorizontalOffset + delta);
    }

    private void ScrollDateTimelineTo(double target)
    {
        DateTimelineScroller.ScrollToHorizontalOffset(Math.Clamp(
            target,
            0,
            DateTimelineScroller.ScrollableWidth));
    }

    private async void OnDateTimelineItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: DateOnly day } || _controller is null) return;
        await _controller.SelectDateForEditingAsync(day.ToDateTime(TimeOnly.MinValue));
    }

    private async void OnChooseTodayClick(object sender, RoutedEventArgs e)
    {
        if (_controller is not null) await _controller.SelectDateForEditingAsync(DateTime.Today);
    }

    private void OnVisualEditorTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncingVisual || _controller is null || !_controller.CanEdit) return;
        var markdown = MarkdownVisualDocument.ToMarkdown(DailyLogEditor.Document);
        _syncingVisual = true;
        try { _controller.EditorText = markdown; }
        finally { _syncingVisual = false; }
        UpdatePreview(markdown);
    }

    private void UpdatePreview(string markdown)
    {
        if (MarkdownPreview is not null) MarkdownPreview.Document = MarkdownVisualDocument.ToFlowDocument(markdown);
    }

    private void OnEditorSelectionChanged(object sender, RoutedEventArgs e)
    {
        // Kept as a named event so toolbar clicks can be added without introducing
        // a WPF animation or losing the current selection.
    }

    private void OnModeClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag }) SetEditorMode(tag == "preview");
    }

    private void SetEditorMode(bool preview)
    {
        _previewMode = preview;
        if (DailyLogEditor is null || MarkdownPreview is null || EditorScrollViewer is null) return;
        var hasEditor = _controller?.HasLogEditor == true;
        EditorScrollViewer.Visibility = !preview && hasEditor ? Visibility.Visible : Visibility.Collapsed;
        MarkdownPreview.Visibility = preview && hasEditor ? Visibility.Visible : Visibility.Collapsed;
        VisualModeButton.Background = preview ? new SolidColorBrush(Color.FromRgb(245, 236, 216)) : new SolidColorBrush(Color.FromRgb(237, 196, 172));
        PreviewModeButton.Background = preview ? new SolidColorBrush(Color.FromRgb(237, 196, 172)) : new SolidColorBrush(Color.FromRgb(245, 236, 216));
        if (!preview) DailyLogEditor.Focus();
    }

    private void OnToggleSidebar(object sender, RoutedEventArgs e)
    {
        _sidebarCollapsed = !_sidebarCollapsed;
        TimelineColumn.Width = _sidebarCollapsed ? new GridLength(0) : new GridLength(270);
        TimelineGap.Width = _sidebarCollapsed ? new GridLength(0) : new GridLength(17);
        TimelineCard.Visibility = _sidebarCollapsed ? Visibility.Collapsed : Visibility.Visible;
        CollapseSidebarButton.Visibility = _sidebarCollapsed ? Visibility.Collapsed : Visibility.Visible;
        ExpandSidebarButton.Visibility = _sidebarCollapsed ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnVisualToolClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag } || !DailyLogEditor.IsEnabled || _previewMode) return;
        DailyLogEditor.Focus();
        switch (tag)
        {
            case "undo": ApplicationCommands.Undo.Execute(null, DailyLogEditor); break;
            case "redo": ApplicationCommands.Redo.Execute(null, DailyLogEditor); break;
            case "bold": EditingCommands.ToggleBold.Execute(null, DailyLogEditor); break;
            case "italic": EditingCommands.ToggleItalic.Execute(null, DailyLogEditor); break;
            case "underline": EditingCommands.ToggleUnderline.Execute(null, DailyLogEditor); break;
            case "strike": ToggleStrikeThrough(); break;
            case "h1": ApplyHeading(1); break;
            case "h2": ApplyHeading(2); break;
            case "h3": ApplyHeading(3); break;
            case "body": ApplyHeading(0); break;
            case "bullet": EditingCommands.ToggleBullets.Execute(null, DailyLogEditor); break;
            case "number": EditingCommands.ToggleNumbering.Execute(null, DailyLogEditor); break;
            case "quote": ApplyQuote(); break;
            case "code": ApplyCode(); break;
            case "link":
                LinkTextInput.Text = DailyLogEditor.Selection.IsEmpty ? "链接文字" : DailyLogEditor.Selection.Text.Trim();
                LinkUrlInput.Text = "https://";
                LinkPanel.Visibility = Visibility.Visible;
                LinkUrlInput.Focus();
                return;
            case "rule": InsertText("\n---\n"); break;
            case "time": InsertText(DateTime.Now.ToString("HH:mm") + " "); break;
        }
        PushVisualText();
    }

    private void OnInsertLinkClick(object sender, RoutedEventArgs e)
    {
        if (!DailyLogEditor.IsEnabled || _previewMode) return;
        var label = string.IsNullOrWhiteSpace(LinkTextInput.Text) ? "链接文字" : LinkTextInput.Text.Trim();
        var url = string.IsNullOrWhiteSpace(LinkUrlInput.Text) ? "https://" : LinkUrlInput.Text.Trim();
        InsertLink(label, url);
        LinkPanel.Visibility = Visibility.Collapsed;
        PushVisualText();
    }

    private void OnEditorPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
        if (e.Key == Key.B) { EditingCommands.ToggleBold.Execute(null, DailyLogEditor); e.Handled = true; }
        else if (e.Key == Key.I) { EditingCommands.ToggleItalic.Execute(null, DailyLogEditor); e.Handled = true; }
        else if (e.Key == Key.U) { EditingCommands.ToggleUnderline.Execute(null, DailyLogEditor); e.Handled = true; }
    }

    private void ApplyHeading(int level)
    {
        foreach (var paragraph in SelectedParagraphs())
        {
            MarkdownVisualDocument.SetHeadingLevel(paragraph, level);
            MarkdownVisualDocument.SetBlockKind(paragraph, null);
            paragraph.FontSize = level switch { 1 => 25, 2 => 21, 3 => 18, _ => 16 };
            paragraph.FontWeight = level == 0 ? FontWeights.Normal : FontWeights.Bold;
            paragraph.Margin = level == 0 ? new Thickness(0, 3, 0, 6) : new Thickness(0, level == 1 ? 12 : 8, 0, 5);
            paragraph.Background = Brushes.Transparent;
            paragraph.Padding = new Thickness(0);
        }
    }

    private void ApplyQuote()
    {
        foreach (var paragraph in SelectedParagraphs())
        {
            var quoted = MarkdownVisualDocument.GetBlockKind(paragraph) == "quote";
            MarkdownVisualDocument.SetBlockKind(paragraph, quoted ? null : "quote");
            MarkdownVisualDocument.SetHeadingLevel(paragraph, 0);
            paragraph.FontSize = 16;
            paragraph.FontWeight = FontWeights.Normal;
            paragraph.Margin = quoted ? new Thickness(0, 3, 0, 6) : new Thickness(18, 4, 0, 4);
            paragraph.Padding = quoted ? new Thickness(0) : new Thickness(10, 4, 8, 4);
            paragraph.Background = quoted ? Brushes.Transparent : new SolidColorBrush(Color.FromRgb(247, 239, 221));
            paragraph.Foreground = quoted ? new SolidColorBrush(Color.FromRgb(67, 59, 52)) : new SolidColorBrush(Color.FromRgb(126, 103, 76));
        }
    }

    private void ApplyCode()
    {
        if (DailyLogEditor.Selection.IsEmpty)
        {
            var start = DailyLogEditor.CaretPosition;
            start.InsertTextInRun("代码");
            var end = start.GetPositionAtOffset(2, LogicalDirection.Forward) ?? start;
            DailyLogEditor.Selection.Select(start, end);
        }
        var range = DailyLogEditor.Selection;
        range.ApplyPropertyValue(TextElement.FontFamilyProperty, new FontFamily("Consolas"));
        range.ApplyPropertyValue(TextElement.BackgroundProperty, new SolidColorBrush(Color.FromRgb(242, 233, 214)));
        range.ApplyPropertyValue(TextElement.ForegroundProperty, new SolidColorBrush(Color.FromRgb(113, 82, 57)));
    }

    private void ToggleStrikeThrough()
    {
        var range = DailyLogEditor.Selection;
        var decorations = range.GetPropertyValue(Inline.TextDecorationsProperty) as TextDecorationCollection;
        var hasStrike = decorations?.Any(item => item.Location == TextDecorationLocation.Strikethrough) == true;
        range.ApplyPropertyValue(Inline.TextDecorationsProperty, hasStrike ? new TextDecorationCollection() : TextDecorations.Strikethrough);
    }

    private void InsertLink(string label, string url)
    {
        var selection = DailyLogEditor.Selection;
        var start = selection.Start;
        selection.Text = label;
        var end = start.GetPositionAtOffset(label.Length, LogicalDirection.Forward) ?? start;
        var range = new TextRange(start, end);
        range.ApplyPropertyValue(Inline.TextDecorationsProperty, TextDecorations.Underline);
        range.ApplyPropertyValue(TextElement.ForegroundProperty, new SolidColorBrush(Color.FromRgb(92, 119, 139)));
        foreach (var inline in AllInlines(DailyLogEditor.Document).Where(inline =>
                     inline.ContentEnd.CompareTo(start) >= 0 && inline.ContentStart.CompareTo(end) <= 0))
            MarkdownVisualDocument.SetLinkTarget(inline, url);
        DailyLogEditor.Selection.Select(start, end);
    }

    private void InsertText(string value)
    {
        var start = DailyLogEditor.Selection.Start;
        DailyLogEditor.Selection.Text = value;
        DailyLogEditor.CaretPosition = start.GetPositionAtOffset(value.Length, LogicalDirection.Forward) ?? DailyLogEditor.CaretPosition;
    }

    private IEnumerable<Paragraph> SelectedParagraphs()
    {
        var start = DailyLogEditor.Selection.Start;
        var end = DailyLogEditor.Selection.End;
        if (start.CompareTo(end) > 0) (start, end) = (end, start);
        return AllParagraphs(DailyLogEditor.Document).Where(paragraph =>
            paragraph.ContentEnd.CompareTo(start) >= 0 && paragraph.ContentStart.CompareTo(end) <= 0).ToArray();
    }

    private static IEnumerable<Paragraph> AllParagraphs(FlowDocument document)
    {
        foreach (var block in document.Blocks)
        {
            if (block is Paragraph paragraph) yield return paragraph;
            else if (block is System.Windows.Documents.List list)
                foreach (var item in list.ListItems)
                    foreach (var child in item.Blocks.OfType<Paragraph>()) yield return child;
        }
    }

    private static IEnumerable<Inline> AllInlines(FlowDocument document)
    {
        foreach (var paragraph in AllParagraphs(document))
            foreach (var inline in AllInlines(paragraph.Inlines)) yield return inline;
    }

    private static IEnumerable<Inline> AllInlines(InlineCollection inlines)
    {
        foreach (var inline in inlines)
        {
            yield return inline;
            if (inline is Span span)
                foreach (var child in AllInlines(span.Inlines)) yield return child;
        }
    }

    private void PushVisualText()
    {
        if (_controller is null || !_controller.CanEdit) return;
        var markdown = MarkdownVisualDocument.ToMarkdown(DailyLogEditor.Document);
        _syncingVisual = true;
        try { _controller.EditorText = markdown; }
        finally { _syncingVisual = false; }
        UpdatePreview(markdown);
    }
}
