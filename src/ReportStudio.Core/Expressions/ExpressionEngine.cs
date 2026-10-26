using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace ReportStudio.Core.Expressions;

public class ExpressionException : Exception
{
    public ExpressionException(string message) : base(message) { }
}

/// <summary>Everything an expression can see while it is being evaluated.</summary>
public class EvalContext
{
    public IReadOnlyDictionary<string, object?>? Row { get; set; }
    /// <summary>Rows that aggregate functions (Sum, Count, ...) operate on: the group, or the whole report.</summary>
    public IReadOnlyList<IReadOnlyDictionary<string, object?>> Scope { get; set; } =
        Array.Empty<IReadOnlyDictionary<string, object?>>();
    public IReadOnlyDictionary<string, object?> Parameters { get; set; } =
        new Dictionary<string, object?>();
    public int PageNumber { get; set; } = 1;
    public int TotalPages { get; set; } = 1;
    public int RowNumber { get; set; }
    public DateTime Now { get; set; } = DateTime.Now;
    public CultureInfo Culture { get; set; } = CultureInfo.CurrentCulture;

    public EvalContext ForRow(IReadOnlyDictionary<string, object?> row) => new()
    {
        Row = row, Scope = Scope, Parameters = Parameters, PageNumber = PageNumber,
        TotalPages = TotalPages, RowNumber = RowNumber, Now = Now, Culture = Culture
    };
}

public abstract class Node
{
    public abstract object? Eval(EvalContext ctx);
}

/// <summary>Public entry points: compile once, evaluate many times.</summary>
public static class Expr
{
    private static readonly ConcurrentDictionary<string, Node> Cache = new();

    public static Node Compile(string expression) =>
        Cache.GetOrAdd(expression, e => new Parser(e).ParseAll());

    public static object? Evaluate(string expression, EvalContext ctx)
    {
        try { return Compile(expression).Eval(ctx); }
        catch (ExpressionException) { throw; }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException or InvalidCastException)
        {
            // Bad number literals, invalid format strings, out-of-range values: report as an expression error.
            throw new ExpressionException(ex.Message);
        }
    }

    public static bool IsTrue(object? v) => v switch
    {
        null => false,
        bool b => b,
        decimal d => d != 0,
        string s => s.Length > 0,
        _ => true
    };

    public static string Format(object? v, string? format, CultureInfo culture)
    {
        if (v == null) return "";
        if (!string.IsNullOrEmpty(format) && v is IFormattable f) return f.ToString(format, culture);
        // A date without a format shows as a date only (dd-MM-yyyy in the Indian culture) unless it carries a time.
        if (v is DateTime dt) return dt.ToString(dt.TimeOfDay == TimeSpan.Zero ? "d" : "g", culture);
        return v is IFormattable g ? g.ToString(null, culture) : v.ToString() ?? "";
    }

    /// <summary>Normalises database values: DBNull -> null, every number -> decimal.</summary>
    public static object? Normalize(object? v)
    {
        if (v == null || v is DBNull) return null;
        return v switch
        {
            decimal => v,
            int or long or short or byte or sbyte or uint or ulong or ushort => Convert.ToDecimal(v),
            double d => double.IsFinite(d) ? (decimal)d : null,
            float f => float.IsFinite(f) ? (decimal)f : null,
            _ => v
        };
    }

    public static int Compare(object? a, object? b)
    {
        if (a == null && b == null) return 0;
        if (a == null) return -1;
        if (b == null) return 1;
        if (a is decimal x && b is decimal y) return x.CompareTo(y);
        if (a is DateTime d1 && b is DateTime d2) return d1.CompareTo(d2);
        if (a is bool b1 && b is bool b2) return b1.CompareTo(b2);
        return string.Compare(Convert.ToString(a, CultureInfo.InvariantCulture),
                              Convert.ToString(b, CultureInfo.InvariantCulture),
                              StringComparison.OrdinalIgnoreCase);
    }

    public static bool TryNumber(object? v, out decimal result)
    {
        switch (v)
        {
            case decimal d: result = d; return true;
            case bool b: result = b ? 1 : 0; return true;
            case string s when decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var p):
                result = p; return true;
            default: result = 0; return false;
        }
    }

    internal static decimal ToNumber(object? v) => TryNumber(v, out var d) ? d : 0m;
}

