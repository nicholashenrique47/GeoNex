using NetTopologySuite.Geometries;

namespace GeoNex.Services.ExpressionEngine;

public sealed class FieldCalculatorService
{
    public static bool ValidateExpression(string expressionText, out string? errorMessage)
    {
        try
        {
            var node = ExpressionParser.Parse(expressionText);
            errorMessage = null;
            return node != null;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            return false;
        }
    }

    public static object? EvaluateSingle(
        string expressionText,
        long fid,
        long rowNum,
        IReadOnlyDictionary<string, object?> attributes,
        Geometry? geometry = null)
    {
        var node = ExpressionParser.Parse(expressionText);
        var context = new FeatureEvaluationContext(fid, rowNum, attributes, geometry);
        return node.Evaluate(context);
    }
}
