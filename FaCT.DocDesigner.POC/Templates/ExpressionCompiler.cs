using System.Text;
using System.Text.RegularExpressions;

namespace FaCT.DocDesigner.POC.Templates;

/// <summary>A calculated-field expression that can't be compiled, with the character position of the problem.</summary>
public sealed class ExpressionException(string message, int position) : Exception(message)
{
	public int Position { get; } = position;
}

/// <summary>
/// A compiled calculated field: Liquid statements that leave the value in <see cref="Result"/>, plus the data paths it
/// reads (<see cref="Paths"/>) and the lists it totals or counts (<see cref="Lists"/>).
/// </summary>
public sealed record CompiledExpression(string Liquid, string Result, IReadOnlyList<string> Paths, IReadOnlyList<string> Lists);

/// <summary>
/// Calculated fields: a small, safe expression language for authors, compiled to Liquid the document service already
/// runs. Arithmetic with precedence and parentheses (<c>policy.premium + policy.fees - policy.discount</c>,
/// <c>(a + b) * 0.1</c>), and functions: sum, count, average, min, max, round, abs, concat, default, days_between.
/// Liquid filters have no precedence, so every operation becomes an <c>{% assign %}</c> of a temporary.
/// Division never fails: dividing by zero or by an empty value gives 0. Only data paths, numbers and quoted text are
/// accepted, so an expression can never inject markup or Liquid.
/// </summary>
public static partial class ExpressionCompiler
{
	public const int MaxLength = 500;
	public const string ResultVariable = "calc_result";
	private const string TempPrefix = "calc_t";

	public static readonly IReadOnlyList<string> Functions =
		["sum", "count", "average", "min", "max", "round", "abs", "concat", "default", "days_between"];

	// Words Liquid reads as values or operators, never as data.
	private static readonly HashSet<string> LiquidWords = new(StringComparer.Ordinal)
		{ "empty", "blank", "nil", "null", "true", "false", "and", "or", "contains" };

	/// <param name="expression">The author's expression.</param>
	/// <param name="lists">The list paths of the data model (e.g. "claims", "location.buildings"); sum/count/average use
	/// them to tell the list from the item property. Without them, the last segment is the property.</param>
	/// <exception cref="ExpressionException">The expression is invalid.</exception>
	public static CompiledExpression Compile(string? expression, IReadOnlyCollection<string>? lists = null)
	{
		var text = expression ?? string.Empty;
		if (text.Trim().Length == 0) throw new ExpressionException("Type a calculation, for example policy.premium + policy.fees.", 0);
		if (text.Length > MaxLength) throw new ExpressionException($"The calculation is too long (at most {MaxLength} characters).", MaxLength);
		var parser = new Parser(Tokenize(text), text.Length);
		var tree = parser.ParseExpression();
		parser.ExpectEnd();
		var emitter = new Emitter(lists);
		var atom = emitter.Atom(tree);
		// calc_value drops trailing zeros (1200.0 -> 1200); text passes through.
		emitter.Assign(ResultVariable, atom + " | calc_value");
		return new CompiledExpression(emitter.Liquid, ResultVariable, emitter.Paths.Distinct().ToList(), emitter.Lists.Distinct().ToList());
	}

	// ---- Tokens -------------------------------------------------------------------------------------------------------

	private enum Kind { Number, Text, Path, Operator, LeftParen, RightParen, Comma }

	private sealed record Token(Kind Kind, string Value, int Position);

