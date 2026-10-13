using PdfSharp.Drawing;
using PdfSharp.Pdf;
using QRCoder;
using Sapientum.Materials.Application;
using Sapientum.Materials.Domain;
using System.Globalization;

namespace Sapientum.Materials.Infrastructure.Pdf;

/// <summary>Renders a closed, public projection. No game-specific branches or task-catalog access.</summary>
public sealed class PdfSharpMaterialRenderer : IPdfRenderer
{
    public Task<byte[]> RenderAsync(PrintSnapshot snapshot, CancellationToken cancellationToken)
    {
        using var document = new PdfDocument();
        document.Info.Title = "Sapientum — материалы игры";
        document.Info.Creator = "Sapientum Materials";
        foreach (var blank in snapshot.Blanks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var sheet = new Sheet(document);
            foreach (var block in snapshot.Template.AnswerSheet) DrawBlock(sheet, block, snapshot, blank, null);
        }
        foreach (var task in snapshot.Tasks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var sheet = new Sheet(document);
            foreach (var block in snapshot.Template.TaskSheet) DrawBlock(sheet, block, snapshot, null, task);
        }
        using var stream = new MemoryStream(); document.Save(stream, false);
        return Task.FromResult(stream.ToArray());
    }
    private static void DrawBlock(Sheet sheet, TemplateBlock block, PrintSnapshot snapshot, PrintableBlank? blank, PrintableTask? task)
    {
        switch (block.Kind)
        {
            case TemplateBlockKind.Text: sheet.Text(block.Text ?? ""); break;
            case TemplateBlockKind.Field:
                var value = block.Field switch
                {
                    TemplateField.Heading => Parameter(snapshot, "heading"),
                    TemplateField.GameTitle => Parameter(snapshot, "gameTitle"),
                    TemplateField.TaskNumber => $"Задача № {blank?.Blank.TaskNumber ?? task?.Number}",
                    TemplateField.BlankNumber => blank is null ? "" : $"Бланк № {blank.Blank.Number}",
                    TemplateField.ModifierLabel => blank?.Blank.Modifier is null ? "" : $"Бонус: {blank.Blank.Modifier.Label}",
                    TemplateField.TeamName => blank is null ? "" : $"Команда: {blank.TeamName}",
                    TemplateField.Statement => task?.Statement ?? "",
                    _ => throw new InvalidOperationException("Unsupported field reached the renderer.")
                };
                sheet.Text(value, block.Field == TemplateField.Heading ? 20 : 14); break;
            case TemplateBlockKind.Qr:
                if (blank is not null) sheet.Qr(blank.Blank.QrToken); break;
            case TemplateBlockKind.AnswerArea: sheet.AnswerArea(); break;
            default: throw new InvalidOperationException("Unsupported block reached the renderer.");
        }
    }
    private static string Parameter(PrintSnapshot snapshot, string key) => snapshot.Parameters.TryGetValue(key, out var value) ? value.Display : "";

    private sealed class Sheet : IDisposable
    {
        private readonly PdfDocument document;
        private XGraphics graphics = null!;
        private double y;
        private const double Margin = 42;
        private const double Width = 511;
        private const double Bottom = 800;
        public Sheet(PdfDocument document) { this.document = document; NewPage(); }
        private void NewPage()
        {
            graphics?.Dispose();
            var page = document.AddPage(); page.Size = PdfSharp.PageSize.A4;
            graphics = XGraphics.FromPdfPage(page); y = Margin;
        }
        private void Reserve(double height) { if (y + height > Bottom) NewPage(); }
        public void Text(string value, double fontSize = 14)
        {
            if (string.IsNullOrEmpty(value)) return;
            var font = new XFont("Sapientum", fontSize, XFontStyleEx.Regular, new XPdfFontOptions(PdfFontEncoding.Unicode));
            foreach (var paragraph in value.Replace("\r", "").Split('\n'))
            {
                var line = "";
                foreach (var word in paragraph.Split(' '))
                {
                    var candidate = line.Length == 0 ? word : $"{line} {word}";
                    if (graphics.MeasureString(candidate, font).Width > Width && line.Length > 0) { DrawLine(line, font, fontSize); line = ""; }
                    // Long tokens are split at character boundaries instead of overflowing the page.
                    foreach (var character in word)
                    {
                        var next = line.Length == 0 ? character.ToString() : line + character;
                        if (graphics.MeasureString(next, font).Width > Width) { DrawLine(line, font, fontSize); line = ""; }
                        line += character;
                    }
                    line += " ";
                }
                DrawLine(line.TrimEnd(), font, fontSize);
            }
            y += 8;
        }
        private void DrawLine(string value, XFont font, double size)
        {
            Reserve(size * 1.5); graphics.DrawString(value, font, XBrushes.Black, new XRect(Margin, y, Width, size * 1.5), XStringFormats.TopLeft); y += size * 1.5;
        }
        public void Qr(string value)
        {
            const double size = 190;
            Reserve(size + 20);
            using var data = QRCodeGenerator.GenerateQrCode(value, QRCodeGenerator.ECCLevel.M);
            var matrix = data.ModuleMatrix; var cell = size / matrix.Count;
            graphics.DrawRectangle(XBrushes.White, Margin, y, size, size);
            for (var row = 0; row < matrix.Count; row++)
                for (var column = 0; column < matrix.Count; column++)
                    if (matrix[row][column]) graphics.DrawRectangle(XBrushes.Black, Margin + column * cell, y + row * cell, cell + 0.02, cell + 0.02);
            y += size + 20;
        }
        public void AnswerArea()
        {
            Text("Ответ:"); Reserve(120);
            graphics.DrawRectangle(new XPen(XColors.Gray, 1), Margin, y, Width, 110); y += 120;
        }
        public void Dispose() => graphics.Dispose();
    }
}
