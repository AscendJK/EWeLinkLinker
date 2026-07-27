using System.Windows;
using System.Windows.Controls;
using EWeLinkLinker.Core.Models;

namespace EWeLinkLinker.ConfigApp;

public partial class ActionRowControl : UserControl
{
    public LinkerAction? Action { get; set; }

    public event EventHandler? DeleteRequested;

    public ActionRowControl()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (DataContext is LinkerAction action)
        {
            Action = action;
        }
    }

    public void SetDevices(IEnumerable<DeviceInfo> devices)
    {
        DeviceCombo.ItemsSource = devices;
        if (Action != null)
        {
            DeviceCombo.SelectedValue = Action.DeviceId;
        }
    }

    private void UpdateUI(LinkerAction action)
    {
        // 设置设备列表（如果已设置）
        if (DeviceCombo.ItemsSource != null)
        {
            DeviceCombo.SelectedValue = action.DeviceId;
        }

        // 设置通道
        OutletCombo.SelectedIndex = Math.Min(action.Outlet, 3);

        // 设置状态
        StateCombo.SelectedIndex = action.State == "on" ? 0 : 1;
    }

    private void DeviceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Action != null && DeviceCombo.SelectedItem is DeviceInfo device)
        {
            Action.DeviceId = device.DeviceId;
            Action.Name = device.Name;
        }
    }

    private void OutletCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Action != null)
        {
            Action.Outlet = OutletCombo.SelectedIndex;
        }
    }

    private void StateCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Action != null)
        {
            Action.State = StateCombo.SelectedIndex == 0 ? "on" : "off";
        }
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        DeleteRequested?.Invoke(this, EventArgs.Empty);
    }
}
