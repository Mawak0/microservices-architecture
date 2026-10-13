namespace Sapientum.Review.Domain;
public enum FailureKind { Validation, Conflict, Missing, Dependency }
public sealed class DomainFailure(string code, FailureKind kind = FailureKind.Conflict, string? message = null)
    : Exception(message ?? code)
{
    public string Code { get; } = code;
    public FailureKind Kind { get; } = kind;
}
public static class Required
{
    public static string Text(string? value) => !string.IsNullOrWhiteSpace(value)
        ? value : throw new DomainFailure("VALIDATION_ERROR", FailureKind.Validation);
}
public enum Verdict { Correct, Incorrect }
public static class Verdicts
{
    public static Verdict Parse(string? value) => value switch
    {
        "correct" => Verdict.Correct,
        "incorrect" => Verdict.Incorrect,
        _ => throw new DomainFailure("VALIDATION_ERROR", FailureKind.Validation)
    };
    public static string Wire(this Verdict value) => value == Verdict.Correct ? "correct" : "incorrect";
}
