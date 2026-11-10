using System.Globalization;
using System.Text;

namespace GeoNex.Services.ExpressionEngine;

public enum TokenType
{
    Number,
    String,
    Identifier,
    Variable,
    Operator,
    OpenParen,
    CloseParen,
    Comma,
    EndOfFile
}

public sealed record Token(TokenType Type, string Value, int Position);

public sealed class ExpressionParser
{
    private readonly List<Token> _tokens = new();
    private int _position = 0;

    public static ExpressionNode Parse(string expressionText)
    {
        if (string.IsNullOrWhiteSpace(expressionText))
            return new LiteralNode(null);

        var tokens = Tokenize(expressionText);
        var parser = new ExpressionParser(tokens);
        return parser.ParseExpression();
    }

    private ExpressionParser(List<Token> tokens)
    {
        _tokens = tokens;
    }

    private static List<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        int i = 0;
        int len = text.Length;

        while (i < len)
        {
            char c = text[i];

            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            // Parentheses and Comma
            if (c == '(') { tokens.Add(new Token(TokenType.OpenParen, "(", i++)); continue; }
            if (c == ')') { tokens.Add(new Token(TokenType.CloseParen, ")", i++)); continue; }
            if (c == ',') { tokens.Add(new Token(TokenType.Comma, ",", i++)); continue; }

            // Strings '...' or "..."
            if (c is '\'' or '"')
            {
                char quote = c;
                int start = i++;
                var sb = new StringBuilder();
                while (i < len && text[i] != quote)
                {
                    if (text[i] == '\\' && i + 1 < len) i++;
                    sb.Append(text[i++]);
                }
                if (i < len && text[i] == quote) i++;
                tokens.Add(new Token(TokenType.String, sb.ToString(), start));
                continue;
            }

            // Variables starting with $ (e.g. $area, $perimeter, $x, $y)
            if (c == '$')
            {
                int start = i++;
                var sb = new StringBuilder("$");
                while (i < len && (char.IsLetterOrDigit(text[i]) || text[i] == '_'))
                {
                    sb.Append(text[i++]);
                }
                tokens.Add(new Token(TokenType.Variable, sb.ToString(), start));
                continue;
            }

            // Numbers
            if (char.IsDigit(c) || (c == '.' && i + 1 < len && char.IsDigit(text[i + 1])))
            {
                int start = i;
                var sb = new StringBuilder();
                while (i < len && (char.IsDigit(text[i]) || text[i] == '.'))
                {
                    sb.Append(text[i++]);
                }
                tokens.Add(new Token(TokenType.Number, sb.ToString(), start));
                continue;
            }

            // Multi-char operators and punctuation
            if (c is '+' or '-' or '*' or '/' or '%' or '^')
            {
                tokens.Add(new Token(TokenType.Operator, c.ToString(), i++));
                continue;
            }

            if (c == '|' && i + 1 < len && text[i + 1] == '|')
            {
                tokens.Add(new Token(TokenType.Operator, "||", i));
                i += 2;
                continue;
            }

            if (c is '=' or '!' or '<' or '>')
            {
                int start = i++;
                var sb = new StringBuilder(c.ToString());
                if (i < len && (text[i] == '=' || (c == '<' && text[i] == '>')))
                {
                    sb.Append(text[i++]);
                }
                tokens.Add(new Token(TokenType.Operator, sb.ToString(), start));
                continue;
            }

            // Identifiers / Keywords / Column Names / Functions
            if (char.IsLetter(c) || c == '_' || c == '"')
            {
                int start = i;
                var sb = new StringBuilder();
                bool bracketed = c == '"';
                if (bracketed) i++;

                while (i < len && (bracketed ? text[i] != '"' : (char.IsLetterOrDigit(text[i]) || text[i] == '_')))
                {
                    sb.Append(text[i++]);
                }

                if (bracketed && i < len && text[i] == '"') i++;

                string val = sb.ToString();
                string upper = val.ToUpperInvariant();
                if (upper is "AND" or "OR" or "NOT")
                {
                    tokens.Add(new Token(TokenType.Operator, upper, start));
                }
                else
                {
                    tokens.Add(new Token(TokenType.Identifier, val, start));
                }
                continue;
            }

            i++;
        }

