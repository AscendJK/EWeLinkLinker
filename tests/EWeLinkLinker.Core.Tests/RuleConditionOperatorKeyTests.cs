using System.Text.Json;
using EWeLinkLinker.Core.Models;
using Xunit;

namespace EWeLinkLinker.Core.Tests;

/// <summary>
/// 「且 / 或」下拉原来绑的是 <c>LogicalOperator</c> 枚举，而候选项的 Tag 是字符串
/// （WPF 的 SelectedValue 按"候选 Tag 与绑定值是否相等"匹配）——两边永远比不相等，
/// 下拉框一直空白，用户看不出当前这条是"且"还是"或"。
/// 现在绑 <see cref="RuleCondition.OperatorKey"/>，这里钉住它的往返语义。
/// </summary>
public class RuleConditionOperatorKeyTests
{
    [Fact]
    public void 默认是且()
    {
        var condition = new RuleCondition();
        Assert.Equal(LogicalOperator.And, condition.Operator);
        Assert.Equal("And", condition.OperatorKey);
    }

    [Fact]
    public void 下拉写回来能落到枚举上()
    {
        var condition = new RuleCondition();
        condition.OperatorKey = "Or";
        Assert.Equal(LogicalOperator.Or, condition.Operator);
        Assert.Equal("Or", condition.OperatorKey);
    }

    [Fact]
    public void 大小写不敏感()
    {
        var condition = new RuleCondition();
        condition.OperatorKey = "or";
        Assert.Equal(LogicalOperator.Or, condition.Operator);
    }

    [Theory]
    [InlineData("")]
    [InlineData("不知道")]
    [InlineData(null)]
    public void 非法值被忽略_不抛异常(string? bad)
    {
        var condition = new RuleCondition { Operator = LogicalOperator.Or };
        condition.OperatorKey = bad!;
        Assert.Equal(LogicalOperator.Or, condition.Operator);
    }

    [Fact]
    public void 不写进配置文件()
    {
        var condition = new RuleCondition { Type = "cpu_temp", Parameter = "80", Operator = LogicalOperator.Or };

        // 用与生产一致的序列化口径：camelCase + 枚举写成字符串
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        var json = JsonSerializer.Serialize(condition, options);

        Assert.DoesNotContain("operatorKey", json);
        Assert.Contains("\"operator\":\"Or\"", json);
    }
}
