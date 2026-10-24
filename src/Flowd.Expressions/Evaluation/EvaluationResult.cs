using Flowd.Expressions.Values;

namespace Flowd.Expressions.Evaluation;

/// <summary>Stable error codes of the expression language (CC-012).</summary>
public static class ExpressionErrorCodes
{
    public const string Syntax = "SYNTAX_ERROR";
    public const string UndefinedVariable = "UNDEFINED_VARIABLE";
    public const string Type = "TYPE_ERROR";
    public const string BudgetExceeded = "BUDGET_EXCEEDED";
    public const string UnknownFunction = "UNKNOWN_FUNCTION";
}

/// <summary>Successful evaluation.</summary>
public sealed record EvaluationSuccess(ExpressionValue Value);

/// <summary>Failed evaluation. Never thrown: evaluation failures are expected (CC-008).</summary>
public sealed record EvaluationError(string Code, string Message);

/// <summary>Outcome of evaluating an expression.</summary>
public union EvaluationResult(EvaluationSuccess, EvaluationError);
