using System.Reflection;
using System.Text;
using Qwek.Api;
using Qwek.Auth;
using Qwek.Config;
using Qwek.Tui;
using Spectre.Console;

Console.OutputEncoding = Encoding.UTF8;

try
{
    Console.Title = "Qwek";
}
catch (IOException)
{
}

AppOptions options;

try
{
    options = ParseOptions(args);
}
catch (ArgumentException ex)
{
    AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
    return 1;
}

if (options.Help)
{
    PrintHelp();
    return 0;
}

if (options.Logout)
{
    return AuthFlow.Logout(options.Profile);
}

string baseUrl;

try
{
    baseUrl = ResolveProfileBaseUrl(options);
}
catch (Exception ex) when (
    ex is ArgumentException or InvalidDataException or IOException or UnauthorizedAccessException)
{
    AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
    return 1;
}

if (Console.IsInputRedirected || Console.IsOutputRedirected)
{
    Console.Error.WriteLine(
        "Qwek must be run from an interactive terminal " +
        "(stdin or stdout is redirected).");
    return 1;
}

using ApiClient api = new(baseUrl);
LoginResultDto? session = await AuthFlow.RunAsync(api, options.Profile);

if (session is null)
{
    return 0;
}

await using TuiApp app = new(api, session);
await app.RunAsync();
return 0;

static AppOptions ParseOptions(string[] args)
{
    string? url = null;
    bool urlSpecified = false;
    string? profile = null;
    bool logout = false;
    bool help = false;

    for (int i = 0; i < args.Length; i++)
    {
        string argument = args[i];

        if (argument is "--url" or "-u")
        {
            if (i + 1 >= args.Length)
            {
                throw new ArgumentException($"Missing value for {argument}.");
            }

            url = args[++i];
            urlSpecified = true;
        }
        else if (argument.StartsWith("--url=", StringComparison.OrdinalIgnoreCase))
        {
            url = argument["--url=".Length..];
            urlSpecified = true;
        }
        else if (argument is "--profile" or "-p")
        {
            if (i + 1 >= args.Length)
            {
                throw new ArgumentException($"Missing value for {argument}.");
            }

            profile = ProfileStore.ValidateName(args[++i]);
        }
        else if (argument.StartsWith("--profile=", StringComparison.OrdinalIgnoreCase))
        {
            profile = ProfileStore.ValidateName(argument["--profile=".Length..]);
        }
        else if (argument is "--logout")
        {
            logout = true;
        }
        else if (argument is "--help" or "-h" ||
                 string.Equals(argument, "help", StringComparison.OrdinalIgnoreCase))
        {
            help = true;
        }
        else if (argument.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                 argument.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            url = argument;
            urlSpecified = true;
        }
    }

    if (url is null)
    {
        string? environmentUrl = Environment.GetEnvironmentVariable("QWEK_URL");

        if (!string.IsNullOrWhiteSpace(environmentUrl))
        {
            url = environmentUrl;
            urlSpecified = true;
        }
    }

    url ??= ReadDefaultBackendUrl();

    if (string.IsNullOrWhiteSpace(url))
    {
        throw new ArgumentException(
            "No backend URL configured. Use --url or set QWEK_URL.");
    }

    if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed) ||
        (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
    {
        throw new ArgumentException($"Invalid backend URL: {url}");
    }

    return new AppOptions(
        url.TrimEnd('/'),
        urlSpecified,
        profile,
        logout,
        help);
}

static string? ReadDefaultBackendUrl()
{
    string? value = Assembly.GetExecutingAssembly()
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(attribute =>
            string.Equals(attribute.Key, "DefaultBackendUrl", StringComparison.Ordinal))
        ?.Value;

    return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

static string ResolveProfileBaseUrl(AppOptions options)
{
    if (options.Profile is null)
    {
        return options.BaseUrl;
    }

    SessionProfile? profile = ProfileStore.Load(options.Profile);

    if (profile is null)
    {
        return options.BaseUrl;
    }

    if (options.BaseUrlSpecified &&
        !ProfileStore.SameBaseUrl(profile.BaseUrl, options.BaseUrl))
    {
        throw new ArgumentException(
            $"Profile '{options.Profile}' is saved for {profile.BaseUrl}, " +
            $"but you specified {options.BaseUrl}. " +
            $"Use --url {profile.BaseUrl} or choose another profile.");
    }

    return ProfileStore.NormalizeBaseUrl(profile.BaseUrl);
}

static void PrintHelp()
{
    Branding.RenderBanner("Realtime server chat in your terminal.");
    AnsiConsole.MarkupLine("[bold]Usage[/]");
    AnsiConsole.MarkupLine("  [blue]qwek [grey][[options]][/][/]");
    AnsiConsole.MarkupLine("  [blue]dotnet run -- [grey][[options]][/][/]");
    AnsiConsole.WriteLine();
    AnsiConsole.MarkupLine("[bold]Options[/]");
    AnsiConsole.MarkupLine("  [blue]-u, --url <url>[/]       Backend base URL");
    AnsiConsole.MarkupLine("  [blue]-p, --profile <name>[/]  Use or create a saved account profile");
    AnsiConsole.MarkupLine("  [blue]    --logout[/]          Forget a saved account profile");
    AnsiConsole.MarkupLine("  [blue]-h, --help[/]            Show this help");
    AnsiConsole.WriteLine();
    AnsiConsole.MarkupLine("[bold]In-app help[/]");
    AnsiConsole.MarkupLine("  [grey]Press ? or F1 in the TUI for all keybindings[/]");
    AnsiConsole.MarkupLine("  [grey]Type /help in the message box for commands[/]");
    AnsiConsole.WriteLine();
    AnsiConsole.MarkupLine("[bold]Environment[/]");
    AnsiConsole.MarkupLine("  [blue]QWEK_URL[/]            Backend base URL");
    AnsiConsole.MarkupLine("  [blue]QWEK_PROFILES_DIR[/]   Override the profile storage directory");
    AnsiConsole.WriteLine();
    AnsiConsole.MarkupLine("[bold]Profiles[/]");
    AnsiConsole.MarkupLine($"  [grey]{Markup.Escape(ProfileStore.ProfilesDirectory)}[/]");
    AnsiConsole.WriteLine();
    AnsiConsole.MarkupLine("[bold]Config[/]");
    AnsiConsole.MarkupLine($"  [grey]{Markup.Escape(AppConfig.ConfigPath)}[/]");
    AnsiConsole.MarkupLine("  [grey]sendKey: \"enter\" (default) or \"ctrl+enter\"[/]");
}

sealed record AppOptions(
    string BaseUrl,
    bool BaseUrlSpecified,
    string? Profile,
    bool Logout,
    bool Help);
