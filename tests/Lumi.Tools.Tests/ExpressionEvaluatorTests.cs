namespace Lumi.Tools.Tests;

public class ExpressionEvaluatorTests
{
    [Theory]
    [InlineData("1+2", 3)]
    [InlineData("2 + 3 * 4", 14)]
    [InlineData("(2 + 3) * 4", 20)]
    [InlineData("10 / 4", 2.5)]
    [InlineData("10 % 4", 2)]
    [InlineData("2 ^ 10", 1024)]
    [InlineData("2 ^ 3 ^ 2", 512)]          // 右結合
    [InlineData("-2 ^ 2", -4)]
    [InlineData("2 ^ -1", 0.5)]
    [InlineData("--3", 3)]
    [InlineData("+5", 5)]
    [InlineData(".5 + .25", 0.75)]
    [InlineData("1e3", 1000)]
    [InlineData("2.5E-1", 0.25)]
    [InlineData("sqrt(16)", 4)]
    [InlineData("abs(-3.5)", 3.5)]
    [InlineData("round(2.5)", 3)]
    [InlineData("round(3.14159, 2)", 3.14)]
    [InlineData("floor(2.9) + ceil(2.1)", 5)]
    [InlineData("min(3, 1, 2)", 1)]
    [InlineData("max(3, 1, 2)", 3)]
    [InlineData("pow(2, 8)", 256)]
    [InlineData("ln(e)", 1)]
    [InlineData("log(1000)", 3)]
    [InlineData("SQRT(9)", 3)]
    [InlineData("(12.5 + 3) × 4 ÷ 2", 31)]
    [InlineData("（1＋2）＊3", 9)]
    [InlineData("  1 +\t2 ", 3)]
    public void Evaluates(string expression, double expected) =>
        Assert.Equal(expected, ExpressionEvaluator.Evaluate(expression), 10);

    [Fact]
    public void Constants_Work() =>
        Assert.Equal(Math.PI * 2, ExpressionEvaluator.Evaluate("2 * pi"), 10);

    [Theory]
    [InlineData("1 / 0", "除以零")]
    [InlineData("1 % 0", "除以零")]
    [InlineData("sqrt(-1)", "負數")]
    [InlineData("ln(0)", "大於 0")]
    [InlineData("log(-5)", "大於 0")]
    [InlineData("10 ^ 1000", "超出")]
    [InlineData("exp(1000)", "超出")]
    [InlineData("1 +", "不完整")]
    [InlineData("(1 + 2", "右括號")]
    [InlineData("1 + 2)", "無法解析")]
    [InlineData("1 2", "無法解析")]
    [InlineData("foo", "不認得")]
    [InlineData("foo(1)", "不認得函式")]
    [InlineData("sqrt()", "1 個參數")]
    [InlineData("sqrt(1, 2)", "1 個參數")]
    [InlineData("pow(1)", "2 個參數")]
    [InlineData("min()", "至少")]
    [InlineData("round(1, 2.5)", "整數")]
    [InlineData("round(1, 20)", "整數")]
    [InlineData("1.2.3", "不是有效的數字")]
    [InlineData("2e", "無法解析")]
    [InlineData("1; DROP TABLE", "無法解析")]
    [InlineData("System.Environment.Exit(1)", "不認得")]
    [InlineData("", "空")]
    [InlineData("   ", "空")]
    [InlineData("sqrt(1,", "不完整")]
    public void Rejects(string expression, string messagePart)
    {
        var e = Assert.Throws<ExpressionException>(() => ExpressionEvaluator.Evaluate(expression));
        Assert.Contains(messagePart, e.Message);
    }

    [Fact]
    public void TooLong_IsRejected()
    {
        var e = Assert.Throws<ExpressionException>(() => ExpressionEvaluator.Evaluate(new string('1', ExpressionEvaluator.MaxLength + 1)));
        Assert.Contains("太長", e.Message);
    }

    [Fact]
    public void DeepParentheses_AreRejected_NotStackOverflow()
    {
        var text = new string('(', 300) + "1" + new string(')', 300);   // 長度 601 > 500，先擋長度
        Assert.Throws<ExpressionException>(() => ExpressionEvaluator.Evaluate(text));

        var within = new string('(', 200) + "1" + new string(')', 200);  // 長度 401，卡深度
        var e = Assert.Throws<ExpressionException>(() => ExpressionEvaluator.Evaluate(within));
        Assert.Contains("太深", e.Message);
    }

    [Fact]
    public void TenThousandNestedParentheses_AreRejected_NotCrash()
    {
        var text = new string('(', 10_000) + "1" + new string(')', 10_000);
        Assert.Contains("太長", Assert.Throws<ExpressionException>(() => ExpressionEvaluator.Evaluate(text)).Message);
    }

    [Fact]
    public void LongUnaryChain_IsRejectedByDepth()
    {
        var e = Assert.Throws<ExpressionException>(() => ExpressionEvaluator.Evaluate(new string('-', 400) + "1"));
        Assert.Contains("太深", e.Message);
    }

    [Fact]
    public void NestedFunctionCalls_AreDepthLimited()
    {
        var text = string.Concat(Enumerable.Repeat("abs(", 100)) + "1" + new string(')', 100);
        Assert.Throws<ExpressionException>(() => ExpressionEvaluator.Evaluate(text));
    }

    [Fact]
    public void TooManyArguments_AreRejected()
    {
        var text = "max(" + string.Join(",", Enumerable.Repeat("1", ExpressionEvaluator.MaxArguments + 1)) + ")";
        Assert.Contains("太多", Assert.Throws<ExpressionException>(() => ExpressionEvaluator.Evaluate(text)).Message);
    }

    [Fact]
    public void ManyOperators_WithinLimit_Work()
    {
        var text = string.Join("+", Enumerable.Repeat("1", 200));
        Assert.Equal(200, ExpressionEvaluator.Evaluate(text));
    }

    [Fact]
    public void NonAsciiDigits_AreNotNumbers()
    {
        Assert.Throws<ExpressionException>(() => ExpressionEvaluator.Evaluate("١٢٣"));   // 阿拉伯-印度數字
    }

    [Fact]
    public void ResultIsCultureInvariant()
    {
        var old = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            Assert.Equal(1.5, ExpressionEvaluator.Evaluate("1.5"));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = old;
        }
    }
}
