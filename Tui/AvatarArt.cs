using System.Text;
using StbImageSharp;

namespace RealTimeAppsTui.Tui;

internal sealed record AvatarArt(IReadOnlyList<string> Lines);

internal static class AvatarArtFactory
{
    private const byte BackdropR = 0x1a;
    private const byte BackdropG = 0x1b;
    private const byte BackdropB = 0x26;
    private const int BackgroundKeyTolerance = 2000;

    public static AvatarArt? Create(
        byte[] data,
        int widthCells,
        int maxHeightCells,
        bool transparentBackground = false)
    {
        try
        {
            ImageResult? image = ImageResult.FromMemory(
                data, ColorComponents.RedGreenBlueAlpha);

            if (image is null || image.Width <= 0 || image.Height <= 0)
            {
                return null;
            }

            int width = Math.Clamp(widthCells, 2, 40);
            int maxHeight = Math.Clamp(maxHeightCells, 1, 24) * 2;
            double aspect = (double)image.Height / image.Width;
            int height = Math.Clamp(
                (int)Math.Round(width * aspect), 2, maxHeight);

            if (height % 2 != 0)
            {
                height = Math.Min(height + 1, maxHeight);
            }

            if (height % 2 != 0)
            {
                height--;
            }

            Rgb? backgroundKey = transparentBackground
                ? ReadCornerColor(image)
                : null;

            int rows = height / 2;
            int subWidth = width * 2;
            List<string> lines = new(rows);
            StringBuilder builder = new(width * 40);

            for (int cellRow = 0; cellRow < rows; cellRow++)
            {
                builder.Clear();

                for (int x = 0; x < width; x++)
                {
                    int subX = x * 2;
                    int subY = cellRow * 2;

                    Rgba upperLeft = Sample(
                        image, subX, subY, subWidth, height);
                    Rgba upperRight = Sample(
                        image, subX + 1, subY, subWidth, height);
                    Rgba lowerLeft = Sample(
                        image, subX, subY + 1, subWidth, height);
                    Rgba lowerRight = Sample(
                        image, subX + 1, subY + 1, subWidth, height);

                    builder.Append(RenderQuadrant(
                        upperLeft,
                        upperRight,
                        lowerLeft,
                        lowerRight,
                        backgroundKey));
                }

                lines.Add(builder.ToString());
            }

            return new AvatarArt(lines);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string RenderQuadrant(
        Rgba upperLeft,
        Rgba upperRight,
        Rgba lowerLeft,
        Rgba lowerRight,
        Rgb? backgroundKey)
    {
        Rgb[] colors =
        [
            Blend(upperLeft),
            Blend(upperRight),
            Blend(lowerLeft),
            Blend(lowerRight)
        ];

        int first = 0;
        int second = 1;
        int bestDistance = -1;

        for (int i = 0; i < colors.Length; i++)
        {
            for (int j = i + 1; j < colors.Length; j++)
            {
                int distance = DistanceSquared(colors[i], colors[j]);

                if (distance > bestDistance)
                {
                    bestDistance = distance;
                    first = i;
                    second = j;
                }
            }
        }

        Rgb foregroundSeed = colors[first];
        Rgb backgroundSeed = colors[second];
        int mask = 0;
        int foreR = 0, foreG = 0, foreB = 0, foreCount = 0;
        int backR = 0, backG = 0, backB = 0, backCount = 0;

        for (int i = 0; i < colors.Length; i++)
        {
            Rgb color = colors[i];

            if (DistanceSquared(color, foregroundSeed) <=
                DistanceSquared(color, backgroundSeed))
            {
                mask |= 1 << i;
                foreR += color.R;
                foreG += color.G;
                foreB += color.B;
                foreCount++;
            }
            else
            {
                backR += color.R;
                backG += color.G;
                backB += color.B;
                backCount++;
            }
        }

        Rgb foreground = foreCount > 0
            ? new Rgb(
                (byte)(foreR / foreCount),
                (byte)(foreG / foreCount),
                (byte)(foreB / foreCount))
            : foregroundSeed;
        Rgb background = backCount > 0
            ? new Rgb(
                (byte)(backR / backCount),
                (byte)(backG / backCount),
                (byte)(backB / backCount))
            : backgroundSeed;

        if (backgroundKey is { } key)
        {
            bool backgroundIsKey =
                DistanceSquared(background, key) <= BackgroundKeyTolerance;
            bool foregroundIsKey =
                DistanceSquared(foreground, key) <= BackgroundKeyTolerance;

            if (backgroundIsKey && foregroundIsKey)
            {
                return " ";
            }

            if (foregroundIsKey)
            {
                (foreground, background) = (background, foreground);
                mask ^= 0xF;
            }

            if (backgroundIsKey)
            {
                return mask == 0
                    ? " "
                    : $"[{ToHex(foreground)}]{QuadrantGlyph(mask)}[/]";
            }
        }

        if (mask == 0)
        {
            return $"[on {ToHex(background)}] [/]";
        }

        return $"[{ToHex(foreground)} on {ToHex(background)}]" +
            $"{QuadrantGlyph(mask)}[/]";
    }

    private static Rgb? ReadCornerColor(ImageResult image)
    {
        if (image.Data.Length < 4)
        {
            return null;
        }

        Rgb color = Blend(new Rgba(
            image.Data[0],
            image.Data[1],
            image.Data[2],
            image.Data[3]));

        return color.R + color.G + color.B <= 240 ? color : null;
    }

    private static char QuadrantGlyph(int mask)
    {
        return mask switch
        {
            1 => '\u2598',
            2 => '\u259D',
            3 => '\u2580',
            4 => '\u2596',
            5 => '\u258C',
            6 => '\u259E',
            7 => '\u259B',
            8 => '\u2597',
            9 => '\u259A',
            10 => '\u2590',
            11 => '\u259C',
            12 => '\u2584',
            13 => '\u2599',
            14 => '\u259F',
            _ => '\u2588'
        };
    }

    private static Rgba Sample(
        ImageResult image,
        int targetX,
        int targetY,
        int targetWidth,
        int targetHeight)
    {
        long x0 = (long)targetX * image.Width / targetWidth;
        long x1 = Math.Max(
            x0 + 1, (long)(targetX + 1) * image.Width / targetWidth);
        long y0 = (long)targetY * image.Height / targetHeight;
        long y1 = Math.Max(
            y0 + 1, (long)(targetY + 1) * image.Height / targetHeight);

        x0 = Math.Clamp(x0, 0, image.Width - 1);
        x1 = Math.Clamp(x1, x0 + 1, image.Width);
        y0 = Math.Clamp(y0, 0, image.Height - 1);
        y1 = Math.Clamp(y1, y0 + 1, image.Height);

        long red = 0;
        long green = 0;
        long blue = 0;
        long alpha = 0;
        long count = 0;
        byte[] pixels = image.Data;

        for (long y = y0; y < y1; y++)
        {
            long row = y * image.Width * 4;

            for (long x = x0; x < x1; x++)
            {
                long index = row + x * 4;
                red += pixels[index];
                green += pixels[index + 1];
                blue += pixels[index + 2];
                alpha += pixels[index + 3];
                count++;
            }
        }

        if (count == 0)
        {
            return new Rgba(0, 0, 0, 0);
        }

        return new Rgba(
            (byte)(red / count),
            (byte)(green / count),
            (byte)(blue / count),
            (byte)(alpha / count));
    }

    private static Rgb Blend(Rgba color)
    {
        byte alpha = color.A;
        byte r = (byte)((color.R * alpha + BackdropR * (255 - alpha)) / 255);
        byte g = (byte)((color.G * alpha + BackdropG * (255 - alpha)) / 255);
        byte b = (byte)((color.B * alpha + BackdropB * (255 - alpha)) / 255);

        return new Rgb(r, g, b);
    }

    private static int DistanceSquared(Rgb left, Rgb right)
    {
        int r = left.R - right.R;
        int g = left.G - right.G;
        int b = left.B - right.B;

        return r * r + g * g + b * b;
    }

    private static string ToHex(Rgb color) =>
        $"#{color.R:x2}{color.G:x2}{color.B:x2}";

    private readonly record struct Rgba(byte R, byte G, byte B, byte A);

    private readonly record struct Rgb(byte R, byte G, byte B);
}
