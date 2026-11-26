using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Serilog;
using Transfer.Core.Remote;

namespace RemoteFiles.Desktop;

/// <summary>右侧标注表单。负责布局、弹层与焦点，业务和异步状态由 Model 管理。</summary>
public sealed class AnnotationPanel : UserControl, IDisposable
{
    private readonly AnnotationViewModel model;
    private bool disposed;
    private WrapPanel? remarkTags;
    private Button? categoryPicker, planPicker, submitButton, revokeButton, annotationHistoryButton;
    private TextBlock? categoryLabel, planLabel, annotationStatus, annotationImageName, annotationSummary, annotationError, annotationHint;
    private readonly TextBlock annotationImageDate = new()
    {
        FontSize = 12, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Right,
        Margin = new Thickness(0, 4, 0, 0), Visibility = Visibility.Collapsed
    };
    private RotateTransform? annotationHistoryChevron;
    private StackPanel? annotationHistory;
    private TextBox? noteBox, operatorBox;
    private Popup? pickerPopup;
    public AnnotationViewModel Model => model;
    public event Action<AnnotationWriteKind>? WriteSucceeded;

    public AnnotationPanel(AnnotationViewModel model, ResourceDictionary resources)
    {
        this.model = model;
        Resources = resources;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        Focusable = false;
        FocusVisualStyle = null;
        Content = BuildAnnotationSide((Brush)FindResource("HairlineBrush"));
        model.PropertyChanged += ModelChanged;
        model.WriteSucceeded += Written;
        UpdateCurrentAnnotationStatus();
    }
    private Binding DraftBinding(string property) => new(property)
    {
        Source = model, Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
    };
    private Style? ThemeStyle(string key) => TryFindResource(key) as Style;
    public void SelectImage(RemoteEntry image)
    {
        if (pickerPopup is not null) pickerPopup.IsOpen = false;
        annotationImageDate.Text = FormattableString.Invariant($"{image.CreatedLocal:yyyy-MM-dd HH:mm:ss}");
        annotationImageDate.Visibility = Visibility.Visible;
        model.SelectImage(image.Name);
    }
    public async Task LoadAsync()
    {
        await model.LoadDictionaryAsync();
        if (disposed) return;
        RefreshDictionaryHint();
        Log.Information("标注字典加载完成；归类 {Categories}，方案 {Plans}，备注标签 {Remarks}",
            model.Dictionary.Categories.Length, model.Dictionary.Plans.Length, model.Dictionary.Remarks.Length);
    }
    private void ModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (disposed) return;
        switch (e.PropertyName)
        {
            case nameof(AnnotationViewModel.Category): categoryLabel!.Text = model.Category.Length == 0 ? "请选择异常归类" : model.Category; break;
            case nameof(AnnotationViewModel.Plan): planLabel!.Text = model.Plan.Length == 0 ? "请选择异常处理方案" : model.Plan; break;
            case nameof(AnnotationViewModel.History):
            case nameof(AnnotationViewModel.IsWriting):
                UpdateCurrentAnnotationStatus();
                break;
            case nameof(AnnotationViewModel.Dictionary): RefreshRemarkTags(); RefreshDictionaryHint(); break;
            case nameof(AnnotationViewModel.Error):
                annotationError!.Text = model.Error;
                annotationError.Visibility = model.Error.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
                break;
            case nameof(AnnotationViewModel.IsBusy):
                submitButton!.IsEnabled = revokeButton!.IsEnabled = !model.IsBusy;
                break;
        }
    }
    private void RefreshDictionaryHint()
    {
        bool empty = model.Dictionary.Categories.Length == 0 && model.Dictionary.Plans.Length == 0;
        annotationHint!.Text = empty ? "服务端未配置标注字典（RpaDataItems / RpaDataItemDetails），下拉暂时为空。" : "";
        annotationHint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
    }
    private void Written(AnnotationWriteKind kind) => WriteSucceeded?.Invoke(kind);
    private Task SubmitAnnotationAsync() => WriteAsync(AnnotationWriteKind.Submit);
    private Task RevokeAnnotationAsync() => WriteAsync(AnnotationWriteKind.Revoke);
    private async Task WriteAsync(AnnotationWriteKind kind)
    {
        if (model.IsBusy || disposed) return;
        bool success = kind == AnnotationWriteKind.Submit ? await model.SubmitAsync() : await model.RevokeAsync();
        if (success || disposed || model.Error.Length == 0) return;
        FrameworkElement? field = model.ErrorField switch
        {
            nameof(AnnotationViewModel.Category) => categoryPicker,
            nameof(AnnotationViewModel.Plan) => planPicker,
            nameof(AnnotationViewModel.Operator) => operatorBox,
            nameof(AnnotationViewModel.Note) => noteBox,
            _ => null
        };
        field?.BringIntoView(); field?.Focus();
        if (field is null)
        {
            string failure = kind == AnnotationWriteKind.Submit ? "提交失败" : "撤销失败";
            Log.Warning("{Operation}；图片 {ImageName}；原因 {Error}", failure, model.ImageName, model.Error);
            Toast.Show(Window.GetWindow(this), failure + "：" + model.Error, ToastKind.Danger, 3200);
        }
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        model.PropertyChanged -= ModelChanged;
        model.WriteSucceeded -= Written;
        if (pickerPopup is not null) pickerPopup.IsOpen = false;
        model.Dispose();
    }

    private Border BuildAnnotationSide(Brush line)
    {
        var stack = new StackPanel { Margin = new Thickness(12, 0, 12, 12) };
        var title = new TextBlock { Text = "异常标注", FontSize = 12, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        title.SetResourceReference(TextBlock.ForegroundProperty, "InkBrush");
        var header = new Border { Height = 42, Padding = new Thickness(12, 0, 12, 0), Child = title };
        DockPanel.SetDock(header, Dock.Top);

        stack.Children.Add(BuildAnnotationStatusCard(line));

        stack.Children.Add(FieldLabel("异常归类", 16));
        (categoryPicker, categoryLabel) = CreatePicker("请选择异常归类");
        stack.Children.Add(CreatePickerRow(categoryPicker, categoryLabel, AnnotationDictionaryKind.Category, "异常归类"));

        stack.Children.Add(FieldLabel("异常处理方案", 12));
        (planPicker, planLabel) = CreatePicker("请选择异常处理方案");
        stack.Children.Add(CreatePickerRow(planPicker, planLabel, AnnotationDictionaryKind.Plan, "异常处理方案"));

        stack.Children.Add(FieldLabel("备注（可选）", 12));
        var addRemark = new Button
        {
            Content = new System.Windows.Shapes.Path { Data = (Geometry)FindResource("IconAddGeometry"), Style = ThemeStyle("IconGlyph") },
            Style = ThemeStyle("AnnotationFieldButton"),
            Padding = new Thickness(0), Margin = new Thickness(6, 0, 0, 0),
            ToolTip = "新增备注标签", VerticalAlignment = VerticalAlignment.Top
        };
        addRemark.SetResourceReference(WidthProperty, "FormControlHeight");
        System.Windows.Automation.AutomationProperties.SetName(addRemark, "新增备注标签");
        addRemark.Click += (_, _) => ShowDictionaryEditor(addRemark, null, AnnotationDictionaryKind.Remark, "备注标签");
        remarkTags = new WrapPanel();
        stack.Children.Add(new ScrollViewer
        {
            Content = remarkTags, MaxHeight = 100, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        });
        noteBox = new TextBox
        {
            Height = 72,
            Style = ThemeStyle("AnnotationFieldInput"),
            Margin = new Thickness(0),
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalContentAlignment = VerticalAlignment.Top,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxLength = AnnotationStore.NoteLimit
        };
        System.Windows.Automation.AutomationProperties.SetName(noteBox, "备注");
        noteBox.SetBinding(TextBox.TextProperty, DraftBinding(nameof(AnnotationViewModel.Note)));
        var remarkRow = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        remarkRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        remarkRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        remarkRow.Children.Add(noteBox);
        Grid.SetColumn(addRemark, 1);
        remarkRow.Children.Add(addRemark);
        stack.Children.Add(remarkRow);

        stack.Children.Add(FieldLabel("操作人", 12));
        operatorBox = new TextBox
        {
            Style = ThemeStyle("AnnotationFieldInput"),
            Margin = new Thickness(0, 6, (double)FindResource("FormControlHeight") + 6, 0),
            Text = model.Operator
        };
        System.Windows.Automation.AutomationProperties.SetName(operatorBox, "操作人");
        operatorBox.SetBinding(TextBox.TextProperty, DraftBinding(nameof(AnnotationViewModel.Operator)));
        stack.Children.Add(operatorBox);

        submitButton = new Button
        {
            Content = "提交标注",
            MinWidth = 112,
            Style = ThemeStyle("FormPrimaryButton")
        };
        submitButton.Click += async (_, _) => await SubmitAnnotationAsync();
        revokeButton = new Button
        {
            Content = "撤销标注",
            ToolTip = "删除当前图片的全部标注记录",
            MinWidth = 96,
            Margin = new Thickness(0, 0, 8, 0),
            Style = ThemeStyle("FormDangerButton"),
            Visibility = Visibility.Collapsed
        };
        revokeButton.Click += async (_, _) => await RevokeAnnotationAsync();
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { revokeButton, submitButton }
        };

        annotationError = new TextBlock
        {
            FontSize = 11,
            Margin = new Thickness(0, 0, 0, 8),
            MaxHeight = 60,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Visibility = Visibility.Collapsed
        };
        annotationError.SetBinding(ToolTipProperty, new Binding(nameof(TextBlock.Text)) { Source = annotationError });
        annotationError.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");

        annotationHint = new TextBlock { FontSize = 10, Margin = new Thickness(0, 10, 0, 0), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        annotationHint.SetResourceReference(TextBlock.ForegroundProperty, "SubtleBrush");
        stack.Children.Add(annotationHint);

        var scroll = new ScrollViewer
        {
            Content = stack,
            FocusVisualStyle = null,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        var footer = new Border
        {
            BorderBrush = line,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(12, 10, 12, 12),
            Child = new StackPanel { Children = { annotationError, actions } }
        };
        DockPanel.SetDock(footer, Dock.Bottom);
        var layout = new DockPanel { Children = { header, footer, scroll } };
        var side = new Border { BorderBrush = line, BorderThickness = new Thickness(1), CornerRadius = (CornerRadius)FindResource("PanelCornerRadius"), Child = layout };
        side.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        return side;
    }

    private Border BuildAnnotationStatusCard(Brush line)
    {
        annotationStatus = new TextBlock { FontSize = 11, FontWeight = FontWeights.SemiBold };
        annotationHistoryButton = new Button
        {
            Style = ThemeStyle("AnnotationHistoryButton"),
            IsEnabled = false
        };
        annotationHistoryChevron = new RotateTransform();
        var chevron = new System.Windows.Shapes.Path
        {
            Data = (Geometry)FindResource("IconChevronGeometry"),
            Style = ThemeStyle("IconGlyph"),
            Width = 10,
            Height = 6,
            Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 0, 0),
            RenderTransform = annotationHistoryChevron,
            RenderTransformOrigin = new Point(0.5, 0.5)
        };
        chevron.SetBinding(VisibilityProperty, new Binding(nameof(IsEnabled))
        {
            Source = annotationHistoryButton,
            Converter = new BooleanToVisibilityConverter()
        });
        annotationHistoryButton.Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { annotationStatus, chevron } };
        annotationHistoryButton.Click += (_, _) => ToggleAnnotationHistory();
        var header = new DockPanel();
        DockPanel.SetDock(annotationHistoryButton, Dock.Right);
        header.Children.Add(annotationHistoryButton);
        var caption = FieldLabel("当前图片", 0);
        caption.VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(caption);

        annotationImageName = new TextBlock
        {
            FontSize = 12, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap,
            LineHeight = 16, LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            Height = 32, TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 6, 0, 0)
        };
        annotationImageDate.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");
        annotationSummary = new TextBlock
        {
            FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 0), Visibility = Visibility.Collapsed
        };
        annotationSummary.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        annotationHistory = new StackPanel
        {
            Margin = new Thickness(0, 6, 0, 0),
            Visibility = Visibility.Collapsed
        };
        System.Windows.Automation.AutomationProperties.SetName(annotationHistory, "标注历史");
        var card = new Border
        {
            Child = new StackPanel { Children = { header, annotationImageName, annotationImageDate, annotationSummary, annotationHistory } },
            Margin = new Thickness(0, 10, 0, 0), Padding = new Thickness(10, 8, 10, 8),
            CornerRadius = new CornerRadius(6), BorderBrush = line, BorderThickness = new Thickness(1)
        };
        card.SetResourceReference(Border.BackgroundProperty, "HeaderBrush");
        return card;
    }

    private void ToggleAnnotationHistory()
    {
        if (annotationHistory is null) return;
        if (annotationHistory.Visibility == Visibility.Visible)
        {
            annotationHistory.Visibility = Visibility.Collapsed;
            annotationSummary!.Visibility = Visibility.Visible;
            annotationHistoryChevron!.Angle = 0;
            return;
        }
        var history = model.History;
        if (history.Count == 0) return;
        annotationHistory.Children.Clear();
        foreach (var record in history) annotationHistory.Children.Add(BuildHistoryRecord(record));
        annotationSummary!.Visibility = Visibility.Collapsed;
        annotationHistory.Visibility = Visibility.Visible;
        annotationHistoryChevron!.Angle = 180;
    }

    private Border BuildHistoryRecord(RemoteAnnotation record)
    {
        var fields = new Grid();
        fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });
        fields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        void AddField(string name, string? value)
        {
            int row = fields.RowDefinitions.Count;
            fields.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = FieldLabel(name, 0);
            var text = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(value) ? "未记录" : value,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11,
                Margin = new Thickness(0, 0, 0, 3)
            };
            text.SetResourceReference(TextBlock.ForegroundProperty, "InkBrush");
            Grid.SetRow(label, row);
            Grid.SetRow(text, row);
            Grid.SetColumn(text, 1);
            fields.Children.Add(label);
            fields.Children.Add(text);
        }

        AddField("时间", AnnotationContext.GetDisplayTime(record.UpdatedAt)?.ToString("yyyy.MM.dd HH:mm:ss"));
        AddField("操作人", record.Operator);
        AddField("归类", record.CategoryName);
        AddField("处理方案", record.PlanName);
        if (!string.IsNullOrWhiteSpace(record.Note)) AddField("备注", record.Note);

        var item = new Border
        {
            Child = fields,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(0, 8, 0, 5)
        };
        item.SetResourceReference(Border.BorderBrushProperty, "HairlineBrush");
        return item;
    }

    private TextBlock FieldLabel(string text, double top)
    {
        var label = new TextBlock { Text = text, FontSize = 11, Margin = new Thickness(0, top, 0, 0) };
        label.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        return label;
    }

    // 用按钮 + 主题化菜单代替 ComboBox：暗色下原生 ComboBox 会露出浅色弹层。
    private (Button Button, TextBlock Label) CreatePicker(string placeholder)
    {
        var label = new TextBlock
        {
            Text = placeholder,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 0, 18, 0)
        };
        var chevron = new System.Windows.Shapes.Path
        {
            Data = (Geometry)FindResource("IconChevronGeometry"),
            Style = ThemeStyle("IconGlyph"),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };
        chevron.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "MutedBrush");
        var content = new Grid();
        content.Children.Add(label);
        content.Children.Add(chevron);
        var button = new Button
        {
            Content = content,
            Style = ThemeStyle("AnnotationFieldButton"),
            Padding = new Thickness(10, 0, 10, 0),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ToolTip = placeholder
        };
        button.Click += (_, _) => ShowPickerMenu(button, label, placeholder);
        return (button, label);
    }

    private Grid CreatePickerRow(Button picker, TextBlock label, AnnotationDictionaryKind kind, string fieldName)
    {
        System.Windows.Automation.AutomationProperties.SetName(picker, fieldName);
        var row = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var add = new Button
        {
            Content = new System.Windows.Shapes.Path { Data = (Geometry)FindResource("IconAddGeometry"), Style = ThemeStyle("IconGlyph") },
            Style = ThemeStyle("AnnotationFieldButton"),
            Padding = new Thickness(0),
            Margin = new Thickness(6, 0, 0, 0),
            ToolTip = "新增" + fieldName
        };
        add.SetResourceReference(WidthProperty, "FormControlHeight");
        System.Windows.Automation.AutomationProperties.SetName(add, "新增" + fieldName);
        add.Click += (_, _) => ShowDictionaryEditor(picker, label, kind, fieldName);
        row.Children.Add(picker);
        Grid.SetColumn(add, 1);
        row.Children.Add(add);
        return row;
    }

    private void ShowDictionaryEditor(Button anchor, TextBlock? label, AnnotationDictionaryKind kind, string fieldName)
    {
        if (model.IsBusy) return;
        var input = new TextBox { MaxLength = AnnotationStore.NameLimit, Margin = new Thickness(0, 6, 0, 0), Style = ThemeStyle("AnnotationFieldInput") };
        System.Windows.Automation.AutomationProperties.SetName(input, fieldName + "名称");
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed };
        error.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
        var save = new Button { Content = "保存", MinWidth = 64, Style = ThemeStyle("FormPrimaryButton") };
        var cancel = new Button { Content = "取消", MinWidth = 64, Margin = new Thickness(0, 0, 8, 0), Style = ThemeStyle("FormButton") };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0),
            Children = { cancel, save }
        };
        var editor = new StackPanel { Margin = new Thickness(8), Children = { FieldLabel("新增" + fieldName, 0), input, error, buttons } };
        var popup = CreateAnnotationPopup(anchor, editor);
        cancel.Click += (_, _) => popup.IsOpen = false;
        popup.Opened += (_, _) => input.Focus();
        editor.PreviewKeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; await SaveAsync(); }
            else if (e.Key == Key.Escape && !model.IsAddingDictionary) { e.Handled = true; popup.IsOpen = false; }
        };
        save.Click += async (_, _) => await SaveAsync();
        popup.IsOpen = true;

        async Task SaveAsync()
        {
            if (model.IsBusy) return;
            error.Visibility = Visibility.Collapsed;
            editor.IsEnabled = false;
            try
            {
                bool saved = await model.AddDictionaryItemAsync(kind, input.Text);
                if (disposed) return;
                if (saved)
                {
                    annotationHint!.Visibility = Visibility.Collapsed;
                    popup.IsOpen = false;
                    Toast.Show(Window.GetWindow(this), "字典已保存");
                }
                else
                {
                    error.Text = model.Error;
                    error.Visibility = Visibility.Visible;
                    input.Focus();
                }
            }
            finally { editor.IsEnabled = true; }
        }
    }

    private void SetPickerValue(Button picker, TextBlock label, string name)
    {
        if (ReferenceEquals(picker, categoryPicker)) model.Category = name;
        else model.Plan = name;
    }

    private void RefreshRemarkTags()
    {
        if (remarkTags is null) return;
        remarkTags.Children.Clear();
        foreach (var item in model.Dictionary?.Remarks ?? [])
        {
            var tag = new Button
            {
                Content = new TextBlock { Text = item.Name, TextWrapping = TextWrapping.Wrap },
                Style = ThemeStyle("FormButton"), MinHeight = 26, Height = double.NaN,
                Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 6, 6, 0),
                MaxWidth = 220, ToolTip = "添加到备注：" + item.Name
            };
            System.Windows.Automation.AutomationProperties.SetName(tag, "备注标签：" + item.Name);
            tag.Click += (_, _) =>
            {
                if (model.AppendRemark(item.Name)) noteBox!.CaretIndex = model.Note.Length;
                else if (model.Error.Length > 0) Toast.Show(Window.GetWindow(this), model.Error, ToastKind.Danger);
            };
            remarkTags.Children.Add(tag);
        }
    }

    // 自绘下拉：弹出层里放一列按钮，避开 ContextMenu/MenuItem 原生模板的勾选列白条。
    private void ShowPickerMenu(Button button, TextBlock label, string placeholder)
    {
        if (model.IsBusy) return;
        var items = ReferenceEquals(button, categoryPicker) ? model.Dictionary?.Categories : model.Dictionary?.Plans;
        if (items is null || items.Length == 0)
        {
            annotationHint!.Text = "服务端未配置标注字典，暂时无法选择。";
            annotationHint.Visibility = Visibility.Visible;
            return;
        }
        var stack = new StackPanel();
        var ghost = ThemeStyle("GhostButton");
        foreach (var item in items)
        {
            var option = new Button
            {
                Content = new TextBlock { Text = item.Name, TextTrimming = TextTrimming.CharacterEllipsis },
                HorizontalContentAlignment = HorizontalAlignment.Left,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(10, 5, 10, 5),
                Margin = new Thickness(2, 1, 2, 1),
                MinHeight = 28
            };
            if (ghost is not null) option.Style = ghost;
            option.Click += (_, _) =>
            {
                SetPickerValue(button, label, item.Name);
                if (pickerPopup is not null) pickerPopup.IsOpen = false;
            };
            stack.Children.Add(option);
        }
        var scroll = new ScrollViewer
        {
            Content = stack,
            MaxHeight = 320,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        CreateAnnotationPopup(button, scroll).IsOpen = true;
    }

    private Popup CreateAnnotationPopup(Button anchor, UIElement content)
    {
        if (pickerPopup is not null) pickerPopup.IsOpen = false;
        var border = new Border
        {
            Style = ThemeStyle("PopupSurface"),
            Child = content
        };
        var popup = new Popup
        {
            PlacementTarget = anchor,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true,
            Child = border,
            Width = Math.Max(anchor.ActualWidth, 240)
        };
        pickerPopup = popup;
        return popup;
    }

    private void UpdateCurrentAnnotationStatus()
    {
        string imageName = model.ImageName;
        var history = model.History;
        var latest = history.FirstOrDefault();
        bool annotated = latest is not null;
        annotationImageName!.Text = imageName;
        string? historyError = model.HistoryReadError;
        annotationStatus!.Text = model.PendingWrite switch
        {
            AnnotationWriteKind.Submit when model.IsWriting => "正在提交…",
            AnnotationWriteKind.Revoke when model.IsWriting => "正在撤销…",
            _ when historyError is not null => "状态未知",
            _ => annotated ? $"已标注 {history.Count} 次" : "未标注"
        };
        annotationStatus.SetResourceReference(TextBlock.ForegroundProperty,
            model.IsWriting ? "AccentBrush" : annotated ? "SuccessBrush" : "WarningBrush");
        annotationHistoryButton!.IsEnabled = annotated;
        annotationHistoryButton.ToolTip = historyError ?? (annotated ? "查看标注历史" : null);
        ToolTipService.SetShowOnDisabled(annotationHistoryButton, historyError is not null);
        System.Windows.Automation.AutomationProperties.SetName(annotationHistoryButton,
            annotated ? $"{annotationStatus.Text}，查看标注历史" : annotationStatus.Text);
        annotationHistory!.Visibility = Visibility.Collapsed;
        annotationHistory.Children.Clear();
        annotationHistoryChevron!.Angle = 0;
        annotationSummary!.Text = latest is null ? "" : $"最近一次 · {latest.CategoryName} / {latest.PlanName}";
        annotationSummary.Visibility = annotated ? Visibility.Visible : Visibility.Collapsed;
        submitButton!.Content = annotated ? "新增标注" : "提交标注";
        revokeButton!.Visibility = annotated ? Visibility.Visible : Visibility.Collapsed;
    }

}