	private static List<Token> Tokenize(string text)
	{
		var tokens = new List<Token>();
		var i = 0;
		while (i < text.Length)
		{
			var c = text[i];
			if (char.IsWhiteSpace(c)) { i++; continue; }
			if (c is '(' or ')' or ',')
			{
				tokens.Add(new Token(c == '(' ? Kind.LeftParen : c == ')' ? Kind.RightParen : Kind.Comma, c.ToString(), i));
				i++;
				continue;
			}
			if (c is '+' or '-' or '*' or '/' or '\u00d7' or '\u00f7' or '\u2212')
			{
				var op = c switch { '\u00d7' => "*", '\u00f7' => "/", '\u2212' => "-", _ => c.ToString() };
				tokens.Add(new Token(Kind.Operator, op, i));
				i++;
				continue;
			}
			if (c is '\'' or '"')
			{
				var end = text.IndexOf(c, i + 1);
				if (end < 0) throw new ExpressionException("Text is missing its closing quote.", i);
				var value = text[(i + 1)..end];
				if (value.Contains('{') || value.Contains('}') || value.Contains('%'))
				{
					throw new ExpressionException("Text in a calculation can't contain {, } or %.", i);
				}
				tokens.Add(new Token(Kind.Text, value, i));
				i = end + 1;
				continue;
			}
			var number = NumberToken().Match(text, i);
			if (number.Success && number.Index == i)
			{
				tokens.Add(new Token(Kind.Number, number.Value, i));
				i += number.Length;
				continue;
			}
			var path = PathToken().Match(text, i);
			if (path.Success && path.Index == i)
			{
				tokens.Add(new Token(Kind.Path, path.Value, i));
				i += path.Length;
				continue;
			}
			throw new ExpressionException($"Unexpected '{c}'.", i);
		}
		return tokens;
	}

	// ---- Syntax tree --------------------------------------------------------------------------------------------------

	private abstract record Node(int Position);
	private sealed record NumberNode(string Value, int Position) : Node(Position);
	private sealed record TextNode(string Value, int Position) : Node(Position);
	private sealed record PathNode(string Path, int Position) : Node(Position);
	private sealed record NegateNode(Node Operand, int Position) : Node(Position);
	private sealed record BinaryNode(string Operator, Node Left, Node Right, int Position) : Node(Position);
	private sealed record CallNode(string Function, IReadOnlyList<Node> Arguments, int Position) : Node(Position);

	private sealed class Parser(List<Token> tokens, int length)
	{
		private int _index;

		private Token? Peek => _index < tokens.Count ? tokens[_index] : null;

		private int Here => Peek?.Position ?? length;

		public void ExpectEnd()
		{
			if (Peek is { } extra)
			{
				throw new ExpressionException(extra.Kind == Kind.RightParen
					? "There is a ')' without a matching '('."
					: $"Expected an operator (+ - * /) before '{extra.Value}'.", extra.Position);
			}
		}

		// expression := term (('+' | '-') term)*
		public Node ParseExpression()
		{
			var left = ParseTerm();
			while (Peek is { Kind: Kind.Operator, Value: "+" or "-" } op)
			{
				_index++;
				left = new BinaryNode(op.Value, left, ParseTerm(), op.Position);
			}
			return left;
		}

		// term := unary (('*' | '/') unary)*
		private Node ParseTerm()
		{
			var left = ParseUnary();
			while (Peek is { Kind: Kind.Operator, Value: "*" or "/" } op)
			{
				_index++;
				left = new BinaryNode(op.Value, left, ParseUnary(), op.Position);
			}
			return left;
		}

		private Node ParseUnary()
		{
			if (Peek is { Kind: Kind.Operator, Value: "-" } minus)
			{
				_index++;
				return new NegateNode(ParseUnary(), minus.Position);
			}
			if (Peek is { Kind: Kind.Operator, Value: "+" })
			{
				_index++;
				return ParseUnary();
			}
			return ParsePrimary();
		}

