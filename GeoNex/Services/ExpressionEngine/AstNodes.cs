using NetTopologySuite.Geometries;

namespace GeoNex.Services.ExpressionEngine;

public abstract class ExpressionNode
{
    public abstract object? Evaluate(FeatureEvaluationContext context);
}

public sealed class LiteralNode : ExpressionNode
{
    public object? Value { get; }
    public LiteralNode(object? value) => Value = value;
    public override object? Evaluate(FeatureEvaluationContext context) => Value;
}

public sealed class FieldRefNode : ExpressionNode
{
    public string FieldName { get; }
    public FieldRefNode(string fieldName) => FieldName = fieldName;

    public override object? Evaluate(FeatureEvaluationContext context)
    {
        if (context.Attributes.TryGetValue(FieldName, out var val))
            return val;
        return null;
    }
}

public sealed class VariableNode : ExpressionNode
{
    public string VariableName { get; }
    public VariableNode(string variableName) => VariableName = variableName.ToLowerInvariant();

    public override object? Evaluate(FeatureEvaluationContext context) => VariableName switch
    {
        "$area" => context.AreaM2,
        "$area_ha" => context.AreaM2 / 10000.0,
        "$perimeter" => context.PerimeterM,
        "$length" => context.PerimeterM,
        "$x" => context.PointX,
        "$y" => context.PointY,
        "$centroid_x" => context.CentroidX,
        "$centroid_y" => context.CentroidY,
        "$id" or "$fid" => context.FID,
        "$rownum" => context.RowNum,
        "$geometry_type" => context.Geometry?.GeometryType ?? "None",
        "$num_points" => context.Geometry?.NumPoints ?? 0,
        _ => null
    };
}

public sealed class BinaryOpNode : ExpressionNode
{
    public string Operator { get; }
    public ExpressionNode Left { get; }
    public ExpressionNode Right { get; }

    public BinaryOpNode(string op, ExpressionNode left, ExpressionNode right)
    {
        Operator = op.ToUpperInvariant();
        Left = left;
        Right = right;
    }

    public override object? Evaluate(FeatureEvaluationContext context)
    {
        var leftVal = Left.Evaluate(context);
        var rightVal = Right.Evaluate(context);

        // String concatenation
        if (Operator is "+" or "||" && (leftVal is string || rightVal is string))
            return $"{leftVal}{rightVal}";

        // Boolean operators
        if (Operator == "AND") return ToBool(leftVal) && ToBool(rightVal);
        if (Operator == "OR") return ToBool(leftVal) || ToBool(rightVal);

        // Comparisons
        if (Operator is "=" or "==") return Equals(leftVal, rightVal);
        if (Operator is "!=" or "<>") return !Equals(leftVal, rightVal);

        // Numeric operations
        double leftNum = ToDouble(leftVal);
        double rightNum = ToDouble(rightVal);

        return Operator switch
        {
            "+" => leftNum + rightNum,
            "-" => leftNum - rightNum,
            "*" => leftNum * rightNum,
            "/" => rightNum != 0 ? leftNum / rightNum : null,
            "%" => rightNum != 0 ? leftNum % rightNum : null,
            "^" => Math.Pow(leftNum, rightNum),
            "<" => leftNum < rightNum,
            "<=" => leftNum <= rightNum,
            ">" => leftNum > rightNum,
            ">=" => leftNum >= rightNum,
            _ => null
        };
    }

    private static double ToDouble(object? val)
    {
        if (val == null) return 0d;
        if (val is double d) return d;
        if (val is float f) return f;
        if (val is int i) return i;
        if (val is long l) return l;
        if (double.TryParse(val.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double parsed))
            return parsed;
        return 0d;
    }

    private static bool ToBool(object? val)
    {
        if (val == null) return false;
        if (val is bool b) return b;
        if (val is int i) return i != 0;
        if (val is long l) return l != 0;
        if (bool.TryParse(val.ToString(), out bool parsed)) return parsed;
        return false;
    }
}

public sealed class UnaryOpNode : ExpressionNode
{
    public string Operator { get; }
    public ExpressionNode Operand { get; }

    public UnaryOpNode(string op, ExpressionNode operand)
    {
        Operator = op.ToUpperInvariant();
        Operand = operand;
    }

    public override object? Evaluate(FeatureEvaluationContext context)
    {
        var val = Operand.Evaluate(context);
        if (Operator == "-")
        {
            if (val is double d) return -d;
            if (val is int i) return -i;
            if (val is long l) return -l;
            if (double.TryParse(val?.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double parsed))
                return -parsed;
            return null;
        }

        if (Operator == "NOT")
        {
            if (val is bool b) return !b;
            return false;
        }

        return val;
    }
}

public sealed class FunctionCallNode : ExpressionNode
{
    public string FunctionName { get; }
    public IReadOnlyList<ExpressionNode> Arguments { get; }

    public FunctionCallNode(string name, IReadOnlyList<ExpressionNode> args)
    {
        FunctionName = name.ToLowerInvariant();
        Arguments = args;
    }