        tokens.Add(new Token(TokenType.EndOfFile, "", len));
        return tokens;
    }

    private Token Current => _position < _tokens.Count ? _tokens[_position] : _tokens[^1];

    private Token Advance()
    {
        var token = Current;
        if (_position < _tokens.Count - 1) _position++;
        return token;
    }

    private bool Match(TokenType type, string? val = null)
    {
        if (Current.Type == type && (val == null || string.Equals(Current.Value, val, StringComparison.OrdinalIgnoreCase)))
        {
            Advance();
            return true;
        }
        return false;
    }

    private ExpressionNode ParseExpression() => ParseLogicalOr();

    private ExpressionNode ParseLogicalOr()
    {
        var left = ParseLogicalAnd();
        while (Match(TokenType.Operator, "OR"))
        {
            var right = ParseLogicalAnd();
            left = new BinaryOpNode("OR", left, right);
        }
        return left;
    }

    private ExpressionNode ParseLogicalAnd()
    {
        var left = ParseEquality();
        while (Match(TokenType.Operator, "AND"))
        {
            var right = ParseEquality();
            left = new BinaryOpNode("AND", left, right);
        }
        return left;
    }

    private ExpressionNode ParseEquality()
    {
        var left = ParseComparison();
        while (Current.Type == TokenType.Operator && Current.Value is "=" or "==" or "!=" or "<>")
        {
            string op = Advance().Value;
            var right = ParseComparison();
            left = new BinaryOpNode(op, left, right);
        }
        return left;
    }

    private ExpressionNode ParseComparison()
    {
        var left = ParseAdditive();
        while (Current.Type == TokenType.Operator && Current.Value is "<" or "<=" or ">" or ">=")
        {
            string op = Advance().Value;
            var right = ParseAdditive();
            left = new BinaryOpNode(op, left, right);
        }
        return left;
    }

    private ExpressionNode ParseAdditive()
    {
        var left = ParseMultiplicative();
        while (Current.Type == TokenType.Operator && Current.Value is "+" or "-" or "||")
        {
            string op = Advance().Value;
            var right = ParseMultiplicative();
            left = new BinaryOpNode(op, left, right);
        }
        return left;
    }

    private ExpressionNode ParseMultiplicative()
    {
        var left = ParsePower();
        while (Current.Type == TokenType.Operator && Current.Value is "*" or "/" or "%")
        {
            string op = Advance().Value;
            var right = ParsePower();
            left = new BinaryOpNode(op, left, right);
        }
        return left;
    }

    private ExpressionNode ParsePower()
    {
        var left = ParseUnary();
        while (Current.Type == TokenType.Operator && Current.Value == "^")
        {
            string op = Advance().Value;
            var right = ParseUnary();
            left = new BinaryOpNode(op, left, right);
        }
        return left;
    }

    private ExpressionNode ParseUnary()
    {
        if (Current.Type == TokenType.Operator && Current.Value is "-" or "NOT")
        {
            string op = Advance().Value;
            var operand = ParseUnary();
            return new UnaryOpNode(op, operand);
        }
        return ParsePrimary();
    }

    private ExpressionNode ParsePrimary()
    {
        var token = Current;

        if (Match(TokenType.Number))
        {
            double val = double.Parse(token.Value, NumberStyles.Float, CultureInfo.InvariantCulture);
            return new LiteralNode(val);
        }

        if (Match(TokenType.String))
        {
            return new LiteralNode(token.Value);
        }

        if (Match(TokenType.Variable))
        {
            return new VariableNode(token.Value);
        }

        if (Match(TokenType.OpenParen))
        {
            var inner = ParseExpression();
            Match(TokenType.CloseParen);
            return inner;
        }

        if (token.Type == TokenType.Identifier)
        {
            string id = Advance().Value;

            // Check if it's a function call like round(val, 2)
            if (Match(TokenType.OpenParen))
            {
                var args = new List<ExpressionNode>();
                if (Current.Type != TokenType.CloseParen)
                {
                    args.Add(ParseExpression());
                    while (Match(TokenType.Comma))
                    {
                        args.Add(ParseExpression());
                    }
                }
                Match(TokenType.CloseParen);
                return new FunctionCallNode(id, args);
            }

            // Check boolean literals
            if (string.Equals(id, "true", StringComparison.OrdinalIgnoreCase)) return new LiteralNode(true);
            if (string.Equals(id, "false", StringComparison.OrdinalIgnoreCase)) return new LiteralNode(false);
            if (string.Equals(id, "null", StringComparison.OrdinalIgnoreCase)) return new LiteralNode(null);

            // Otherwise, it's a field reference
            return new FieldRefNode(id);
        }

        // Unknown token fallback
        Advance();
        return new LiteralNode(null);
    }
}