// ---------------------------------------------------------------- AST nodes

internal sealed class Literal : Node
{
    private readonly object? _value;
    public Literal(object? value) { _value = value; }
    public override object? Eval(EvalContext ctx) => _value;
}

internal sealed class FieldRef : Node
{
    private readonly string _name;
    public FieldRef(string name) { _name = name; }

    public override object? Eval(EvalContext ctx)
    {
        switch (_name.ToLowerInvariant())
        {
            case "page": return (decimal)ctx.PageNumber;
            case "pagecount": case "totalpages": return (decimal)ctx.TotalPages;
            case "rownumber": return (decimal)ctx.RowNumber;
            case "now": return ctx.Now;
            case "today": return ctx.Now.Date;
            case "true": return true;
            case "false": return false;
            case "null": return null;
        }
        if (ctx.Row != null && ctx.Row.TryGetValue(_name, out var v)) return v;
        if (ctx.Parameters.TryGetValue(_name, out var p)) return p;
        if (ctx.Row == null || ctx.Row.Count == 0) return null;
        throw new ExpressionException($"Unknown field '{_name}'");
    }
}

internal sealed class ParamRef : Node
{
    private readonly string _name;
    public ParamRef(string name) { _name = name; }

    public override object? Eval(EvalContext ctx) =>
        ctx.Parameters.TryGetValue(_name, out var v) ? v : throw new ExpressionException($"Unknown parameter '@{_name}'");
}

internal sealed class Unary : Node
{
    private readonly string _op;
    private readonly Node _operand;
    public Unary(string op, Node operand) { _op = op; _operand = operand; }

    public override object? Eval(EvalContext ctx)
    {
        var v = _operand.Eval(ctx);
        return _op == "-" ? -Expr.ToNumber(v) : !Expr.IsTrue(v);
    }
}

internal sealed class Binary : Node
{
    private readonly string _op;
    private readonly Node _l, _r;
    public Binary(string op, Node l, Node r) { _op = op; _l = l; _r = r; }

    public override object? Eval(EvalContext ctx)
    {
        if (_op == "&&") return Expr.IsTrue(_l.Eval(ctx)) && Expr.IsTrue(_r.Eval(ctx));
        if (_op == "||") return Expr.IsTrue(_l.Eval(ctx)) || Expr.IsTrue(_r.Eval(ctx));

        var a = _l.Eval(ctx);
        var b = _r.Eval(ctx);
        switch (_op)
        {
            case "+":
                if (a is string || b is string)
                    return Expr.Format(a, null, ctx.Culture) + Expr.Format(b, null, ctx.Culture);
                if (a is DateTime dt && b is decimal days) return dt.AddDays((double)days);
                return Expr.ToNumber(a) + Expr.ToNumber(b);
            case "-":
                if (a is DateTime d1 && b is DateTime d2) return (decimal)(d1 - d2).TotalDays;
                if (a is DateTime d3 && b is decimal days2) return d3.AddDays(-(double)days2);
                return Expr.ToNumber(a) - Expr.ToNumber(b);
            case "*": return Expr.ToNumber(a) * Expr.ToNumber(b);
            case "/": { var d = Expr.ToNumber(b); return d == 0 ? null : Expr.ToNumber(a) / d; }
            case "%": { var d = Expr.ToNumber(b); return d == 0 ? null : Expr.ToNumber(a) % d; }
            case "==": return Expr.Compare(a, b) == 0;
            case "!=": return Expr.Compare(a, b) != 0;
            case "<": return Expr.Compare(a, b) < 0;
            case "<=": return Expr.Compare(a, b) <= 0;
            case ">": return Expr.Compare(a, b) > 0;
            case ">=": return Expr.Compare(a, b) >= 0;
        }
        throw new ExpressionException($"Unknown operator {_op}");
    }
}