    public override object? Evaluate(FeatureEvaluationContext context)
    {
        var evaluatedArgs = Arguments.Select(a => a.Evaluate(context)).ToArray();

        return FunctionName switch
        {
            // String functions
            "concat" => string.Concat(evaluatedArgs.Select(a => a?.ToString() ?? "")),
            "upper" => evaluatedArgs.Length > 0 ? evaluatedArgs[0]?.ToString()?.ToUpperInvariant() : null,
            "lower" => evaluatedArgs.Length > 0 ? evaluatedArgs[0]?.ToString()?.ToLowerInvariant() : null,
            "trim" => evaluatedArgs.Length > 0 ? evaluatedArgs[0]?.ToString()?.Trim() : null,
            "length" or "len" => evaluatedArgs.Length > 0 ? evaluatedArgs[0]?.ToString()?.Length ?? 0 : 0,
            "substr" or "substring" => EvaluateSubstring(evaluatedArgs),
            "replace" => EvaluateReplace(evaluatedArgs),
            "lpad" => EvaluateLpad(evaluatedArgs),

            // Math functions
            "round" => EvaluateRound(evaluatedArgs),
            "floor" => evaluatedArgs.Length > 0 ? Math.Floor(ToDouble(evaluatedArgs[0])) : null,
            "ceil" or "ceiling" => evaluatedArgs.Length > 0 ? Math.Ceiling(ToDouble(evaluatedArgs[0])) : null,
            "abs" => evaluatedArgs.Length > 0 ? Math.Abs(ToDouble(evaluatedArgs[0])) : null,
            "sqrt" => evaluatedArgs.Length > 0 ? Math.Sqrt(Math.Max(0, ToDouble(evaluatedArgs[0]))) : null,
            "pow" => evaluatedArgs.Length >= 2 ? Math.Pow(ToDouble(evaluatedArgs[0]), ToDouble(evaluatedArgs[1])) : null,
            "clamp" => EvaluateClamp(evaluatedArgs),

            // Conditional
            "if" => evaluatedArgs.Length >= 3 ? (ToBool(evaluatedArgs[0]) ? evaluatedArgs[1] : evaluatedArgs[2]) : null,
            "coalesce" => evaluatedArgs.FirstOrDefault(a => a != null),

            // DateTime
            "now" => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            "today" => DateTime.Today.ToString("yyyy-MM-dd"),
            _ => null
        };
    }

    private static object? EvaluateSubstring(object?[] args)
    {
        if (args.Length < 2 || args[0] == null) return null;
        string s = args[0]!.ToString()!;
        int start = Math.Max(0, Convert.ToInt32(args[1]));
        if (args.Length >= 3)
        {
            int len = Math.Max(0, Convert.ToInt32(args[2]));
            if (start + len > s.Length) len = Math.Max(0, s.Length - start);
            return s.Substring(start, len);
        }
        return s.Substring(start);
    }

    private static object? EvaluateReplace(object?[] args)
    {
        if (args.Length < 3 || args[0] == null) return null;
        string text = args[0]!.ToString()!;
        string find = args[1]?.ToString() ?? "";
        string replace = args[2]?.ToString() ?? "";
        return text.Replace(find, replace);
    }

    private static object? EvaluateLpad(object?[] args)
    {
        if (args.Length < 2 || args[0] == null) return null;
        string s = args[0]!.ToString()!;
        int totalWidth = Convert.ToInt32(args[1]);
        char paddingChar = args.Length >= 3 && args[2]?.ToString()?.Length > 0 ? args[2]!.ToString()![0] : '0';
        return s.PadLeft(totalWidth, paddingChar);
    }

    private static object? EvaluateRound(object?[] args)
    {
        if (args.Length == 0 || args[0] == null) return null;
        double val = ToDouble(args[0]);
        int decimals = args.Length >= 2 ? Math.Clamp(Convert.ToInt32(args[1]), 0, 10) : 0;
        return Math.Round(val, decimals);
    }

    private static object? EvaluateClamp(object?[] args)
    {
        if (args.Length < 3) return null;
        double val = ToDouble(args[0]);
        double min = ToDouble(args[1]);
        double max = ToDouble(args[2]);
        return Math.Clamp(val, min, max);
    }

    private static double ToDouble(object? val)
    {
        if (val == null) return 0d;
        if (val is double d) return d;
        if (val is float f) return f;
        if (val is int i) return i;
        if (val is long l) return l;
        if (double.TryParse(val.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double parsed))
            return parsed;
        return 0d;
    }

    private static bool ToBool(object? val)
    {
        if (val == null) return false;
        if (val is bool b) return b;
        if (val is int i) return i != 0;
        if (val is long l) return l != 0;
        if (bool.TryParse(val.ToString(), out bool parsed)) return parsed;
        return false;
    }
}

public sealed class FeatureEvaluationContext
{
    public long FID { get; }
    public long RowNum { get; }
    public IReadOnlyDictionary<string, object?> Attributes { get; }
    public Geometry? Geometry { get; }

    public double AreaM2 { get; }
    public double PerimeterM { get; }
    public double CentroidX { get; }
    public double CentroidY { get; }
    public double PointX { get; }
    public double PointY { get; }

    public FeatureEvaluationContext(
        long fid,
        long rowNum,
        IReadOnlyDictionary<string, object?> attributes,
        Geometry? geometry)
    {
        FID = fid;
        RowNum = rowNum;
        Attributes = attributes;
        Geometry = geometry;

        if (geometry != null)
        {
            AreaM2 = geometry.Area;
            PerimeterM = geometry.Length;
            var centroid = geometry.Centroid;
            if (centroid != null)
            {
                CentroidX = centroid.X;
                CentroidY = centroid.Y;
            }

            if (geometry is NetTopologySuite.Geometries.Point pt)
            {
                PointX = pt.X;
                PointY = pt.Y;
            }
            else
            {
                PointX = CentroidX;
                PointY = CentroidY;
            }
        }
    }
}