		private Node ParsePrimary()
		{
			var token = Peek ?? throw new ExpressionException("The calculation ends too early: a value is missing.", length);
			_index++;
			switch (token.Kind)
			{
				case Kind.Number:
					return new NumberNode(token.Value, token.Position);
				case Kind.Text:
					return new TextNode(token.Value, token.Position);
				case Kind.LeftParen:
					var inner = ParseExpression();
					Expect(Kind.RightParen, "Missing ')'.");
					return inner;
				case Kind.Path when Peek is { Kind: Kind.LeftParen }:
					var name = token.Value.ToLowerInvariant();
					if (!Functions.Contains(name))
					{
						throw new ExpressionException($"Unknown function '{token.Value}'. Use one of: {string.Join(", ", Functions)}.", token.Position);
					}
					_index++;
					var arguments = new List<Node>();
					if (Peek is not { Kind: Kind.RightParen })
					{
						arguments.Add(ParseExpression());
						while (Peek is { Kind: Kind.Comma })
						{
							_index++;
							arguments.Add(ParseExpression());
						}
					}
					Expect(Kind.RightParen, $"Missing ')' after the arguments of {name}.");
					return new CallNode(name, arguments, token.Position);
				case Kind.Path:
					if (token.Value.StartsWith("calc_", StringComparison.Ordinal))
					{
						throw new ExpressionException("Names starting with calc_ are reserved.", token.Position);
					}
					if (LiquidWords.Contains(token.Value.Split('.', '[')[0]))
					{
						throw new ExpressionException($"'{token.Value.Split('.', '[')[0]}' is a reserved word in templates and can't be used as a field name.", token.Position);
					}
					return new PathNode(token.Value, token.Position);
				default:
					throw new ExpressionException($"Expected a value but found '{token.Value}'.", token.Position);
			}
		}

		private void Expect(Kind kind, string message)
		{
			if (Peek?.Kind != kind) throw new ExpressionException(message, Here);
			_index++;
		}
	}

	// ---- Liquid -------------------------------------------------------------------------------------------------------

	private sealed class Emitter(IReadOnlyCollection<string>? lists)
	{
		private readonly StringBuilder _liquid = new();
		private int _temps;

		public List<string> Paths { get; } = [];
		public List<string> Lists { get; } = [];
		public string Liquid => _liquid.ToString();

		public void Assign(string variable, string expression) =>
			_liquid.Append("{%- assign ").Append(variable).Append(" = ").Append(expression).Append(" -%}");

		private string Temp() => TempPrefix + ++_temps;

		private string Store(string expression)
		{
			var temp = Temp();
			Assign(temp, expression);
			return temp;
		}

		public string Atom(Node node)
		{
			switch (node)
			{
				case NumberNode number:
					return number.Value;
				case TextNode text:
					return Quote(text.Value);
				case PathNode path:
					Paths.Add(path.Path);
					return path.Path;
				case NegateNode negate:
					return Store("0 | minus: " + Number(negate.Operand));
				case BinaryNode binary:
					var left = Number(binary.Left);
					var right = Number(binary.Right);
					return binary.Operator switch
					{
						"+" => Store(left + " | plus: " + right),
						"-" => Store(left + " | minus: " + right),
						"*" => Store(left + " | times: " + right),
						_ => Divide(left, right)
					};
				case CallNode call:
					return Call(call);
				default:
					throw new InvalidOperationException("Unknown node.");
			}
		}

		// Arithmetic needs numbers: text literals are refused with a pointer to concat().
		private string Number(Node node)
		{
			if (node is TextNode text)
			{
				throw new ExpressionException("Text can't be used in arithmetic. Use concat(...) to join text.", text.Position);
			}
			return Atom(node);
		}

		// Dividing by zero (or by a missing value) gives 0 instead of failing the whole document. Liquid divides whole
		// numbers as integers (7 / 2 = 3), so the divisor is made a decimal first.
		private string Divide(string left, string right)
		{
			var temp = Temp();
			Assign(temp, "0");
			_liquid.Append("{%- unless ").Append(right).Append(" == 0 or ").Append(right).Append(" == blank -%}");
			var divisor = Store(right + " | times: 1.0");
			Assign(temp, left + " | divided_by: " + divisor);
			_liquid.Append("{%- endunless -%}");
			return temp;
		}

