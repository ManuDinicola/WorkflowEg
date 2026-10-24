using Flowd.Expressions.Evaluation;
using Flowd.Expressions.Parsing;
using Flowd.Expressions.Values;

namespace Flowd.Expressions;

/// <summary>
/// Public entry point of the flowd-FEEL subset (FR-EXPR-001). Text starting with <c>=</c> is a dynamic expression;
/// anything else is a static string literal.
/// </summary>
public static class FeelEvaluator
{
    private const string DynamicPrefix = "=";

    public static bool IsDynamic(string text) => text.TrimStart().StartsWith(DynamicPrefix, StringComparison.Ordinal);

    /// <summary>Returns the syntax error of a dynamic expression, or null when it parses (used at deployment, FR-DEP-004).</summary>
    public static EvaluationError? CheckSyntax(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!IsDynamic(text))
        {
            return null;
        }

        try
        {
            _ = Parser.Parse(Body(text));
            return null;
        }
        catch (SyntaxException ex)
        {
            return new EvaluationError(ExpressionErrorCodes.Syntax, ex.Message);
        }
    }

    public static EvaluationResult Evaluate(string text, ExpressionContext context, EvaluationBudget? budget = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(context);
        if (!IsDynamic(text))
        {
            return new EvaluationSuccess(text);
        }

        try
        {
            var tree = Parser.Parse(Body(text));
            var evaluator = new Evaluator(new BudgetMeter(budget ?? EvaluationBudget.Default), context.Now);
            return new EvaluationSuccess(evaluator.Evaluate(tree, new Scope(context.Variables, null)));
        }
        catch (SyntaxException ex)
        {
            return new EvaluationError(ExpressionErrorCodes.Syntax, ex.Message);
        }
        catch (EvaluationFailure ex)
        {
            return new EvaluationError(ex.Code, ex.Message);
        }
    }

    private static string Body(string text) => text.TrimStart()[DynamicPrefix.Length..];
}
