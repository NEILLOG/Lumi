using System.Globalization;

namespace Lumi.Tools;

/// <summary>運算式有錯（語法、除以零、超出範圍…）。訊息是給模型與使用者看的繁中說明。</summary>
public sealed class ExpressionException(string message) : Exception(message);

/// <summary>
/// 自己寫的遞迴下降運算式解析器，不執行任何程式碼，也不使用 eval 類的機制。
/// 支援 + - * / % ^、括號、一元正負號、常數 pi／e、函式 sqrt abs round floor ceil min max pow ln log exp sin cos tan。
/// 為了避免惡意輸入，長度與巢狀深度都有上限；結果必須是有限的數字。
/// </summary>
public sealed class ExpressionEvaluator
{
    public const int MaxLength = 500;
    public const int MaxDepth = 64;
    public const int MaxArguments = 16;

    private readonly string _text;
    private int _pos;
    private int _depth;

    private ExpressionEvaluator(string text) => _text = text;

    public static double Evaluate(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            throw new ExpressionException("運算式是空的。");
        if (expression.Length > MaxLength)
            throw new ExpressionException($"運算式太長（上限 {MaxLength} 個字元）。");

        var parser = new ExpressionEvaluator(Normalize(expression));
        var value = parser.ParseExpression();
        parser.SkipSpaces();
        if (parser._pos < parser._text.Length)
            throw new ExpressionException($"第 {parser._pos + 1} 個字元「{parser._text[parser._pos]}」無法解析。");
        return Check(value);
    }

    /// <summary>把常見的全形與數學符號換成半形，讓模型照使用者原文傳進來也能算。</summary>
    private static string Normalize(string text) => text
        .Replace('×', '*').Replace('÷', '/').Replace('−', '-').Replace('（', '(').Replace('）', ')')
        .Replace('＋', '+').Replace('－', '-').Replace('＊', '*').Replace('／', '/').Replace('，', ',');

    private static double Check(double value)
    {
        if (double.IsNaN(value)) throw new ExpressionException("結果不是有效的數字。");
        if (double.IsInfinity(value)) throw new ExpressionException("結果超出可以表示的範圍。");
        return value;
    }

    private void SkipSpaces()
    {
        while (_pos < _text.Length && char.IsWhiteSpace(_text[_pos])) _pos++;
    }

    private bool Accept(char c)
    {
        SkipSpaces();
        if (_pos < _text.Length && _text[_pos] == c)
        {
            _pos++;
            return true;
        }
        return false;
    }

    private void Enter()
    {
        if (++_depth > MaxDepth)
            throw new ExpressionException($"運算式巢狀太深（上限 {MaxDepth} 層）。");
    }

    private void Leave() => _depth--;

    // expr := term (('+' | '-') term)*
    private double ParseExpression()
    {
        Enter();
        var value = ParseTerm();
        while (true)
        {
            if (Accept('+')) value = Check(value + ParseTerm());
            else if (Accept('-')) value = Check(value - ParseTerm());
            else break;
        }
        Leave();
        return value;
    }

    // term := unary (('*' | '/' | '%') unary)*
    private double ParseTerm()
    {
        var value = ParseUnary();
        while (true)
        {
            if (Accept('*'))
            {
                value = Check(value * ParseUnary());
            }
            else if (Accept('/'))
            {
                var divisor = ParseUnary();
                if (divisor == 0) throw new ExpressionException("不能除以零。");
                value = Check(value / divisor);
            }
            else if (Accept('%'))
            {
                var divisor = ParseUnary();
                if (divisor == 0) throw new ExpressionException("不能除以零（取餘數）。");
                value = Check(value % divisor);
            }
            else break;
        }
        return value;
    }

    // unary := ('-' | '+') unary | power      （-2^2 = -(2^2) = -4）
    private double ParseUnary()
    {
        Enter();
        double value;
        if (Accept('-')) value = -ParseUnary();
        else if (Accept('+')) value = ParseUnary();
        else value = ParsePower();
        Leave();
        return value;
    }

    // power := primary ('^' unary)?           （右結合：2^3^2 = 2^(3^2)）
    private double ParsePower()
    {
        var baseValue = ParsePrimary();
        if (Accept('^'))
        {
            var exponent = ParseUnary();
            return Check(Math.Pow(baseValue, exponent));
        }
        return baseValue;
    }

