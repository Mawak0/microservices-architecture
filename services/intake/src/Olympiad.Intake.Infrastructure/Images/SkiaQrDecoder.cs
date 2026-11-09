using Olympiad.Intake.Application;
using Olympiad.Intake.Domain;
using SkiaSharp;
using ZXing;
using ZXing.Common;

namespace Olympiad.Intake.Infrastructure.Images;

public sealed class SkiaQrDecoder : IImageQrDecoder
{
    public string Decode(ReadOnlyMemory<byte> image, string mediaType)
    {
        using var stream = new SKMemoryStream(image.ToArray());
        using var codec = SKCodec.Create(stream);
        if (codec is null || codec.Info.Width <= 0 || codec.Info.Height <= 0 ||
            (long)codec.Info.Width * codec.Info.Height > 25_000_000)
            throw InvalidImage();
        var expected = mediaType switch { "image/png" => SKEncodedImageFormat.Png, "image/jpeg" => SKEncodedImageFormat.Jpeg, _ => (SKEncodedImageFormat?)null };
        if (expected is null || codec.EncodedFormat != expected) throw InvalidImage();
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var bitmap = new SKBitmap(info);
        if (codec.GetPixels(info, bitmap.GetPixels()) != SKCodecResult.Success) throw InvalidImage();
        var source = new RGBLuminanceSource(bitmap.Bytes, bitmap.Width, bitmap.Height, RGBLuminanceSource.BitmapFormat.RGBA32);
        var reader = new BarcodeReaderGeneric
        {
            AutoRotate = true,
            Options = new DecodingOptions { TryHarder = true, PossibleFormats = [BarcodeFormat.QR_CODE] }
        };
        var results = reader.DecodeMultiple(source);
        if (results is not { Length: 1 } || string.IsNullOrWhiteSpace(results[0].Text))
            throw new IntakeException(IntakeError.QrUnreadable, "The image must contain exactly one readable QR code.");
        return results[0].Text;
    }
    private static IntakeException InvalidImage() => new(IntakeError.InvalidImage, "The uploaded bytes are not a supported PNG/JPEG image.");
}
