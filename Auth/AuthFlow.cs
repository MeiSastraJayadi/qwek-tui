using Qwek.Api;
using Qwek.Tui;
using Spectre.Console;

namespace Qwek.Auth;

public static class AuthFlow
{
    private const string NewAccountChoice = "Log in with another account";
    private const string QuitChoice = "Quit";
    private const string CancelChoice = "Cancel";

    private static readonly Color Accent = new(122, 162, 247);
    private static readonly Style Highlight = new(
        foreground: Color.Black,
        background: Accent);

    public static async Task<LoginResultDto?> RunAsync(
        ApiClient api,
        string? profileName = null)
    {
        LoginResultDto? session = null;

        if (profileName is not null)
        {
            SessionProfile? profile;

            try
            {
                profile = ProfileStore.Load(profileName);
            }
            catch (Exception ex) when (
                ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                ShowFatal(ex.Message);
                return null;
            }

            if (profile is not null)
            {
                if (!ProfileStore.SameBaseUrl(profile.BaseUrl, api.BaseUrl))
                {
                    ShowFatal(
                        $"Profile '{profileName}' is saved for {profile.BaseUrl}, " +
                        $"but you are connecting to {api.BaseUrl}.");
                    return null;
                }

                session = await TryResumeAsync(api, profile);
            }
        }
        else
        {
            IReadOnlyList<SessionProfile> profiles =
                ProfileStore.ListFor(api.BaseUrl);

            if (profiles.Count > 0)
            {
                session = await PickAccountAsync(api, profiles);
            }
        }

        session ??= await ShowMainMenuAsync(api);

        if (session is null)
        {
            return null;
        }

        try
        {
            SessionProfile saved = ProfileStore.Save(
                session,
                api.BaseUrl,
                profileName);

            string displayName = string.IsNullOrWhiteSpace(session.User.DisplayName)
                ? session.User.UserName
                : session.User.DisplayName;

            AnsiConsole.MarkupLine(
                $"[green]Signed in as[/] [bold]{Markup.Escape(displayName)}[/] " +
                $"[grey](@{Markup.Escape(session.User.UserName)})[/]");
            AnsiConsole.MarkupLine(
                $"[grey]Profile:[/] [blue]{Markup.Escape(saved.Name)}[/]");
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            AnsiConsole.MarkupLine(
                $"[yellow]Warning:[/] could not save profile: {Markup.Escape(ex.Message)}");
        }

        await Task.Delay(500);
        return session;
    }

