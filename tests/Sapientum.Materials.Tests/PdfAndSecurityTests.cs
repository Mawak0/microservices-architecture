using PdfSharp.Fonts;
using Sapientum.Materials.Domain;
using Sapientum.Materials.Infrastructure.Pdf;
using Sapientum.Materials.Infrastructure.Security;
using Xunit;

namespace Sapientum.Materials.Tests;

public sealed class PdfAndSecurityTests
{
    [Fact] public void SignedPayloadContainsImmutableIdentityAndRejectsTamperingOrOtherKeys()
    {
        var protector = new HmacBlankTokenProtector(new byte[32]);
        var identity = new BlankIdentity("blank", 7, "game", "team", "task", "42", new(ModifierKind.Multiply, 2));
        var token = protector.Protect(identity); Assert.Equal(identity, protector.Unprotect(token));
        Assert.Null(protector.Unprotect(token + "tampered")); Assert.Null(protector.Unprotect("fixture-signed:blank"));
        Assert.Null(new HmacBlankTokenProtector(Enumerable.Repeat((byte)1, 32).ToArray()).Unprotect(token));
    }
    [Fact] public async Task ActualPdfContainsCyrillicTaskTextAndEmbeddedUnicodeFont()
    {
        GlobalFontSettings.FontResolver ??= new UnicodeFontResolver(null);
        var bytes = await new PdfSharpMaterialRenderer().RenderAsync(Sample.Snapshot(), default);
        using var document = UglyToad.PdfPig.PdfDocument.Open(bytes);
        Assert.Equal(2, document.NumberOfPages);
        var text = string.Join("\n", document.GetPages().Select(p => p.Text));
        Assert.Contains("Математическая олимпиада", text); Assert.Contains("Условие: вычислите 2 + 2", text);
        Assert.Contains("Ответ:", text);
        Assert.DoesNotContain("referenceAnswer", text);
        Assert.Contains("/FontFile2", System.Text.Encoding.ASCII.GetString(bytes));
    }
    [Fact] public async Task LongStatementsContinueOnNewPagesWithoutBeingDiscarded()
    {
        GlobalFontSettings.FontResolver ??= new UnicodeFontResolver(null);
        var statement = string.Join(" ", Enumerable.Repeat("Математическое условие", 1200)) + " КОНЕЦ_УСЛОВИЯ";
        var bytes = await new PdfSharpMaterialRenderer().RenderAsync(Sample.Snapshot(statement), default);
        using var document = UglyToad.PdfPig.PdfDocument.Open(bytes);
        Assert.True(document.NumberOfPages > 2);
        Assert.Contains("КОНЕЦ_УСЛОВИЯ", string.Join("\n", document.GetPages().Select(p => p.Text)));
    }
}