internal sealed class Call : Node
{
    private readonly string _name;
    private readonly List<Node> _args;
    public Call(string name, List<Node> args) { _name = name; _args = args; }

    public override object? Eval(EvalContext ctx)
    {
        var fn = _name.ToLowerInvariant();
        switch (fn)
        {
            // ---- aggregates over ctx.Scope ----
            case "sum": return Aggregate(ctx).Aggregate(0m, (s, v) => s + Expr.ToNumber(v));
            case "count":
                return _args.Count == 0 ? (decimal)ctx.Scope.Count : (decimal)Aggregate(ctx).Count(v => v != null);
            case "countdistinct": return (decimal)Aggregate(ctx).Where(v => v != null).Distinct().Count();
            case "avg":
            {
                var vals = Aggregate(ctx).Where(v => v != null).Select(Expr.ToNumber).ToList();
                return vals.Count == 0 ? null : vals.Sum() / vals.Count;
            }
            case "min":
                return Aggregate(ctx).Where(v => v != null)
                    .OrderBy(v => v, Comparer<object?>.Create(Expr.Compare)).FirstOrDefault();
            case "max":
                return Aggregate(ctx).Where(v => v != null)
                    .OrderByDescending(v => v, Comparer<object?>.Create(Expr.Compare)).FirstOrDefault();

            // ---- logic (lazy) ----
            case "iif": case "if":
                Need(3);
                return Expr.IsTrue(_args[0].Eval(ctx)) ? _args[1].Eval(ctx) : _args[2].Eval(ctx);
            case "isnull": Need(2); return _args[0].Eval(ctx) ?? _args[1].Eval(ctx);
        }

        var a = _args.Select(x => x.Eval(ctx)).ToList();
        object? A(int i) => i < a.Count ? a[i] : null;
        string S(int i) => Expr.Format(A(i), null, ctx.Culture);
        int I(int i) => (int)Expr.ToNumber(A(i));

        switch (fn)
        {
            case "format": Need(2); return Expr.Format(A(0), S(1), ctx.Culture);
            case "now": return ctx.Now;
            case "today": return ctx.Now.Date;
            case "upper": return S(0).ToUpperInvariant();
            case "lower": return S(0).ToLowerInvariant();
            case "trim": return S(0).Trim();
            case "len": return (decimal)S(0).Length;
            case "left": { var s = S(0); return s.Substring(0, Math.Clamp(I(1), 0, s.Length)); }
            case "right": { var s = S(0); var n = Math.Clamp(I(1), 0, s.Length); return s.Substring(s.Length - n); }
            case "mid":
            {
                var s = S(0);
                var start = Math.Clamp(I(1) - 1, 0, s.Length);
                var len = a.Count > 2 ? Math.Clamp(I(2), 0, s.Length - start) : s.Length - start;
                return s.Substring(start, len);
            }
            case "replace": return S(0).Replace(S(1), S(2));
            case "totext": case "str": return S(0);
            case "round": return Math.Round(Expr.ToNumber(A(0)), a.Count > 1 ? I(1) : 0, MidpointRounding.AwayFromZero);
            case "abs": return Math.Abs(Expr.ToNumber(A(0)));
            case "amountinwords": case "rupeeswords":
                return ReportStudio.Core.Formatting.IndianFormat.AmountInWords(
                    Expr.TryNumber(A(0), out var amount) ? amount : null);
            case "year": return A(0) is DateTime y ? (decimal)y.Year : null;
            case "month": return A(0) is DateTime m ? (decimal)m.Month : null;
            case "day": return A(0) is DateTime d ? (decimal)d.Day : null;
        }
        throw new ExpressionException($"Unknown function '{_name}'");
    }

    private IEnumerable<object?> Aggregate(EvalContext ctx)
    {
        if (_args.Count == 0) throw new ExpressionException($"{_name}() needs an argument");
        foreach (var row in ctx.Scope)
            yield return _args[0].Eval(ctx.ForRow(row));
    }

    private void Need(int n)
    {
        if (_args.Count < n) throw new ExpressionException($"{_name}() needs {n} arguments");
    }
}

// ---------------------------------------------------------------- parser

