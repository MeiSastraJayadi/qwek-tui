using System.Reflection;
using Spectre.Console;

namespace RealTimeAppsTui.Tui;

internal static class Branding
{
    private const int LogoWidth = 40;
    private const int LogoMaxHeight = 12;

    private static readonly Color Accent = new(122, 162, 247);

    private static readonly Lazy<AvatarArt?> Logo = new(() =>
    {
        byte[]? data = LoadLogoBytes();
        return data is null
            ? null
            : AvatarArtFactory.Create(
                data,
                LogoWidth,
                LogoMaxHeight,
                transparentBackground: true);
    });

    public static void RenderBanner(string subtitle)
    {
        AvatarArt? logo = Logo.Value;

        if (logo is not null)
        {
            AnsiConsole.Write(
                new Markup(string.Join("\n", logo.Lines))
                {
                    Justification = Justify.Left
                });
        }
        else
        {
            AnsiConsole.Write(
                new FigletText("Qwek")
                    .Color(Accent)
                    .LeftJustified());
        }

        AnsiConsole.WriteLine();

        AnsiConsole.Write(
            new Markup($"[grey]{Markup.Escape(subtitle)}[/]")
            {
                Justification = Justify.Left
            });

        AnsiConsole.WriteLine();
    }

    private static byte[]? LoadLogoBytes()
    {
        try
        {
            Assembly assembly = typeof(Branding).Assembly;
            string? resourceName = assembly
                .GetManifestResourceNames()
                .FirstOrDefault(name => name.EndsWith(
                    "qwek-logo.jpeg",
                    StringComparison.OrdinalIgnoreCase));

            if (resourceName is null)
            {
                return null;
            }

            using Stream? stream = assembly.GetManifestResourceStream(resourceName);

            if (stream is null)
            {
                return null;
            }

            using MemoryStream buffer = new();
            stream.CopyTo(buffer);
            return buffer.ToArray();
        }
        catch (Exception)
        {
            return null;
        }
    }
}
