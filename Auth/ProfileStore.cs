using System.Text;
using System.Text.Json;
using Qwek.Api;

namespace Qwek.Auth;

public sealed record SessionProfile(
    string Name,
    string BaseUrl,
    string UserId,
    string UserName,
    string DisplayName,
    string AccessToken,
    DateTimeOffset SavedAt);

public static class ProfileStore
{
    private const int MaxNameLength = 64;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static string ProfilesDirectory { get; } = ResolveProfilesDirectory();

    public static IReadOnlyList<SessionProfile> List()
    {
        if (!Directory.Exists(ProfilesDirectory))
        {
            return [];
        }

        List<SessionProfile> profiles = [];
        List<string> files;

        try
        {
            files = Directory.EnumerateFiles(ProfilesDirectory, "*.json").ToList();
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        foreach (string path in files)
        {
            try
            {
                SessionProfile? profile = Read(path);
                if (profile is not null)
                {
                    profiles.Add(profile);
                }
            }
            catch (Exception ex) when (
                ex is IOException or UnauthorizedAccessException or JsonException)
            {
            }
        }

        return profiles
            .OrderByDescending(profile => profile.SavedAt)
            .ThenBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static IReadOnlyList<SessionProfile> ListFor(string baseUrl) =>
        List()
            .Where(profile => SameBaseUrl(profile.BaseUrl, baseUrl))
            .ToList();

    public static SessionProfile? Load(string name)
    {
        name = ValidateName(name);
        string path = PathFor(name);

        if (!File.Exists(path))
        {
            return null;
        }

        SessionProfile? profile;

        try
        {
            profile = Read(path);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                $"Profile '{name}' is not readable.",
                ex);
        }

        if (profile is null)
        {
            throw new InvalidDataException(
                $"Profile '{name}' is missing required fields.");
        }

        return string.Equals(profile.Name, name, FileNameComparison)
            ? profile
            : profile with { Name = name };
    }

    public static SessionProfile Save(
        LoginResultDto session,
        string baseUrl,
        string? preferredName)
    {
        if (string.IsNullOrWhiteSpace(session.AccessToken))
        {
            throw new InvalidOperationException(
                "Cannot save a profile without an access token.");
        }

        string normalizedBaseUrl = NormalizeBaseUrl(baseUrl);
        List<SessionProfile> existing = List().ToList();

        SessionProfile? identity = existing.FirstOrDefault(profile =>
            IsIdentity(profile, session, normalizedBaseUrl));

        string name;
        if (preferredName is not null)
        {
            name = ValidateName(preferredName);
        }
        else if (identity is not null)
        {
            name = identity.Name;
        }
        else
        {
            name = SanitizeName(session.User.UserName);
        }

        bool nameTakenByOther = existing.Any(profile =>
            NameEquals(profile.Name, name) &&
            !IsIdentity(profile, session, normalizedBaseUrl));

        if (nameTakenByOther)
        {
            name = FindAvailableName(name, existing, session, normalizedBaseUrl);
        }

        SessionProfile saved = new(
            name,
            normalizedBaseUrl,
            session.User.UserId,
            session.User.UserName,
            session.User.DisplayName,
            session.AccessToken,
            DateTimeOffset.UtcNow);

        Write(saved);

        if (identity is not null && !NameEquals(identity.Name, name))
        {
            Delete(identity.Name);
        }

        return saved;
    }

    public static bool Delete(string name)
    {
        name = ValidateName(name);
        string path = PathFor(name);

        if (!File.Exists(path))
        {
            return false;
        }

        File.Delete(path);
        return true;
    }

    public static string NormalizeBaseUrl(string baseUrl) =>
        baseUrl.Trim().TrimEnd('/');

    public static bool SameBaseUrl(string left, string right) =>
        string.Equals(
            NormalizeBaseUrl(left),
            NormalizeBaseUrl(right),
            StringComparison.OrdinalIgnoreCase);

    public static string ValidateName(string name)
    {
        string trimmed = (name ?? string.Empty).Trim();

        if (trimmed.Length == 0)
        {
            throw new ArgumentException("Profile name cannot be empty.");
        }

        if (trimmed.Length > MaxNameLength)
        {
            throw new ArgumentException(
                $"Profile name must be {MaxNameLength} characters or fewer.");
        }

        if (trimmed is "." or "..")
        {
            throw new ArgumentException("Invalid profile name.");
        }

        if (trimmed.Any(ch =>
                !(char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' or '@')))
        {
            throw new ArgumentException(
                "Profile names may contain letters, digits, '-', '_', '.', and '@'.");
        }

        return trimmed;
    }

    private static SessionProfile? Read(string path)
    {
        string payload = File.ReadAllText(path);
        SessionProfile? profile =
            JsonSerializer.Deserialize<SessionProfile>(payload, Json);

        if (profile is null ||
            string.IsNullOrWhiteSpace(profile.UserId) ||
            string.IsNullOrWhiteSpace(profile.UserName) ||
            string.IsNullOrWhiteSpace(profile.AccessToken) ||
            string.IsNullOrWhiteSpace(profile.BaseUrl))
        {
            return null;
        }

        return profile with { Name = Path.GetFileNameWithoutExtension(path) };
    }

    private static void Write(SessionProfile profile)
    {
        Directory.CreateDirectory(ProfilesDirectory);
        RestrictDirectory();

        string path = PathFor(profile.Name);
        string temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        string payload = JsonSerializer.Serialize(profile, Json);

        try
        {
            File.WriteAllText(temp, payload);
            RestrictFile(temp);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    private static string FindAvailableName(
        string baseName,
        List<SessionProfile> existing,
        LoginResultDto session,
        string baseUrl)
    {
        for (int suffix = 2; suffix < 1000; suffix++)
        {
            string candidate = AppendSuffix(baseName, suffix);
            SessionProfile? match = existing.FirstOrDefault(profile =>
                NameEquals(profile.Name, candidate));

            if (match is null || IsIdentity(match, session, baseUrl))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException(
            "Could not choose a unique profile name.");
    }

    private static string AppendSuffix(string name, int suffix)
    {
        string tail = "-" + suffix;
        int maxBaseLength = MaxNameLength - tail.Length;

        if (name.Length > maxBaseLength)
        {
            name = name[..maxBaseLength];
        }

        return name + tail;
    }

    private static string SanitizeName(string value)
    {
        StringBuilder builder = new();

        foreach (char ch in value.Trim())
        {
            if (char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.')
            {
                builder.Append(ch);
            }
            else if (char.IsWhiteSpace(ch) || ch is '@' or '+')
            {
                builder.Append('-');
            }
        }

        string name = builder.ToString().Trim('-', '.', '_');

        if (name.Length == 0)
        {
            name = "profile";
        }

        return name.Length > 48 ? name[..48] : name;
    }

    private static bool IsIdentity(
        SessionProfile profile,
        LoginResultDto session,
        string baseUrl) =>
        string.Equals(profile.UserId, session.User.UserId, StringComparison.Ordinal) &&
        SameBaseUrl(profile.BaseUrl, baseUrl);

    private static bool NameEquals(string left, string right) =>
        string.Equals(left, right, FileNameComparison);

    private static StringComparison FileNameComparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private static string PathFor(string name) =>
        Path.Combine(ProfilesDirectory, name + ".json");

    private static string ResolveProfilesDirectory()
    {
        string? overrideDirectory =
            Environment.GetEnvironmentVariable("QWEK_PROFILES_DIR");

        if (!string.IsNullOrWhiteSpace(overrideDirectory))
        {
            return Path.GetFullPath(overrideDirectory);
        }

        if (OperatingSystem.IsWindows())
        {
            string appData = Environment.GetFolderPath(
                Environment.SpecialFolder.ApplicationData);

            return Path.Combine(appData, "Qwek", "profiles");
        }

        string? xdgConfig =
            Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        string configDirectory = string.IsNullOrWhiteSpace(xdgConfig)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".config")
            : xdgConfig;

        return Path.Combine(configDirectory, "qwek", "profiles");
    }

    private static void RestrictDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(
                ProfilesDirectory,
                UnixFileMode.UserRead |
                UnixFileMode.UserWrite |
                UnixFileMode.UserExecute);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void RestrictFile(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