internal sealed class Parser
{
    private enum T { Num, Str, Ident, Param, Op, LParen, RParen, Comma, End }

    private readonly struct Tok
    {
        public T Type { get; }
        public string Text { get; }
        public Tok(T type, string text) { Type = type; Text = text; }
    }

    private readonly List<Tok> _toks = new();
    private int _pos;

    public Parser(string src) => Tokenize(src);

    public Node ParseAll()
    {
        var n = ParseOr();
        if (Cur.Type != T.End) throw new ExpressionException($"Unexpected '{Cur.Text}'");
        return n;
    }

    private Tok Cur => _toks[_pos];
    private Tok Next() => _toks[_pos++];
    private bool IsOp(params string[] ops) => Cur.Type == T.Op && ops.Contains(Cur.Text);

    private Node ParseOr()
    {
        var l = ParseAnd();
        while (IsOp("||", "or")) { Next(); l = new Binary("||", l, ParseAnd()); }
        return l;
    }

    private Node ParseAnd()
    {
        var l = ParseNot();
        while (IsOp("&&", "and")) { Next(); l = new Binary("&&", l, ParseNot()); }
        return l;
    }

    private Node ParseNot()
    {
        if (IsOp("!", "not")) { Next(); return new Unary("!", ParseNot()); }
        return ParseCompare();
    }

    private Node ParseCompare()
    {
        var l = ParseAdd();
        while (IsOp("==", "=", "!=", "<>", "<", "<=", ">", ">="))
        {
            var op = Next().Text;
            op = op == "=" ? "==" : op == "<>" ? "!=" : op;
            l = new Binary(op, l, ParseAdd());
        }
        return l;
    }

    private Node ParseAdd()
    {
        var l = ParseMul();
        while (IsOp("+", "-")) { var op = Next().Text; l = new Binary(op, l, ParseMul()); }
        return l;
    }

    private Node ParseMul()
    {
        var l = ParseUnary();
        while (IsOp("*", "/", "%")) { var op = Next().Text; l = new Binary(op, l, ParseUnary()); }
        return l;
    }

    private Node ParseUnary()
    {
        if (IsOp("-")) { Next(); return new Unary("-", ParseUnary()); }
        if (IsOp("+")) { Next(); return ParseUnary(); }
        return ParsePrimary();
    }

    private Node ParsePrimary()
    {
        var t = Next();
        switch (t.Type)
        {
            case T.Num: return new Literal(decimal.Parse(t.Text, CultureInfo.InvariantCulture));
            case T.Str: return new Literal(t.Text);
            case T.Param: return new ParamRef(t.Text);
            case T.LParen:
            {
                var n = ParseOr();
                Expect(T.RParen);
                return n;
            }
            case T.Ident:
                if (Cur.Type == T.LParen)
                {
                    Next();
                    var args = new List<Node>();
                    if (Cur.Type != T.RParen)
                    {
                        args.Add(ParseOr());
                        while (Cur.Type == T.Comma) { Next(); args.Add(ParseOr()); }
                    }
                    Expect(T.RParen);
                    return new Call(t.Text, args);
                }
                return new FieldRef(t.Text);
        }
        throw new ExpressionException($"Unexpected '{t.Text}'");
    }

    private void Expect(T type)
    {
        if (Cur.Type != type) throw new ExpressionException($"Expected {type} but found '{Cur.Text}'");
        Next();
    }

