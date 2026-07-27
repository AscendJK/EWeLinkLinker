using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using EWeLinkLinker.Core.Models;

namespace EWeLinkLinker.ConfigApp;

public partial class CompositeConditionEditor : UserControl
{
    public ObservableCollection<RuleCondition> SubConditions { get; set; } = new();
    public LogicalOperator Operator { get; set; } = LogicalOperator.And;

    public CompositeConditionEditor()
    {
        InitializeComponent();
        SubConditionsItemsControl.ItemsSource = SubConditions;
    }

    private void LogicComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LogicComboBox.SelectedItem is ComboBoxItem item && item.Tag is string tag)
        {
            Operator = tag == "And" ? LogicalOperator.And : LogicalOperator.Or;
        }
    }

    private void AddSubCondition_Click(object sender, RoutedEventArgs e)
    {
        SubConditions.Add(new RuleCondition { Type = "time", Parameter = "08:00" });
    }

    private void RemoveSubCondition_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is RuleCondition condition)
        {
            SubConditions.Remove(condition);
        }
    }

    private void SubTypeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox combo && combo.Tag is RuleCondition condition)
        {
            if (combo.SelectedItem is ComboBoxItem item && item.Tag is string tag)
            {
                condition.Type = tag;
                // 设置默认参数值
                condition.Parameter = tag switch
                {
                    "time" => "08:00",
                    "interval" => "30",
                    "cpu_temp" => "70",
                    "cpu_usage" => "90",
                    "app_start" => "notepad",
                    "app_close" => "notepad",
                    _ => ""
                };
            }
        }
    }
}
