using System.Collections.ObjectModel;

namespace Sapientum.Materials.Domain;

public enum TemplateValueKind { Null, Text, Number, Boolean }
public sealed record TemplateValue(TemplateValueKind Kind, string? Text = null, decimal Number = 0, bool Boolean = false)
{
    public string Display => Kind switch { TemplateValueKind.Text => Text ?? "", TemplateValueKind.Number => Number.ToString(System.Globalization.CultureInfo.InvariantCulture), TemplateValueKind.Boolean => Boolean.ToString(), _ => "" };
}
public enum TemplateBlockKind { Text, Field, Qr, AnswerArea }
public enum TemplateField { Heading, TaskNumber, Statement, BlankNumber, TeamName, GameTitle, ModifierLabel }
public sealed record TemplateBlock(TemplateBlockKind Kind, string? Text = null, TemplateField? Field = null)
{
    public void Validate()
    {
        if (!Enum.IsDefined(Kind) || (Kind == TemplateBlockKind.Text && Text is null) ||
            (Kind == TemplateBlockKind.Field && (Field is null || !Enum.IsDefined(Field.Value))))
            throw new DomainException("VALIDATION_ERROR", "Unsupported template block.");
    }
}
public sealed class MaterialTemplate
{
    public string Key { get; }
    public IReadOnlyList<TemplateBlock> AnswerSheet { get; }
    public IReadOnlyList<TemplateBlock> TaskSheet { get; }
    public MaterialTemplate(string key, IEnumerable<TemplateBlock> answerSheet, IEnumerable<TemplateBlock> taskSheet)
    {
        Key = Guard.Id(key, nameof(key)); AnswerSheet = Array.AsReadOnly(answerSheet.ToArray()); TaskSheet = Array.AsReadOnly(taskSheet.ToArray());
        if (AnswerSheet.Count == 0 || TaskSheet.Count == 0) throw new DomainException("VALIDATION_ERROR", "Template sheets must contain blocks.");
        foreach (var block in AnswerSheet.Concat(TaskSheet)) block.Validate();
        if (!AnswerSheet.Any(b => b.Kind == TemplateBlockKind.Qr))
            throw new DomainException("VALIDATION_ERROR", "An answer sheet must contain an identifying QR.");
    }
}

/// <summary>Public print projection deliberately cannot hold a reference answer.</summary>
public sealed record PrintableTask(string Id, string Number, string Statement);
public sealed record PrintableBlank(AnswerBlank Blank, string TeamName);
public sealed class PrintSnapshot
{
    public MaterialTemplate Template { get; }
    public IReadOnlyDictionary<string, TemplateValue> Parameters { get; }
    public IReadOnlyList<PrintableBlank> Blanks { get; }
    public IReadOnlyList<PrintableTask> Tasks { get; }
    public PrintSnapshot(MaterialTemplate template, IReadOnlyDictionary<string, TemplateValue> parameters,
        IEnumerable<PrintableBlank> blanks, IEnumerable<PrintableTask> tasks)
    {
        Template = template;
        Parameters = new ReadOnlyDictionary<string, TemplateValue>(new Dictionary<string, TemplateValue>(parameters));
        Blanks = Array.AsReadOnly(blanks.ToArray()); Tasks = Array.AsReadOnly(tasks.ToArray());
        if (Blanks.Count + Tasks.Count == 0) throw new DomainException("VALIDATION_ERROR", "Select at least one material.");
        if (Blanks.Select(b => b.Blank.Id).Distinct().Count() != Blanks.Count || Tasks.Select(t => t.Id).Distinct().Count() != Tasks.Count)
            throw new DomainException("VALIDATION_ERROR", "Selected materials must be unique.");
    }
}
