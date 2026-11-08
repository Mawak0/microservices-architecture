using Sapientum.Intake.Domain;
using Sapientum.Intake.Infrastructure.Images;
using SkiaSharp;
using ZXing;
using ZXing.QrCode;
using Xunit;

namespace Sapientum.Intake.Tests;

public sealed class QrDecoderTests
{
    [Theory]
    [InlineData("image/png", SKEncodedImageFormat.Png)]
    [InlineData("image/jpeg", SKEncodedImageFormat.Jpeg)]
    public void DecodesRealQrFromPngAndJpeg(string mediaType, SKEncodedImageFormat format)
    {
        using var bitmap = Qr("registered:blank-123");
        using var bytes = bitmap.Encode(format, 95);
        Assert.Equal("registered:blank-123", new SkiaQrDecoder().Decode(bytes.ToArray(), mediaType));
    }
    [Fact]
    public void RejectsCorruptBytesAndDeclaredFormatMismatch()
    {
        var decoder = new SkiaQrDecoder();
        Assert.Equal(IntakeError.InvalidImage, Assert.Throws<IntakeException>(() => decoder.Decode(new byte[] { 1, 2, 3 }, "image/png")).Error);
        using var bitmap = Qr("a"); using var bytes = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        Assert.Equal(IntakeError.InvalidImage, Assert.Throws<IntakeException>(() => decoder.Decode(bytes.ToArray(), "image/jpeg")).Error);
    }
    [Fact]
    public void RejectsValidImageWithoutQr()
    {
        using var bitmap = new SKBitmap(40, 40); bitmap.Erase(SKColors.White);
        using var bytes = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        Assert.Equal(IntakeError.QrUnreadable, Assert.Throws<IntakeException>(() => new SkiaQrDecoder().Decode(bytes.ToArray(), "image/png")).Error);
    }
    [Fact]
    public void RejectsTwoDifferentReadableQrs()
    {
        using var first = Qr("first"); using var second = Qr("second"); using var image = new SKBitmap(800, 400);
        image.Erase(SKColors.White);
        using (var canvas = new SKCanvas(image)) { canvas.DrawBitmap(first, 0, 0); canvas.DrawBitmap(second, 400, 0); }
        using var bytes = image.Encode(SKEncodedImageFormat.Png, 100);
        Assert.Equal(IntakeError.QrUnreadable, Assert.Throws<IntakeException>(() => new SkiaQrDecoder().Decode(bytes.ToArray(), "image/png")).Error);
    }
    private static SKBitmap Qr(string text)
    {
        var matrix = new QRCodeWriter().encode(text, BarcodeFormat.QR_CODE, 300, 300);
        var bitmap = new SKBitmap(matrix.Width, matrix.Height);
        for (var y = 0; y < matrix.Height; y++) for (var x = 0; x < matrix.Width; x++) bitmap.SetPixel(x, y, matrix[x, y] ? SKColors.Black : SKColors.White);
        return bitmap;
    }
}