    private void Tokenize(string s)
    {
        int i = 0;
        while (i < s.Length)
        {
            char c = s[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }

            if (char.IsDigit(c) || (c == '.' && i + 1 < s.Length && char.IsDigit(s[i + 1])))
            {
                int st = i;
                while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.')) i++;
                _toks.Add(new Tok(T.Num, s.Substring(st, i - st)));
            }
            else if (c == '\'' || c == '"')
            {
                var sb = new StringBuilder();
                i++;
                while (i < s.Length && s[i] != c)
                {
                    if (s[i] == '\\' && i + 1 < s.Length) i++;
                    sb.Append(s[i++]);
                }
                if (i >= s.Length) throw new ExpressionException("Unterminated string");
                i++;
                _toks.Add(new Tok(T.Str, sb.ToString()));
            }
            else if (c == '[')
            {
                int end = s.IndexOf(']', i);
                if (end < 0) throw new ExpressionException("Missing ']'");
                _toks.Add(new Tok(T.Ident, s.Substring(i + 1, end - i - 1).Trim()));
                i = end + 1;
            }
            else if (c == '@')
            {
                int st = ++i;
                while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '_')) i++;
                _toks.Add(new Tok(T.Param, s.Substring(st, i - st)));
            }
            else if (char.IsLetter(c) || c == '_')
            {
                int st = i;
                while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '_' || s[i] == '.')) i++;
                var word = s.Substring(st, i - st);
                var lw = word.ToLowerInvariant();
                _toks.Add(lw is "and" or "or" or "not" ? new Tok(T.Op, lw) : new Tok(T.Ident, word));
            }
            else if (c == '(') { _toks.Add(new Tok(T.LParen, "(")); i++; }
            else if (c == ')') { _toks.Add(new Tok(T.RParen, ")")); i++; }
            else if (c == ',') { _toks.Add(new Tok(T.Comma, ",")); i++; }
            else
            {
                string two = i + 1 < s.Length ? s.Substring(i, 2) : "";
                if (two is "==" or "!=" or "<>" or "<=" or ">=" or "&&" or "||") { _toks.Add(new Tok(T.Op, two)); i += 2; }
                else if ("+-*/%<>=!".Contains(c)) { _toks.Add(new Tok(T.Op, c.ToString())); i++; }
                else throw new ExpressionException($"Unexpected character '{c}'");
            }
        }
        _toks.Add(new Tok(T.End, "<end>"));
    }
}

/// <summary>Splits "Total: {Sum(Amount)}" into literal and expression parts.</summary>
public sealed class TextTemplate
{
    private readonly List<(bool IsExpr, string Text)> _parts = new();
    private static readonly ConcurrentDictionary<string, TextTemplate> Cache = new();

    public static TextTemplate Parse(string text) => Cache.GetOrAdd(text ?? "", t => new TextTemplate(t));

    public bool IsEmpty => _parts.Count == 0;
    public bool HasExpressions => _parts.Any(p => p.IsExpr);

    private TextTemplate(string text)
    {
        var lit = new StringBuilder();
        int i = 0;
        while (i < text.Length)
        {
            if (text[i] == '{')
            {
                if (i + 1 < text.Length && text[i + 1] == '{') { lit.Append('{'); i += 2; continue; }
                int depth = 1, j = i + 1;
                char quote = '\0';
                for (; j < text.Length; j++)
                {
                    char c = text[j];
                    if (quote != '\0') { if (c == quote) quote = '\0'; continue; }
                    if (c == '\'' || c == '"') quote = c;
                    else if (c == '{') depth++;
                    else if (c == '}' && --depth == 0) break;
                }
                if (depth != 0) { lit.Append(text.Substring(i)); break; }   // unbalanced: treat the rest as literal
                if (lit.Length > 0) { _parts.Add((false, lit.ToString())); lit.Clear(); }
                _parts.Add((true, text.Substring(i + 1, j - i - 1)));
                i = j + 1;
            }
            else if (text[i] == '}' && i + 1 < text.Length && text[i + 1] == '}') { lit.Append('}'); i += 2; }
            else lit.Append(text[i++]);
        }
        if (lit.Length > 0) _parts.Add((false, lit.ToString()));
    }

    /// <summary>Evaluates the template. A lone {expression} keeps its native type (number, date...).</summary>
    public object? EvaluateValue(EvalContext ctx) =>
        _parts.Count == 1 && _parts[0].IsExpr ? Expr.Evaluate(_parts[0].Text, ctx) : EvaluateString(ctx, null);

    public string EvaluateString(EvalContext ctx, string? format)
    {
        var sb = new StringBuilder();
        foreach (var (isExpr, text) in _parts)
            sb.Append(isExpr ? Expr.Format(Expr.Evaluate(text, ctx), format, ctx.Culture) : text);
        return sb.ToString();
    }
}
