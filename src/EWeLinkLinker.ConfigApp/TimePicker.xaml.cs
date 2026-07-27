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
        MinutesCombo.ItemsSource = new[] { "00", "05", "10", "15", "20", "25", "30", "35", "40", "45", "50", "55" };
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

        if (TimeSpan.TryParse(time, out var ts))
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
        // 初始化期间或隐藏时不更新源
        if (_isInitializing || Visibility != Visibility.Visible) return;
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
