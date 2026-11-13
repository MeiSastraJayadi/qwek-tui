namespace Qwek.Api;

public sealed record AuthenticatedUserDto(
    string UserId,
    string UserName,
    string DisplayName);

public sealed record LoginResultDto(
    string AccessToken,
    AuthenticatedUserDto User);

public sealed record RegisterResultDto(
    bool Succeeded,
    string? UserId,
    IReadOnlyList<string>? Errors);

public sealed record ServerDto(
    Guid Id,
    string Name,
    string? AvatarUrl,
    string OwnerId,
    DateTime CreatedAt);

public sealed record ChannelDto(
    int Id,
    string Name,
    string? Topic,
    Guid ServerId,
    DateTime CreatedAt);

public sealed record ReplyPreviewDto(
    int MessageId,
    string UserId,
    string DisplayName,
    string Content);

public sealed record ReactionDto(
    string Emoji,
    int Count);

public sealed record ReactionResultDto(
    int MessageId,
    IReadOnlyList<ReactionDto> Reactions,
    IReadOnlyList<string> MyReactions);

public sealed record MessageDto(
    int Id,
    int ChannelId,
    string UserId,
    string DisplayName,
    string Content,
    DateTime CreatedAt,
    DateTime? EditedAt,
    string? AvatarUrl = null,
    string? AttachmentUrl = null,
    string? AttachmentName = null,
    string? AttachmentContentType = null,
    long? AttachmentSize = null,
    ReplyPreviewDto? ReplyTo = null,
    IReadOnlyList<ReactionDto>? Reactions = null,
    IReadOnlyList<string>? MyReactions = null);

public sealed record ServerMemberDto(
    string UserId,
    Guid ServerId,
    string UserName,
    string DisplayName,
    string? Email,
    DateTime JoinedAt);

public sealed record UserProfileDto(
    string UserId,
    string UserName,
    string DisplayName,
    string? Email,
    string? AvatarUrl);
