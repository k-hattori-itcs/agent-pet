using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SkiaSharp;

namespace TokenPet.Services;

public class SpriteGenerator
{
    private const int Size = 48;
    private static readonly SKColor Orange = new(255, 165, 0);
    private static readonly SKColor DarkOrange = new(200, 100, 0);
    private static readonly SKColor LightOrange = new(255, 200, 100);
    private static readonly SKColor White = SKColors.White;
    private static readonly SKColor Black = SKColors.Black;
    private static readonly SKColor Pink = new(255, 182, 193);
    private static readonly SKColor DarkBrown = new(60, 30, 10);
    private static readonly SKColor Transparent = SKColors.Transparent;

    public static BitmapSource? GenerateFrame(string animation, int frameIndex)
    {
        using var surface = SKSurface.Create(new SKImageInfo(Size, Size));
        var canvas = surface.Canvas;
        canvas.Clear(Transparent);

        switch (animation)
        {
            case "idle": DrawIdle(canvas, frameIndex); break;
            case "walk": DrawWalk(canvas, frameIndex); break;
            case "run_left": DrawWalk(canvas, frameIndex, true); break;
            case "wave": DrawIdle(canvas, 0); break;
            case "jump": DrawJump(canvas, frameIndex); break;
            case "fail": DrawIdle(canvas, 0); break;
            case "sleep": DrawSleep(canvas, frameIndex); break;
            case "sprint": DrawWalk(canvas, frameIndex); break;
            case "sit": DrawSit(canvas, frameIndex); break;
            default: DrawIdle(canvas, 0); break;
        }

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return LoadBitmap(data.ToArray());
    }

    public static Dictionary<string, BitmapSource[]> GenerateAllFrames()
    {
        return Models.AnimationDefs.All.ToDictionary(
            a => a.Name,
            a => Enumerable.Range(0, a.FrameCount).Select(i => GenerateFrame(a.Name, i)!)
                .Where(b => b != null).Cast<BitmapSource>().ToArray()
        );
    }

    private static void DrawIdle(SKCanvas canvas, int frame, bool flipH = false)
    {
        using var paint = new SKPaint { IsAntialias = false };
        int cx = Size / 2;
        int bounce = frame % 4 >= 2 ? 1 : 0;

        canvas.Save();
        if (flipH) { canvas.Scale(-1, 1, cx, 0); }

        // Body
        paint.Color = Orange;
        DrawCircle(canvas, cx, 16 - bounce, 10, paint);

        // Head
        DrawCircle(canvas, cx, 5 - bounce, 8, paint);

        // Ears
        paint.Color = Orange;
        DrawTriangle(canvas, cx - 6, 0 - bounce, cx - 8, -6 - bounce, cx - 3, 0 - bounce);
        DrawTriangle(canvas, cx + 6, 0 - bounce, cx + 8, -6 - bounce, cx + 3, 0 - bounce);
        paint.Color = Pink;
        DrawTriangle(canvas, cx - 5, 0 - bounce, cx - 7, -4 - bounce, cx - 4, 0 - bounce);
        DrawTriangle(canvas, cx + 5, 0 - bounce, cx + 7, -4 - bounce, cx + 4, 0 - bounce);

        // Eyes
        paint.Color = White;
        DrawCircle(canvas, cx - 3, 4 - bounce, 2, paint);
        DrawCircle(canvas, cx + 3, 4 - bounce, 2, paint);
        paint.Color = Black;
        DrawCircle(canvas, cx - 3, 4 - bounce, 1, paint);
        DrawCircle(canvas, cx + 3, 4 - bounce, 1, paint);

        // Blink
        if (frame == 1 || frame == 3)
        {
            paint.Color = Orange;
            DrawRect(canvas, cx - 5, 4 - bounce, 10, 1, paint);
        }

        // Nose & mouth
        paint.Color = Pink;
        DrawCircle(canvas, cx, 6 - bounce, 1, paint);
        paint.Color = DarkBrown;
        canvas.DrawLine(cx - 1, 7 - bounce, cx - 3, 8 - bounce, paint);
        canvas.DrawLine(cx + 1, 7 - bounce, cx + 3, 8 - bounce, paint);

        // Belly
        paint.Color = White;
        DrawCircle(canvas, cx, 14 - bounce, 6, paint);

        // Paws
        paint.Color = White;
        DrawCircle(canvas, cx - 4, 20 - bounce, 3, paint);
        DrawCircle(canvas, cx + 4, 20 - bounce, 3, paint);

        // Tail
        paint.Color = Orange;
        DrawCurvedTail(canvas, cx + 10, 14 - bounce);

        canvas.Restore();
    }

    private static void DrawWalk(SKCanvas canvas, int frame, bool flipH = false)
    {
        DrawIdle(canvas, frame, flipH);
        using var paint = new SKPaint { IsAntialias = false, Color = Orange };
        int legOffset = frame % 2 == 0 ? 2 : -2;
        int cx = Size / 2;
        if (flipH) cx = Size - cx;
        // Leg movement
        paint.Color = White;
        DrawCircle(canvas, cx - 4 + legOffset, 23, 2, paint);
        DrawCircle(canvas, cx + 4 - legOffset, 23, 2, paint);
    }

