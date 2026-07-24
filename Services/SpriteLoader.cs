using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkiaSharp;

namespace AgentCompanion.Services;

public static class SpriteLoader
{
    private const int MaxImageDimension = 8192;
    private const long MaxImagePixels = 32L * 1024 * 1024;
    public static BitmapSource? LoadSpritesheet(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            ValidateImage(path);
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext is ".webp")
                return LoadWebP(path);
            if (ext is ".png" or ".jpg" or ".jpeg" or ".bmp")
                return LoadStandard(path);
            return null;
        }
        catch (Exception ex)
        {
            AppLogger.Error("Spritesheet loading failed.", ex);
            return null;
        }
    }

    internal static void ValidateImage(string path)
    {
        using var codec = SKCodec.Create(path)
            ?? throw new InvalidDataException("The image could not be decoded.");
        var info = codec.Info;
        if (info.Width <= 0 || info.Height <= 0 ||
            info.Width > MaxImageDimension || info.Height > MaxImageDimension ||
            (long)info.Width * info.Height > MaxImagePixels)
            throw new InvalidDataException("The decoded image dimensions exceed the configured limit.");

        var extension = Path.GetExtension(path);
        var formatMatches = extension.ToLowerInvariant() switch
        {
            ".png" => codec.EncodedFormat == SKEncodedImageFormat.Png,
            ".webp" => codec.EncodedFormat == SKEncodedImageFormat.Webp,
            ".jpg" or ".jpeg" => codec.EncodedFormat == SKEncodedImageFormat.Jpeg,
            ".bmp" => codec.EncodedFormat == SKEncodedImageFormat.Bmp,
            _ => false
        };
        if (!formatMatches)
            throw new InvalidDataException("The image content does not match its file extension.");
    }
    private static BitmapSource LoadStandard(string path)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.UriSource = new Uri(path);
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    private static BitmapSource LoadWebP(string path)
    {
        using var skBitmap = SKBitmap.Decode(path);
        if (skBitmap == null) throw new InvalidDataException("Failed to decode WebP");

        using var image = SKImage.FromBitmap(skBitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = new MemoryStream(data.ToArray());
        var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var source = decoder.Frames[0];
        source.Freeze();
        return source;
    }
}