    private double ParsePrimary()
    {
        SkipSpaces();
        if (_pos >= _text.Length)
            throw new ExpressionException("運算式不完整，結尾少了東西。");

        var c = _text[_pos];
        if (c == '(')
        {
            _pos++;
            var inner = ParseExpression();
            if (!Accept(')')) throw new ExpressionException("少了右括號「)」。");
            return inner;
        }
        if (char.IsAsciiDigit(c) || c == '.')
            return ParseNumber();
        if (char.IsAsciiLetter(c))
            return ParseIdentifier();
        throw new ExpressionException($"第 {_pos + 1} 個字元「{c}」無法解析。");
    }

    private double ParseNumber()
    {
        var start = _pos;
        while (_pos < _text.Length && (char.IsAsciiDigit(_text[_pos]) || _text[_pos] == '.')) _pos++;
        // 科學記號：1e3、2.5E-4（後面要接數字，否則 e 留給常數／錯誤處理）
        if (_pos < _text.Length && _text[_pos] is 'e' or 'E')
        {
            var look = _pos + 1;
            if (look < _text.Length && _text[look] is '+' or '-') look++;
            if (look < _text.Length && char.IsAsciiDigit(_text[look]))
            {
                _pos = look;
                while (_pos < _text.Length && char.IsAsciiDigit(_text[_pos])) _pos++;
            }
        }

        var token = _text[start.._pos];
        if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            throw new ExpressionException($"「{token}」不是有效的數字。");
        return Check(number);
    }

    private double ParseIdentifier()
    {
        var start = _pos;
        while (_pos < _text.Length && (char.IsAsciiLetter(_text[_pos]) || char.IsAsciiDigit(_text[_pos]))) _pos++;
        var name = _text[start.._pos].ToLowerInvariant();

        SkipSpaces();
        if (_pos < _text.Length && _text[_pos] == '(')
        {
            _pos++;
            var args = ParseArguments();
            return Call(name, args);
        }

        return name switch
        {
            "pi" => Math.PI,
            "e" => Math.E,
            _ => throw new ExpressionException($"不認得「{name}」。"),
        };
    }

    private List<double> ParseArguments()
    {
        var args = new List<double>();
        if (Accept(')')) return args;
        while (true)
        {
            if (args.Count >= MaxArguments)
                throw new ExpressionException($"函式的參數太多（上限 {MaxArguments} 個）。");
            args.Add(ParseExpression());
            if (Accept(')')) return args;
            if (!Accept(',')) throw new ExpressionException("函式的參數之間要用逗號分隔，並以「)」結尾。");
        }
    }

    private static double Call(string name, List<double> a)
    {
        double One() => a.Count == 1 ? a[0] : throw new ExpressionException($"{name}() 需要 1 個參數。");
        (double, double) Two() => a.Count == 2 ? (a[0], a[1]) : throw new ExpressionException($"{name}() 需要 2 個參數。");

        switch (name)
        {
            case "sqrt":
                var s = One();
                if (s < 0) throw new ExpressionException("不能對負數開平方根。");
                return Math.Sqrt(s);
            case "abs": return Math.Abs(One());
            case "floor": return Math.Floor(One());
            case "ceil": return Math.Ceiling(One());
            case "exp": return Check(Math.Exp(One()));
            case "sin": return Math.Sin(One());
            case "cos": return Math.Cos(One());
            case "tan": return Check(Math.Tan(One()));
            case "ln":
                var ln = One();
                if (ln <= 0) throw new ExpressionException("ln() 的參數必須大於 0。");
                return Math.Log(ln);
            case "log":
                var log = One();
                if (log <= 0) throw new ExpressionException("log() 的參數必須大於 0。");
                return Math.Log10(log);
            case "pow":
                var (b, x) = Two();
                return Check(Math.Pow(b, x));
            case "min":
                if (a.Count == 0) throw new ExpressionException("min() 至少需要 1 個參數。");
                return a.Min();
            case "max":
                if (a.Count == 0) throw new ExpressionException("max() 至少需要 1 個參數。");
                return a.Max();
            case "round":
                if (a.Count is not (1 or 2)) throw new ExpressionException("round() 需要 1 或 2 個參數（數字、小數位數）。");
                var digits = a.Count == 2 ? a[1] : 0;
                if (digits != Math.Floor(digits) || digits < 0 || digits > 15)
                    throw new ExpressionException("round() 的小數位數必須是 0 到 15 的整數。");
                return Math.Round(a[0], (int)digits, MidpointRounding.AwayFromZero);
            default:
                throw new ExpressionException($"不認得函式「{name}」。");
        }
    }
}