    private static void DrawJump(SKCanvas canvas, int frame)
    {
        using var paint = new SKPaint { IsAntialias = false };
        int cx = Size / 2;
        int jump = frame < 2 ? -8 : -4;

        // Body
        paint.Color = Orange;
        DrawCircle(canvas, cx, 16 + jump, 10, paint);

        // Head
        DrawCircle(canvas, cx, 5 + jump, 8, paint);

        // Ears
        DrawTriangle(canvas, cx - 6, 0 + jump, cx - 8, -6 + jump, cx - 3, 0 + jump);
        DrawTriangle(canvas, cx + 6, 0 + jump, cx + 8, -6 + jump, cx + 3, 0 + jump);

        // Eyes (happy)
        paint.Color = Black;
        DrawCircle(canvas, cx - 3, 4 + jump, 1, paint);
        DrawCircle(canvas, cx + 3, 4 + jump, 1, paint);

        // Paws up
        paint.Color = White;
        DrawCircle(canvas, cx - 6, 10 + jump, 3, paint);
        DrawCircle(canvas, cx + 6, 10 + jump, 3, paint);

        // Back legs
        DrawCircle(canvas, cx - 3, 22 + jump, 3, paint);
        DrawCircle(canvas, cx + 3, 22 + jump, 3, paint);
    }

    private static void DrawSleep(SKCanvas canvas, int frame)
    {
        using var paint = new SKPaint { IsAntialias = false };
        int cx = Size / 2;
        int breathe = frame % 2 == 0 ? 0 : 1;

        // Body (laying down, oval)
        paint.Color = Orange;
        DrawEllipse(canvas, cx, 22, 12, 7 + breathe, paint);

        // Head
        DrawCircle(canvas, cx - 6, 20, 8, paint);

        // Ears
        DrawTriangle(canvas, cx - 12, 14, cx - 14, 8, cx - 9, 14);
        DrawTriangle(canvas, cx - 2, 14, cx, 8, cx - 5, 14);

        // Eyes (closed)
        paint.Color = Black;
        canvas.DrawLine(cx - 9, 19, cx - 7, 19, paint);
        canvas.DrawLine(cx - 4, 19, cx - 2, 19, paint);

        // Nose
        paint.Color = Pink;
        DrawCircle(canvas, cx - 7, 21, 1, paint);

        // Tail
        paint.Color = Orange;
        DrawCurvedTail(canvas, cx + 10, 20);

        // Zzz
        if (frame == 0)
        {
            paint.Color = White;
            using var font = new SKFont(SKTypeface.Default, 10);
            canvas.DrawText("Z", cx + 5, 10, font, paint);
            canvas.DrawText("z", cx + 10, 4, font, paint);
        }
    }

    private static void DrawSit(SKCanvas canvas, int frame)
    {
        using var paint = new SKPaint { IsAntialias = false };
        int cx = Size / 2;

        // Body (sitting)
        paint.Color = Orange;
        DrawCircle(canvas, cx, 14, 10, paint);

        // Head
        DrawCircle(canvas, cx, 4, 8, paint);

        // Ears
        DrawTriangle(canvas, cx - 6, -1, cx - 8, -7, cx - 3, -1);
        DrawTriangle(canvas, cx + 6, -1, cx + 8, -7, cx + 3, -1);

        // Eyes (looking around)
        paint.Color = White;
        DrawCircle(canvas, cx - 3 + (frame % 3 - 1), 3, 2, paint);
        DrawCircle(canvas, cx + 3 + (frame % 3 - 1), 3, 2, paint);
        paint.Color = Black;
        DrawCircle(canvas, cx - 3 + (frame % 3 - 1), 3, 1, paint);
        DrawCircle(canvas, cx + 3 + (frame % 3 - 1), 3, 1, paint);

        // Nose
        paint.Color = Pink;
        DrawCircle(canvas, cx, 5, 1, paint);

        // Belly
        paint.Color = White;
        DrawCircle(canvas, cx, 12, 6, paint);

        // Front paws
        DrawCircle(canvas, cx - 5, 20, 3, paint);
        DrawCircle(canvas, cx + 5, 20, 3, paint);

        // Back paws (spread)
        DrawOval(canvas, cx - 6, 22, 4, 2, paint);
        DrawOval(canvas, cx + 6, 22, 4, 2, paint);

        // Tail
        paint.Color = Orange;
        DrawCurvedTail(canvas, cx + 11, 16);
    }

    private static void DrawCurvedTail(SKCanvas canvas, int x, int y)
    {
        using var paint = new SKPaint { IsAntialias = false, Color = Orange, Style = SKPaintStyle.Stroke, StrokeWidth = 3 };
        var path = new SKPath();
        path.MoveTo(x, y);
        path.CubicTo(x + 6, y - 3, x + 8, y - 8, x + 4, y - 10);
        canvas.DrawPath(path, paint);
    }

    private static void DrawCircle(SKCanvas canvas, int x, int y, int r, SKPaint paint)
    {
        canvas.DrawRect(x - r, y - r, r * 2, r * 2, paint);
    }

    private static void DrawEllipse(SKCanvas canvas, int cx, int cy, int rx, int ry, SKPaint paint)
    {
        canvas.DrawOval(cx - rx, cy - ry, rx * 2, ry * 2, paint);
    }

    private static void DrawRect(SKCanvas canvas, int x, int y, int w, int h, SKPaint paint)
    {
        canvas.DrawRect(x, y, w, h, paint);
    }

    private static void DrawOval(SKCanvas canvas, int x, int y, int w, int h, SKPaint paint)
    {
        canvas.DrawOval(x - w / 2f, y - h / 2f, w, h, paint);
    }

    private static void DrawTriangle(SKCanvas canvas, int x1, int y1, int x2, int y2, int x3, int y3)
    {
        using var paint = new SKPaint { IsAntialias = false, Color = Orange };
        var path = new SKPath();
        path.MoveTo(x1, y1);
        path.LineTo(x2, y2);
        path.LineTo(x3, y3);
        path.Close();
        canvas.DrawPath(path, paint);
    }

    private static BitmapSource LoadBitmap(byte[] data)
    {
        using var stream = new MemoryStream(data);
        var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        return decoder.Frames[0];
    }
}