    public static int Logout(string? profileName)
    {
        try
        {
            if (profileName is not null)
            {
                if (!ProfileStore.Delete(profileName))
                {
                    AnsiConsole.MarkupLine(
                        $"[yellow]No saved profile named[/] {Markup.Escape(profileName)}.");
                    return 1;
                }

                AnsiConsole.MarkupLine(
                    $"[green]Removed profile[/] {Markup.Escape(profileName)}.");
                return 0;
            }

            if (Console.IsInputRedirected || Console.IsOutputRedirected)
            {
                AnsiConsole.MarkupLine(
                    "[red]Specify --profile <name> when input or output is redirected.[/]");
                return 1;
            }

            IReadOnlyList<SessionProfile> profiles = ProfileStore.List();

            if (profiles.Count == 0)
            {
                AnsiConsole.MarkupLine("[yellow]No saved profiles.[/]");
                return 0;
            }

            List<string> labels = profiles
                .Select(FormatLogoutLabel)
                .ToList();
            labels.Add(CancelChoice);

            string choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[bold]Remove which saved profile?[/]")
                    .PageSize(10)
                    .HighlightStyle(Highlight)
                    .AddChoices(labels));

            if (choice == CancelChoice)
            {
                return 0;
            }

            int index = labels.IndexOf(choice);
            if (index < 0 || index >= profiles.Count)
            {
                return 0;
            }

            ProfileStore.Delete(profiles[index].Name);
            AnsiConsole.MarkupLine(
                $"[green]Removed profile[/] {Markup.Escape(profiles[index].Name)}.");
            return 0;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
            return 1;
        }
    }

    private static async Task<LoginResultDto?> PickAccountAsync(
        ApiClient api,
        IReadOnlyList<SessionProfile> initialProfiles)
    {
        IReadOnlyList<SessionProfile> profiles = initialProfiles;

        while (true)
        {
            AnsiConsole.Clear();
            RenderBanner();

            List<string> labels = profiles
                .Select(FormatProfileLabel)
                .ToList();
            labels.Add(NewAccountChoice);
            labels.Add(QuitChoice);

            string choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[bold]Choose an account[/]")
                    .PageSize(10)
                    .HighlightStyle(Highlight)
                    .AddChoices(labels));

            if (choice == QuitChoice)
            {
                return null;
            }

            if (choice == NewAccountChoice)
            {
                return await ShowMainMenuAsync(api);
            }

            int index = labels.IndexOf(choice);
            if (index < 0 || index >= profiles.Count)
            {
                continue;
            }

            LoginResultDto? session = await TryResumeAsync(api, profiles[index]);
            if (session is not null)
            {
                return session;
            }

            profiles = ProfileStore.ListFor(api.BaseUrl);

            if (profiles.Count == 0)
            {
                return await ShowMainMenuAsync(api);
            }
        }
    }

    private static async Task<LoginResultDto?> TryResumeAsync(
        ApiClient api,
        SessionProfile profile)
    {
        api.AccessToken = profile.AccessToken;

        try
        {
            UserProfileDto user = await api.GetProfileAsync();

            return new LoginResultDto(
                profile.AccessToken,
                new AuthenticatedUserDto(
                    user.UserId,
                    user.UserName,
                    user.DisplayName));
        }
        catch (ApiException ex) when (ex.StatusCode == 401)
        {
            api.AccessToken = null;
            ProfileStore.Delete(profile.Name);

            ShowFatal(
                $"Saved session for @{profile.UserName} has expired. " +
                "Please log in again.");
            return null;
        }
        catch (Exception ex)
        {
            api.AccessToken = null;

            ShowFatal($"Could not resume @{profile.UserName}: {ex.Message}");
            return null;
        }
    }

    private static async Task<LoginResultDto?> ShowMainMenuAsync(ApiClient api)
    {
        while (true)
        {
            AnsiConsole.Clear();
            RenderBanner();

            string choice = AnsiConsole.Prompt(
                new SelectionPrompt<string>()
                    .Title("[bold]What do you want to do?[/]")
                    .PageSize(5)
                    .HighlightStyle(Highlight)
                    .AddChoices("Login", "Register", "Quit"));

            LoginResultDto? session = choice switch
            {
                "Login" => await LoginAsync(api),
                "Register" => await RegisterAsync(api),
                _ => null
            };

            if (session is not null)
            {
                return session;
            }

            if (choice == "Quit")
            {
                return null;
            }
        }
    }

    private static async Task<LoginResultDto?> LoginAsync(ApiClient api)
    {
        AnsiConsole.WriteLine();

        string username = AnsiConsole.Prompt(
            new TextPrompt<string>("[bold]Username or email:[/]")
                .PromptStyle(Accent)
                .Validate(value => string.IsNullOrWhiteSpace(value)
                    ? ValidationResult.Error("Username is required.")
                    : ValidationResult.Success()));

        string password = AnsiConsole.Prompt(
            new TextPrompt<string>("[bold]Password:[/]")
                .PromptStyle(Accent)
                .Secret());

        try
        {
            LoginResultDto result = await api.LoginAsync(username.Trim(), password);
            api.AccessToken = result.AccessToken;
            return result;
        }
        catch (ApiException ex)
        {
            ShowError(ex.Message);
        }
        catch (Exception ex)
        {
            ShowError($"Login failed: {ex.Message}");
        }

        WaitForKey();
        return null;
    }

    private static async Task<LoginResultDto?> RegisterAsync(ApiClient api)
    {
        AnsiConsole.WriteLine();

        string username = AnsiConsole.Prompt(
            new TextPrompt<string>("[bold]Username:[/]")
                .PromptStyle(Accent)
                .Validate(value => string.IsNullOrWhiteSpace(value)
                    ? ValidationResult.Error("Username is required.")
                    : ValidationResult.Success()));

        string displayName = AnsiConsole.Prompt(
            new TextPrompt<string>("[bold]Display name[/] [grey](optional):[/]")
                .PromptStyle(Accent)
                .AllowEmpty());

        string email = AnsiConsole.Prompt(
            new TextPrompt<string>("[bold]Email:[/]")
                .PromptStyle(Accent)
                .Validate(value => value.Contains('@')
                    ? ValidationResult.Success()
                    : ValidationResult.Error("Enter a valid email address.")));

        string password = AnsiConsole.Prompt(
            new TextPrompt<string>("[bold]Password[/] [grey](min. 8 characters):[/]")
                .PromptStyle(Accent)
                .Secret()
                .Validate(value => value.Length >= 8
                    ? ValidationResult.Success()
                    : ValidationResult.Error("Password must be at least 8 characters.")));

        string confirm = AnsiConsole.Prompt(
            new TextPrompt<string>("[bold]Confirm password:[/]")
                .PromptStyle(Accent)
                .Secret()
                .Validate(value => value == password
                    ? ValidationResult.Success()
                    : ValidationResult.Error("Passwords do not match.")));

        try
        {
            RegisterResultDto result = await api.RegisterAsync(
                username.Trim(),
                string.IsNullOrWhiteSpace(displayName) ? username.Trim() : displayName.Trim(),
                email.Trim(),
                password);

            if (!result.Succeeded)
            {
                ShowError(result.Errors is { Count: > 0 }
                    ? string.Join("\n", result.Errors)
                    : "Registration failed.");
                WaitForKey();
                return null;
            }

            AnsiConsole.MarkupLine("[green]Account created.[/] Signing in...");

            LoginResultDto login = await api.LoginAsync(username.Trim(), password);
            api.AccessToken = login.AccessToken;
            return login;
        }
        catch (ApiException ex)
        {
            ShowError(ex.Message);
        }
        catch (Exception ex)
        {
            ShowError($"Registration failed: {ex.Message}");
        }

        WaitForKey();
        return null;
    }

    private static string FormatProfileLabel(SessionProfile profile) =>
        $"{Markup.Escape(DisplayNameOf(profile))} " +
        $"[grey](@{Markup.Escape(profile.UserName)})[/] " +
        $"[blue]{Markup.Escape(profile.Name)}[/]";

    private static string FormatLogoutLabel(SessionProfile profile) =>
        $"{Markup.Escape(DisplayNameOf(profile))} " +
        $"[grey](@{Markup.Escape(profile.UserName)})[/] " +
        $"[blue]{Markup.Escape(profile.Name)}[/] " +
        $"[grey]{Markup.Escape(profile.BaseUrl)}[/]";

    private static string DisplayNameOf(SessionProfile profile) =>
        string.IsNullOrWhiteSpace(profile.DisplayName)
            ? profile.UserName
            : profile.DisplayName;

    private static void ShowFatal(string message)
    {
        AnsiConsole.Clear();
        RenderBanner();
        ShowError(message);
        WaitForKey();
    }

    private static void RenderBanner() =>
        Branding.RenderBanner(
            "A realtime chat client for the terminal");

    private static void ShowError(string message)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(
            new Panel(new Markup($"[red]{Markup.Escape(message)}[/]"))
            {
                Border = BoxBorder.Rounded,
                BorderStyle = new Style(foreground: Color.Red),
                Padding = new Padding(1, 0, 1, 0)
            });
    }

    private static void WaitForKey()
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[grey]Press any key to continue...[/]");
        Console.ReadKey(true);
    }
}
