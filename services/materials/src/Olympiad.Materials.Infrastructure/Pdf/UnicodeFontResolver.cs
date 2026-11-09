using PdfSharp.Fonts;

namespace Olympiad.Materials.Infrastructure.Pdf;

public sealed class UnicodeFontResolver : IFontResolver
{
    private readonly byte[] regular;
    public UnicodeFontResolver(string? configuredPath)
    {
        var path = configuredPath ?? (OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "arial.ttf")
            : "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf");
        if (!File.Exists(path)) throw new InvalidOperationException($"A Unicode TrueType font is required at {path}. Set Pdf__FontPath.");
        regular = File.ReadAllBytes(path);
    }
    public byte[] GetFont(string faceName) => regular.ToArray();
    public FontResolverInfo ResolveTypeface(string familyName, bool bold, bool italic) => new("olympiad-unicode", bold, italic);
}
