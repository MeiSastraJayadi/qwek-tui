using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Qwek.Api;

public sealed class ApiClient : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _http;

    public string BaseUrl { get; }
    public string? AccessToken { get; set; }

    public ApiClient(string baseUrl)
    {
        BaseUrl = baseUrl.TrimEnd('/');
        _http = new HttpClient
        {
            BaseAddress = new Uri(BaseUrl + "/"),
            Timeout = TimeSpan.FromSeconds(20)
        };
    }

    public Task<LoginResultDto> LoginAsync(
        string usernameOrEmail,
        string password,
        CancellationToken ct = default)
    {
        return SendAuthRequestAsync<LoginResultDto>(
            "api/auth/login",
            new { usernameOrEmail, password },
            ct);
    }

    public async Task<RegisterResultDto> RegisterAsync(
        string username,
        string displayName,
        string email,
        string password,
        CancellationToken ct = default)
    {
        using HttpRequestMessage request = CreateRequest(
            HttpMethod.Post,
            "api/auth/register",
            new { username, displayName, email, password });

        using HttpResponseMessage response = await SendRawAsync(request, ct);
        string payload = await response.Content.ReadAsStringAsync(ct);
        RegisterResultDto? result = TryDeserialize<RegisterResultDto>(payload);

        if (result is not null &&
            (response.IsSuccessStatusCode || (int)response.StatusCode == 400))
        {
            return result;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw CreateException(response, payload);
        }

        throw new ApiException("The server returned an unexpected response.");
    }

    public async Task<List<ServerDto>> GetServersAsync(CancellationToken ct = default)
    {
        return await SendAsync<List<ServerDto>>(
            HttpMethod.Get, "api/servers", null, ct) ?? [];
    }

    public async Task<ServerDto> CreateServerAsync(
        string name,
        CancellationToken ct = default)
    {
        return await SendAsync<ServerDto>(
            HttpMethod.Post,
            "api/servers",
            new { name, avatarUrl = (string?)null },
            ct)
            ?? throw new ApiException("The server did not return the created server.");
    }

    public async Task JoinServerAsync(Guid serverId, CancellationToken ct = default)
    {
        await SendAsync<object>(
            HttpMethod.Post, $"api/servers/{serverId}/join", null, ct);
    }

    public async Task<List<ChannelDto>> GetChannelsAsync(
        Guid serverId,
        CancellationToken ct = default)
    {
        return await SendAsync<List<ChannelDto>>(
            HttpMethod.Get, $"api/servers/{serverId}/channels", null, ct) ?? [];
    }

    public async Task<ChannelDto> CreateChannelAsync(
        Guid serverId,
        string name,
        string? topic,
        CancellationToken ct = default)
    {
        return await SendAsync<ChannelDto>(
            HttpMethod.Post,
            $"api/servers/{serverId}/channels",
            new { name, topic },
            ct)
            ?? throw new ApiException("The server did not return the created channel.");
    }

    public async Task<List<MessageDto>> GetMessagesAsync(
        int channelId,
        int limit = 100,
        int? beforeId = null,
        CancellationToken ct = default)
    {
        string path = $"api/channels/{channelId}/messages?limit={limit}";
        if (beforeId is not null)
        {
            path += $"&beforeId={beforeId.Value}";
        }

        return await SendAsync<List<MessageDto>>(HttpMethod.Get, path, null, ct) ?? [];
    }

    public async Task<MessageDto> SendMessageAsync(
        int channelId,
        string content,
        int? replyToMessageId = null,
        CancellationToken ct = default)
    {
        return await SendAsync<MessageDto>(
            HttpMethod.Post,
            $"api/channels/{channelId}/messages",
            new { content, replyToMessageId },
            ct)
            ?? throw new ApiException("The server did not return the sent message.");
    }

    public async Task<ReactionResultDto> ToggleReactionAsync(
        int messageId,
        string emoji,
        CancellationToken ct = default)
    {
        return await SendAsync<ReactionResultDto>(
            HttpMethod.Post,
            $"api/messages/{messageId}/reactions",
            new { emoji },
            ct)
            ?? throw new ApiException("The server did not return the reaction.");
    }

    public async Task EditMessageAsync(
        int messageId,
        string content,
        CancellationToken ct = default)
    {
        await SendAsync<object>(
            HttpMethod.Patch,
            $"api/messages/{messageId}",
            new { content },
            ct);
    }

    public async Task DeleteMessageAsync(
        int messageId,
        CancellationToken ct = default)
    {
        await SendAsync<object>(
            HttpMethod.Delete,
            $"api/messages/{messageId}",
            null,
            ct);
    }

    public async Task<List<ServerMemberDto>> GetMembersAsync(
        Guid serverId,
        CancellationToken ct = default)
    {
        return await SendAsync<List<ServerMemberDto>>(
            HttpMethod.Get,
            $"api/servers/{serverId}/members",
            null,
            ct) ?? [];
    }

    public async Task<UserProfileDto> GetProfileAsync(CancellationToken ct = default)
    {
        return await SendAsync<UserProfileDto>(
            HttpMethod.Get, "api/users/me/", null, ct)
            ?? throw new ApiException("The server did not return your profile.");
    }

    public async Task<UserProfileDto> UpdateDisplayNameAsync(
        string displayName,
        CancellationToken ct = default)
    {
        return await SendAsync<UserProfileDto>(
            HttpMethod.Patch,
            "api/users/me/display-name",
            new { displayName },
            ct)
            ?? throw new ApiException("The server did not return your profile.");
    }

    public async Task<UserProfileDto> UpdateUsernameAsync(
        string username,
        CancellationToken ct = default)
    {
        return await SendAsync<UserProfileDto>(
            HttpMethod.Patch,
            "api/users/me/username",
            new { username },
            ct)
            ?? throw new ApiException("The server did not return your profile.");
    }

    public async Task<UserProfileDto> UpdateAvatarAsync(
        string? avatarUrl,
        CancellationToken ct = default)
    {
        return await SendAsync<UserProfileDto>(
            HttpMethod.Patch,
            "api/users/me/avatar",
            new { avatarUrl },
            ct)
            ?? throw new ApiException("The server did not return your profile.");
    }

    public async Task<UserProfileDto> UploadAvatarAsync(
        string filePath,
        CancellationToken ct = default)
    {
        if (!File.Exists(filePath))
        {
            throw new ApiException("The selected avatar file no longer exists.");
        }

        using FileStream stream = File.OpenRead(filePath);
        using MultipartFormDataContent form = new();
        using StreamContent content = new(stream);
        content.Headers.ContentType =
            new MediaTypeHeaderValue(GetImageContentType(filePath));
        form.Add(content, "file", Path.GetFileName(filePath));

        using HttpRequestMessage request = CreateRequest(
            HttpMethod.Post, "api/users/me/avatar/upload", null);
        request.Content = form;

        return await SendRequestAsync<UserProfileDto>(request, ct)
            ?? throw new ApiException("The server did not return your profile.");
    }

    public async Task<MessageDto> SendAttachmentAsync(
        int channelId,
        string filePath,
        string content,
        CancellationToken ct = default)
    {
        if (!File.Exists(filePath))
        {
            throw new ApiException("The selected file no longer exists.");
        }

        using FileStream stream = File.OpenRead(filePath);
        using MultipartFormDataContent form = new();
        using StreamContent fileContent = new(stream);
        fileContent.Headers.ContentType =
            new MediaTypeHeaderValue("application/octet-stream");
        form.Add(fileContent, "file", Path.GetFileName(filePath));
        form.Add(new StringContent(content ?? string.Empty), "content");

        using HttpRequestMessage request = CreateRequest(
            HttpMethod.Post,
            $"api/channels/{channelId}/messages/upload",
            null);
        request.Content = form;

        return await SendRequestAsync<MessageDto>(request, ct)
            ?? throw new ApiException("The server did not return the sent message.");
    }

    public async Task<bool> DownloadAsync(
        string url,
        string destinationPath,
        CancellationToken ct = default)
    {
        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, url);

            if (!string.IsNullOrWhiteSpace(AccessToken))
            {
                request.Headers.Authorization =
                    new AuthenticationHeaderValue("Bearer", AccessToken);
            }

            using HttpResponseMessage response = await _http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, ct);

            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            string? directory = Path.GetDirectoryName(destinationPath);

            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await using Stream source =
                await response.Content.ReadAsStreamAsync(ct);
            await using FileStream target = File.Create(destinationPath);
            await source.CopyToAsync(target, ct);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public async Task<byte[]?> GetImageAsync(
        string url,
        CancellationToken ct = default)
    {
        try
        {
            using HttpRequestMessage request = new(HttpMethod.Get, url);

            if (!string.IsNullOrWhiteSpace(AccessToken))
            {
                request.Headers.Authorization =
                    new AuthenticationHeaderValue("Bearer", AccessToken);
            }

            using HttpResponseMessage response = await _http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, ct);

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            if (response.Content.Headers.ContentLength > 10 * 1024 * 1024)
            {
                return null;
            }

            return await response.Content.ReadAsByteArrayAsync(ct);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string GetImageContentType(string filePath)
    {
        return Path.GetExtension(filePath).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".jpg" or ".jpeg" => "image/jpeg",
            _ => "application/octet-stream"
        };
    }

    private async Task<T> SendAuthRequestAsync<T>(
        string path,
        object body,
        CancellationToken ct)
    {
        using HttpRequestMessage request = CreateRequest(HttpMethod.Post, path, body);
        using HttpResponseMessage response = await SendRawAsync(request, ct);
        string payload = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            throw CreateException(response, payload);
        }

        T? result = TryDeserialize<T>(payload);
        return result ?? throw new ApiException(
            "The server returned an unexpected response.",
            (int)response.StatusCode);
    }

    private async Task<T?> SendAsync<T>(
        HttpMethod method,
        string path,
        object? body,
        CancellationToken ct)
    {
        using HttpRequestMessage request = CreateRequest(method, path, body);
        return await SendRequestAsync<T>(request, ct);
    }

    private async Task<T?> SendRequestAsync<T>(
        HttpRequestMessage request,
        CancellationToken ct)
    {
        using HttpResponseMessage response = await SendRawAsync(request, ct);
        string payload = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
        {
            throw CreateException(response, payload);
        }

        if (string.IsNullOrWhiteSpace(payload))
        {
            return default;
        }

        T? result;
        try
        {
            result = JsonSerializer.Deserialize<T>(payload, Json);
        }
        catch (JsonException ex)
        {
            throw new ApiException(
                "The server returned an unexpected response.",
                (int)response.StatusCode,
                ex);
        }

        return result;
    }

    private HttpRequestMessage CreateRequest(
        HttpMethod method,
        string path,
        object? body)
    {
        HttpRequestMessage request = new(method, path);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        if (!string.IsNullOrWhiteSpace(AccessToken))
        {
            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", AccessToken);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: Json);
        }

        return request;
    }

    private async Task<HttpResponseMessage> SendRawAsync(
        HttpRequestMessage request,
        CancellationToken ct)
    {
        try
        {
            return await _http.SendAsync(request, ct);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new ApiException("The request timed out.", inner: ex);
        }
        catch (HttpRequestException ex)
        {
            throw new ApiException(
                $"Cannot reach {BaseUrl}. Is the backend running?",
                inner: ex);
        }
    }

    private static T? TryDeserialize<T>(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return default;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(payload, Json);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private static ApiException CreateException(
        HttpResponseMessage response,
        string payload)
    {
        int status = (int)response.StatusCode;

        if (status == 401)
        {
            return new ApiException("Invalid username or password.", status);
        }

        string? detail = null;

        try
        {
            using JsonDocument document = JsonDocument.Parse(payload);
            JsonElement root = document.RootElement;

            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("detail", out JsonElement detailElement) &&
                    detailElement.ValueKind == JsonValueKind.String)
                {
                    detail = detailElement.GetString();
                }
                else if (root.TryGetProperty("title", out JsonElement titleElement) &&
                         titleElement.ValueKind == JsonValueKind.String)
                {
                    detail = titleElement.GetString();
                }
                else if (root.TryGetProperty("errors", out JsonElement errors) &&
                         errors.ValueKind == JsonValueKind.Array)
                {
                    detail = string.Join(
                        " ",
                        errors.EnumerateArray()
                            .Select(error => error.GetString())
                            .Where(error => !string.IsNullOrWhiteSpace(error)));
                }
            }
        }
        catch (JsonException)
        {
        }

        if (string.IsNullOrWhiteSpace(detail))
        {
            detail = string.IsNullOrWhiteSpace(payload)
                ? response.ReasonPhrase
                : payload.Trim();
        }

        if (detail is not null && detail.Length > 300)
        {
            detail = detail[..300] + "...";
        }

        return new ApiException(detail ?? $"Request failed ({status}).", status);
    }

    public void Dispose() => _http.Dispose();
}