		private string Call(CallNode call)
		{
			void Arity(int min, int max)
			{
				if (call.Arguments.Count < min || call.Arguments.Count > max)
				{
					var expected = min == max ? $"{min}" : max == int.MaxValue ? $"at least {min}" : $"{min} or {max}";
					throw new ExpressionException($"{call.Function}() takes {expected} value{(expected == "1" ? "" : "s")}.", call.Position);
				}
			}

			switch (call.Function)
			{
				case "sum":
				{
					Arity(1, 1);
					var (list, property) = ListAndProperty(call.Arguments[0], call.Function);
					return Store(list + " | sum: " + Quote(property));
				}
				case "count":
				{
					Arity(1, 1);
					var (list, property) = ListAndProperty(call.Arguments[0], call.Function);
					if (property.Length > 0) throw new ExpressionException("count() takes a list, for example count(locations).", call.Position);
					return Store(list + " | size");
				}
				case "average":
				{
					Arity(1, 1);
					var (list, property) = ListAndProperty(call.Arguments[0], call.Function);
					var total = Store(list + " | sum: " + Quote(property));
					var count = Store(list + " | size");
					return Divide(total, count);
				}
				case "min" or "max":
				{
					Arity(2, int.MaxValue);
					var filter = call.Function == "min" ? " | at_most: " : " | at_least: ";
					return Store(Number(call.Arguments[0]) + string.Concat(call.Arguments.Skip(1).Select(a => filter + Number(a))));
				}
				case "round":
				{
					Arity(1, 2);
					var places = "0";
					if (call.Arguments.Count == 2)
					{
						if (call.Arguments[1] is not NumberNode { Value: var digits } || !int.TryParse(digits, out var n) || n > 6)
						{
							throw new ExpressionException("round()'s second value is the number of decimal places (0 to 6).", call.Arguments[1].Position);
						}
						places = digits;
					}
					return Store(Number(call.Arguments[0]) + " | calc_round: " + places);
				}
				case "abs":
					Arity(1, 1);
					return Store(Number(call.Arguments[0]) + " | abs");
				case "concat":
				{
					Arity(1, int.MaxValue);
					var parts = call.Arguments.Select(a => a is NumberNode n ? Quote(n.Value) : Atom(a)).ToList();
					return Store(parts[0] + " | append: ''" + string.Concat(parts.Skip(1).Select(p => " | append: " + p)));
				}
				case "default":
					Arity(2, 2);
					return Store(Atom(call.Arguments[0]) + " | default: " + Atom(call.Arguments[1]));
				case "days_between":
				{
					Arity(2, 2);
					var start = Store(Atom(call.Arguments[0]) + " | date: '%s'");
					var end = Store(Atom(call.Arguments[1]) + " | date: '%s'");
					var seconds = Store(end + " | minus: " + start);
					return Store(seconds + " | divided_by: 86400 | floor");
				}
				default:
					throw new ExpressionException($"Unknown function '{call.Function}'.", call.Position);
			}
		}

		// "claims.amount" -> ("claims", "amount"): the longest known list the path starts with, then the item property.
		private (string List, string Property) ListAndProperty(Node node, string function)
		{
			if (node is not PathNode { Path: var path })
			{
				throw new ExpressionException($"{function}() takes a list field, for example {function}(claims.amount).", node.Position);
			}
			if (path.Contains('['))
			{
				throw new ExpressionException($"{function}() takes a whole list, without [ ].", node.Position);
			}
			string list;
			if (lists is { Count: > 0 })
			{
				list = lists.Where(l => path == l || path.StartsWith(l + ".", StringComparison.Ordinal)).OrderByDescending(l => l.Length).FirstOrDefault()
					?? throw new ExpressionException($"'{path}' is not a list. {function}() takes a list field, for example {function}(claims.amount).", node.Position);
			}
			else
			{
				var dot = path.LastIndexOf('.');
				list = function == "count" || dot < 0 ? path : path[..dot];
			}
			Lists.Add(list);
			return (list, path.Length > list.Length ? path[(list.Length + 1)..] : string.Empty);
		}

		// Text literals can't contain the quote they are wrapped in (Liquid strings have no escapes).
		private static string Quote(string value) =>
			!value.Contains('\'') ? "'" + value + "'"
			: !value.Contains('"') ? "\"" + value + "\""
			: throw new ExpressionException("Text can't contain both ' and \".", 0);
	}

	[GeneratedRegex(@"\d+(?:\.\d+)?")]
	private static partial Regex NumberToken();

	[GeneratedRegex(@"[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*|\[\d+\])*")]
	private static partial Regex PathToken();
}
