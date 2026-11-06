using System.Windows;
using System.Windows.Controls;

namespace EWeLinkLinker.ConfigApp;

public partial class TimePicker : UserControl
{
    public static readonly DependencyProperty SelectedTimeProperty =
        DependencyProperty.Register(nameof(SelectedTime), typeof(string), typeof(TimePicker),
            new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnSelectedTimeChanged));

    public string SelectedTime
    {
        get => (string)GetValue(SelectedTimeProperty);
        set => SetValue(SelectedTimeProperty, value);
    }

    public TimePicker()
    {
        InitializeComponent();
        InitTimeComboBoxes();
        UpdateDisplay(SelectedTime);

        // 延迟标记初始化完成，确保绑定已建立
        Loaded += (_, _) => MarkInitializationComplete();
    }

    private void InitTimeComboBoxes()
    {
        HoursCombo.ItemsSource = Enumerable.Range(0, 24).Select(i => i.ToString("D2"));
        // 分钟给全部 60 档。以前只有 5 分钟一档：配置里存着 08:07 时 SelectedItem 落空、
        // 分钟那格显示空白，而 UpdateValue 要求两个都非 null —— 用户改小时会被静默丢弃。
        MinutesCombo.ItemsSource = Enumerable.Range(0, 60).Select(i => i.ToString("D2"));
    }

    /// <summary>
    /// 当用户选择时间时触发的事件
    /// </summary>
    public event EventHandler? SelectedTimeChanged;

    private static void OnSelectedTimeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TimePicker control)
        {
            control.UpdateDisplay(e.NewValue?.ToString() ?? "");
            // 如果是用户操作（非初始化），触发事件
            if (!control._isInitializing)
            {
                control.SelectedTimeChanged?.Invoke(control, EventArgs.Empty);
            }
        }
    }

    private bool _isInitializing = true;

    private void UpdateDisplay(string time)
    {
        // 先取消订阅事件，避免设置 SelectedItem 时触发
        HoursCombo.SelectionChanged -= OnComboChanged;
        MinutesCombo.SelectionChanged -= OnComboChanged;

        if (TimeSpan.TryParse(time, System.Globalization.CultureInfo.InvariantCulture, out var ts))
        {
            HoursCombo.SelectedItem = ts.Hours.ToString("D2");
            MinutesCombo.SelectedItem = ts.Minutes.ToString("D2");
        }

        HoursCombo.SelectionChanged += OnComboChanged;
        MinutesCombo.SelectionChanged += OnComboChanged;
    }

    /// <summary>
    /// 标记初始化完成，此后用户操作才触发事件
    /// </summary>
    public void MarkInitializationComplete()
    {
        _isInitializing = false;
    }

    private void OnComboChanged(object? sender, SelectionChangedEventArgs e)
    {
        // 用 IsVisible 而不是 Visibility：控件自身的 Visibility 在父面板整体隐藏时仍然是 Visible，
        // 那个判断等于没写（时间条件被类型切换隐藏期间照样会回写）。
        if (_isInitializing || !IsVisible) return;
        UpdateValue();
    }

    private void UpdateValue()
    {
        if (!_isInitializing && HoursCombo.SelectedItem != null && MinutesCombo.SelectedItem != null)
        {
            SelectedTime = $"{HoursCombo.SelectedItem}:{MinutesCombo.SelectedItem}";
        }
    }
}
