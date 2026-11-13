using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Microsoft.AspNetCore.SignalR.Client;
using RealTimeAppsTui.Api;
using RealTimeAppsTui.Config;
using Spectre.Console;

namespace RealTimeAppsTui.Tui;

public sealed partial class TuiApp : IAsyncDisposable
{
    private const int MaxInputLength = 2000;

    private enum Pane
    {
        Servers,
        Channels,
        Messages,
        Input
    }

    private enum FilePickerKind
    {
        Avatar,
        Attachment
    }

    private enum ModalKind
    {
        CreateServer,
        CreateChannel,
        JoinServer,
        EditMessage,
        ConfirmDelete,
        EditDisplayName,
        EditUsername,
        SendAttachment
    }

    private enum QuickSwitchKind
    {
        Server,
        Channel
    }

    private enum EmojiPickerMode
    {
        Insert,
        Reaction
    }

    private sealed class ModalState
    {
        public ModalKind Kind { get; init; }
        public string Title { get; init; } = "";
        public string Hint { get; init; } = "";
        public string Buffer { get; set; } = "";
        public int MessageId { get; init; }
        public string AttachmentPath { get; init; } = "";
    }

    private sealed record MessageEvent(int ChannelId, int MessageId);

    private sealed record PresenceChangedEvent(string UserId, bool IsOnline);

    private sealed record UserTypingEvent(
        int ChannelId,
        string UserId,
        string DisplayName,
        bool IsTyping);

    private sealed record MessageReactionsEvent(
        int ChannelId,
        int MessageId,
        string UserId,
        string Emoji,
        bool IsAdded,
        List<ReactionDto> Reactions);

    private sealed record TypingUser(string DisplayName, DateTime ExpiresAt);

    private sealed record ChannelUnread(int Count, bool Mentioned);

    private sealed record UnreadMessageInfo(
        int Id,
        bool IsMine,
        bool MentionsMe,
        string Author);

    private sealed record UnreadChannelSnapshot(
        Guid ServerId,
        string ServerName,
        int ChannelId,
        string ChannelName,
        IReadOnlyList<UnreadMessageInfo> Messages);

    private sealed record UnreadServerChannels(
        Guid ServerId,
        IReadOnlyList<ChannelDto> Channels);

    private sealed record UnreadPollResult(
        IReadOnlyList<UnreadServerChannels> DiscoveredChannels,
        IReadOnlyList<UnreadChannelSnapshot> Snapshots);

    private sealed record SearchEntry(
        int MessageIndex,
        int Score,
        string Content,
        IReadOnlyList<int> Matches);

    private sealed record QuickSwitchEntry(
        QuickSwitchKind Kind,
        Guid ServerId,
        string ServerName,
        int ChannelId,
        string ChannelName,
        int Score,
        int Order);

    private static readonly JsonSerializerOptions HubJson =
        new(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true
        };

    private static readonly string[] AvatarFileExtensions =
        [".png", ".jpg", ".jpeg", ".gif"];

    private static readonly string[] AttachmentFileExtensions =
    [
        ".png", ".jpg", ".jpeg", ".gif", ".webp",
        ".pdf", ".txt", ".md", ".csv",
        ".mp4", ".webm", ".mov",
        ".mp3", ".wav", ".ogg",
        ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".zip"
    ];

    private static readonly string[] PreviewableImageExtensions =
        [".png", ".jpg", ".jpeg", ".gif"];

    private const int ChatAvatarWidth = 4;
    private const int ProfileAvatarWidth = 8;
    private const int AttachmentPreviewWidth = 8;
    private const int UnreadPageSize = 100;
    private const int MaxMentionSuggestions = 5;

    private static readonly TimeSpan UnreadPollInterval =
        TimeSpan.FromSeconds(10);

    private readonly ApiClient _api;
    private LoginResultDto _session;
    private readonly string _hubUrl;
    private readonly Channel<Func<Task>> _events =
        Channel.CreateUnbounded<Func<Task>>();

    private readonly List<ServerDto> _servers = [];
    private readonly List<ChannelDto> _channels = [];
    private readonly List<MessageDto> _messages = [];
    private readonly List<ServerMemberDto> _serverMembers = [];
    private readonly List<string> _sentHistory = [];
    private readonly Dictionary<int, ChannelUnread> _unreadByChannel = [];
    private readonly Dictionary<int, int> _lastReadByChannel = [];
    private readonly Dictionary<Guid, List<ChannelDto>> _channelsByServer = [];
    private readonly CancellationTokenSource _unreadPollCts = new();
    private volatile bool _unreadPollInFlight;
    private string _mentionUserName = "";
    private Regex? _mentionRegex;

    private HubConnection? _hub;
    private bool _live;
    private bool _quit;
    private bool _dirty = true;
    private int _renderedWidth;
    private int _renderedHeight;

    private int _serverIndex = -1;
    private int _channelIndex = -1;
    private int _messageIndex = -1;
    private int _viewStart;
    private int _lastBodyHeight = 10;
    private bool _followTail = true;
    private bool _ensureSelectionVisible;
    private int _joinedChannelId = -1;

    private Pane _focus = Pane.Channels;
    private string _input = "";
    private string _draft = "";
    private int _historyCursor = -1;
    private readonly Dictionary<int, string> _draftsByChannel = [];
    private readonly bool _sendOnCtrlEnter;
    private MessageDto? _replyTarget;
    private bool _showMentionSuggestions;
    private string _mentionQuery = "";
    private int _mentionStartIndex = -1;
    private int _mentionIndex;
    private bool _mentionDismissed;
    private readonly List<ServerMemberDto> _mentionResults = [];
    private readonly Dictionary<Guid, List<ServerMemberDto>> _membersByServer = [];
    private readonly HashSet<string> _onlineUserIds = [];
    private readonly Dictionary<string, TypingUser> _typingUsers = [];
    private bool _typingSent;
    private int _typingChannelId = -1;
    private DateTime _typingSentAt = DateTime.MinValue;
    private CancellationTokenSource? _typingStopCts;

    private string _status = "Starting...";
    private DateTime _statusUntil = DateTime.MinValue;
    private bool _statusIsError;

    private bool _help;
    private int _helpScroll;
    private int _helpMaxScroll;
    private int _helpPageHeight = 10;
    private bool _showSearch;
    private string _searchQuery = "";
    private readonly List<SearchEntry> _searchResults = [];
    private int _searchIndex;
    private bool _showQuickSwitch;
    private string _quickSwitchQuery = "";
    private readonly List<QuickSwitchEntry> _quickSwitchResults = [];
    private int _quickSwitchIndex;
    private bool _showEmojiPicker;
    private string _emojiQuery = "";
    private readonly List<EmojiEntry> _emojiResults = [];
    private int _emojiIndex;
    private EmojiPickerMode _emojiPickerMode = EmojiPickerMode.Insert;
    private int _reactionMessageId;
    private bool _showProfile;
    private UserProfileDto? _profile;
    private readonly Dictionary<string, AvatarArt?> _avatarCache = [];
    private readonly HashSet<string> _avatarRequests = [];
    private readonly Dictionary<string, string> _fallbackAvatarColors = [];
    private FilePickerKind _filePickerKind;
    private bool _showAvatarPicker;
    private string _avatarPickerDirectory = "";
    private readonly List<FileSystemInfo> _avatarPickerEntries = [];
    private int _avatarPickerIndex;
    private bool _avatarSearchActive;
    private string _avatarSearchQuery = "";
    private int _avatarSearchVersion;
    private bool _avatarSearchBusy;
    private CancellationTokenSource? _avatarSearchCts;
    private bool _showServerDetails;
    private int _serverDetailScroll;
    private int _serverDetailPageHeight = 10;
    private int _serverDetailMaxScroll;
    private ModalState? _modal;

    public TuiApp(ApiClient api, LoginResultDto session)
    {
        _api = api;
        _session = session;
        _hubUrl = api.BaseUrl + "/hubs/chat";

        AppConfig config = AppConfig.Load();
        _sendOnCtrlEnter = config.SendOnCtrlEnter;
    }

    private ServerDto? CurrentServer =>
        _serverIndex >= 0 && _serverIndex < _servers.Count
            ? _servers[_serverIndex]
            : null;

    private ChannelDto? CurrentChannel =>
        _channelIndex >= 0 && _channelIndex < _channels.Count
            ? _channels[_channelIndex]
            : null;

    private MessageDto? CurrentMessage =>
        _messageIndex >= 0 && _messageIndex < _messages.Count
            ? _messages[_messageIndex]
            : null;

    private bool HasOverlay =>
        _showQuickSwitch ||
        _showEmojiPicker ||
        _showSearch ||
        _showAvatarPicker ||
        _showProfile ||
        _showServerDetails ||
        _help;

    public async Task RunAsync()
    {
        AnsiConsole.Clear();
        AnsiConsole.MarkupLine("[grey]Connecting realtime...[/]");
        await ConnectHubAsync();
        await InitializeWorkspaceAsync();

        AnsiConsole.Cursor.Hide();
        AnsiConsole.Clear();

        try
        {
            LiveDisplay live = AnsiConsole.Live(new Markup(""))
                .AutoClear(false)
                .Overflow(VerticalOverflow.Crop)
                .Cropping(VerticalOverflowCropping.Bottom);

            await live.StartAsync(async context =>
            {
                DateTime lastSizeCheck = DateTime.MinValue;
                DateTime lastUnreadPoll = DateTime.MinValue;
                DateTime lastTypingCheck = DateTime.MinValue;

                while (!_quit)
                {
                    while (_events.Reader.TryRead(out Func<Task>? action))
                    {
                        await RunEventAsync(action);
                    }

                    if ((DateTime.UtcNow - lastSizeCheck).TotalMilliseconds > 500)
                    {
                        lastSizeCheck = DateTime.UtcNow;

                        if (GetConsoleWidth() != _renderedWidth ||
                            GetConsoleHeight() != _renderedHeight)
                        {
                            _dirty = true;
                        }
                    }

                    if (Console.KeyAvailable)
                    {
                        ConsoleKeyInfo key = Console.ReadKey(true);

                        try
                        {
                            await HandleKeyAsync(key);
                        }
                        catch (Exception ex)
                        {
                            ShowError(Friendly(ex));
                        }

                        _dirty = true;
                    }
                    else
                    {
                        await Task.Delay(15);
                    }

                    bool statusExpired = _statusUntil != DateTime.MinValue &&
                                         DateTime.UtcNow >= _statusUntil;

                    if (statusExpired)
                    {
                        _statusUntil = DateTime.MinValue;
                        _dirty = true;
                    }

                    if (!_unreadPollInFlight &&
                        DateTime.UtcNow - lastUnreadPoll >= UnreadPollInterval)
                    {
                        lastUnreadPoll = DateTime.UtcNow;
                        ScheduleUnreadPoll();
                    }

                    if (DateTime.UtcNow - lastTypingCheck >= TimeSpan.FromSeconds(1))
                    {
                        lastTypingCheck = DateTime.UtcNow;
                        ExpireTypingUsers();
                    }

                    if (_dirty)
                    {
                        context.UpdateTarget(Render());
                        context.Refresh();
                        _renderedWidth = GetConsoleWidth();
                        _renderedHeight = GetConsoleHeight();
                        _dirty = false;
                    }
                }
            });
        }
        finally
        {
            AnsiConsole.Cursor.Show();
            AnsiConsole.WriteLine();
        }
    }

    private async Task RunEventAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            ShowError(Friendly(ex));
        }

        _dirty = true;
    }

    private async Task InitializeWorkspaceAsync()
    {
        try
        {
            List<ServerDto> servers = await _api.GetServersAsync();
            _servers.Clear();
            _servers.AddRange(servers);

            if (_servers.Count > 0)
            {
                _serverIndex = 0;
                await LoadChannelsAsync();
                SetStatus(_live
                    ? "Ready. Pick a channel and say hello."
                    : "Backend connected, realtime offline.");
            }
            else
            {
                SetStatus("No servers yet. Press n to create one or j to join.");
            }
        }
        catch (Exception ex)
        {
            ShowError("Could not load servers: " + Friendly(ex));
        }
    }

    private void BuildHub()
    {
        _hub = new HubConnectionBuilder()
            .WithUrl(_hubUrl, options =>
            {
                options.AccessTokenProvider = () =>
                    Task.FromResult<string?>(_session.AccessToken);
            })
            .WithAutomaticReconnect()
            .Build();

        _hub.On<JsonElement>("ReceiveMessage", element =>
        {
            MessageDto? message = Deserialize<MessageDto>(element);
            if (message is not null)
            {
                _events.Writer.TryWrite(() =>
                {
                    ApplyIncomingMessage(message);
                    return Task.CompletedTask;
                });
            }
        });

        _hub.On<JsonElement>("MessageEdited", element =>
        {
            MessageEvent? messageEvent = Deserialize<MessageEvent>(element);
            if (messageEvent is not null)
            {
                _events.Writer.TryWrite(async () =>
                {
                    if (CurrentChannel?.Id == messageEvent.ChannelId)
                    {
                        await ReloadMessagesQuietAsync();
                    }
                });
            }
        });

        _hub.On<JsonElement>("MessageDeleted", element =>
        {
            MessageEvent? messageEvent = Deserialize<MessageEvent>(element);
            if (messageEvent is not null)
            {
                _events.Writer.TryWrite(() =>
                {
                    if (CurrentChannel?.Id == messageEvent.ChannelId)
                    {
                        ApplyDeletedMessage(messageEvent.MessageId);
                    }

                    return Task.CompletedTask;
                });
            }
        });

        _hub.On<JsonElement>("MessageReactionsChanged", element =>
        {
            MessageReactionsEvent? reactionEvent =
                Deserialize<MessageReactionsEvent>(element);

            if (reactionEvent is not null)
            {
                _events.Writer.TryWrite(() =>
                {
                    ApplyReactionEvent(reactionEvent);
                    return Task.CompletedTask;
                });
            }
        });

        _hub.On<JsonElement>("UserTyping", element =>
        {
            UserTypingEvent? typingEvent = Deserialize<UserTypingEvent>(element);

            if (typingEvent is not null)
            {
                _events.Writer.TryWrite(() =>
                {
                    ApplyTypingEvent(typingEvent);
                    return Task.CompletedTask;
                });
            }
        });

        _hub.On<JsonElement>("PresenceSnapshot", element =>
        {
            List<string>? userIds = Deserialize<List<string>>(element);

            if (userIds is not null)
            {
                _events.Writer.TryWrite(() =>
                {
                    _onlineUserIds.Clear();

                    foreach (string userId in userIds)
                    {
                        _onlineUserIds.Add(userId);
                    }

                    _dirty = true;
                    return Task.CompletedTask;
                });
            }
        });

        _hub.On<JsonElement>("PresenceChanged", element =>
        {
            PresenceChangedEvent? presenceEvent =
                Deserialize<PresenceChangedEvent>(element);

            if (presenceEvent is not null)
            {
                _events.Writer.TryWrite(() =>
                {
                    if (presenceEvent.IsOnline)
                    {
                        _onlineUserIds.Add(presenceEvent.UserId);
                    }
                    else
                    {
                        _onlineUserIds.Remove(presenceEvent.UserId);
                        _typingUsers.Remove(presenceEvent.UserId);
                    }

                    _dirty = true;
                    return Task.CompletedTask;
                });
            }
        });

        _hub.Reconnecting += _ =>
        {
            _events.Writer.TryWrite(() =>
            {
                _live = false;
                _onlineUserIds.Clear();
                _typingUsers.Clear();
                _typingSent = false;
                SetStatus("Realtime reconnecting...", seconds: 30);
                return Task.CompletedTask;
            });

            return Task.CompletedTask;
        };

        _hub.Reconnected += _ =>
        {
            _events.Writer.TryWrite(async () =>
            {
                _live = true;
                await JoinHubChannelAsync();
                await ReloadMessagesQuietAsync();
                SetStatus("Realtime reconnected.");
            });

            return Task.CompletedTask;
        };

        _hub.Closed += _ =>
        {
            _events.Writer.TryWrite(() =>
            {
                _live = false;
                _onlineUserIds.Clear();
                _typingUsers.Clear();
                _typingSent = false;
                SetStatus(
                    "Realtime disconnected. Press F5 to retry.",
                    error: true);
                return Task.CompletedTask;
            });

            return Task.CompletedTask;
        };
    }

    private async Task ConnectHubAsync()
    {
        try
        {
            if (_hub is null)
            {
                BuildHub();
            }

            if (_hub!.State == HubConnectionState.Disconnected)
            {
                await _hub.StartAsync();
            }

            _live = _hub.State == HubConnectionState.Connected;

            if (_live)
            {
                await JoinHubChannelAsync();
                SetStatus("Realtime connected.");
            }
            else
            {
                SetStatus("Realtime offline.", error: true);
            }
        }
        catch (Exception ex)
        {
            _live = false;
            ShowError("Realtime unavailable: " + Friendly(ex));
        }
    }

    private async Task LoadChannelsAsync(int? selectChannelId = null)
    {
        ServerDto? server = CurrentServer;

        SaveCurrentDraft();
        CancelTyping();
        _typingUsers.Clear();
        _replyTarget = null;
        CloseMentionSuggestions();
        _mentionDismissed = false;
        _showServerDetails = false;
        _serverMembers.Clear();
        _channels.Clear();
        _channelIndex = -1;
        _messages.Clear();
        _messageIndex = -1;
        _viewStart = 0;
        _followTail = true;
        ClearComposerInput();

        if (server is null)
        {
            await LeaveHubChannelAsync();
            return;
        }

        try
        {
            List<ChannelDto> channels = await _api.GetChannelsAsync(server.Id);
            _channels.AddRange(channels);
            _channelsByServer[server.Id] = [.. _channels];

            if (_channels.Count == 0)
            {
                await LeaveHubChannelAsync();
                SetStatus("This server has no channels yet. Press c to create one.");
                return;
            }

            int index = selectChannelId is null
                ? 0
                : _channels.FindIndex(channel => channel.Id == selectChannelId.Value);

            _channelIndex = index >= 0 ? index : 0;
            LoadDraftForCurrentChannel();
            await LoadMessagesAsync();
            await JoinHubChannelAsync();
            ScheduleUnreadPoll();
        }
        catch (Exception ex)
        {
            ShowError("Could not load channels: " + Friendly(ex));
        }
    }

    private async Task LoadMessagesAsync()
    {
        ChannelDto? channel = CurrentChannel;

        _messages.Clear();
        _messageIndex = -1;
        _viewStart = 0;
        _followTail = true;
        _ensureSelectionVisible = false;

        if (channel is null)
        {
            await LeaveHubChannelAsync();
            return;
        }

        try
        {
            List<MessageDto> messages = await _api.GetMessagesAsync(channel.Id);
            messages.Reverse();
            _messages.AddRange(messages);
            _messageIndex = _messages.Count - 1;

            if (_messages.Count > 0)
            {
                MarkChannelRead(channel.Id, _messages[^1].Id);
            }

            RequestAvatarLoads();
        }
        catch (Exception ex)
        {
            ShowError("Could not load messages: " + Friendly(ex));
        }
    }

    private async Task ReloadMessagesQuietAsync()
    {
        ChannelDto? channel = CurrentChannel;
        if (channel is null)
        {
            return;
        }

        int? selectedId = CurrentMessage?.Id;
        bool follow = _followTail;

        try
        {
            List<MessageDto> messages = await _api.GetMessagesAsync(channel.Id);
            messages.Reverse();
            _messages.Clear();
            _messages.AddRange(messages);

            int index = selectedId is null
                ? -1
                : _messages.FindIndex(message => message.Id == selectedId.Value);

            _messageIndex = index >= 0 ? index : _messages.Count - 1;
            _followTail = follow;
            _ensureSelectionVisible = true;

            if (_messages.Count > 0)
            {
                MarkChannelRead(channel.Id, _messages[^1].Id);
            }

            RequestAvatarLoads();

            if (_showSearch)
            {
                UpdateSearchResults();
            }
        }
        catch (Exception ex)
        {
            ShowError("Could not refresh messages: " + Friendly(ex));
        }
    }

    private async Task JoinHubChannelAsync()
    {
        if (_hub is null || _hub.State != HubConnectionState.Connected)
        {
            return;
        }

        ChannelDto? channel = CurrentChannel;

        try
        {
            if (_joinedChannelId > 0 &&
                _joinedChannelId != (channel?.Id ?? -1))
            {
                await _hub.SendAsync("LeavelChannel", _joinedChannelId);
                _joinedChannelId = -1;
            }

            if (channel is not null && _joinedChannelId != channel.Id)
            {
                await _hub.SendAsync("JoinChannel", channel.Id);
                _joinedChannelId = channel.Id;
            }
        }
        catch (Exception ex)
        {
            ShowError("Could not join the realtime channel: " + Friendly(ex));
        }
    }

    private async Task LeaveHubChannelAsync()
    {
        if (_hub is null ||
            _hub.State != HubConnectionState.Connected ||
            _joinedChannelId <= 0)
        {
            _joinedChannelId = -1;
            return;
        }

        try
        {
            await _hub.SendAsync("LeavelChannel", _joinedChannelId);
        }
        catch (Exception ex)
        {
            ShowError("Could not leave the realtime channel: " + Friendly(ex));
        }
        finally
        {
            _joinedChannelId = -1;
        }
    }

    private async Task SelectServerAsync(int index, bool force = false)
    {
        if (index < 0 || index >= _servers.Count)
        {
            return;
        }

        bool changed = index != _serverIndex;
        _serverIndex = index;

        if (!force && !changed && _channels.Count > 0)
        {
            return;
        }

        await LoadChannelsAsync();
    }

    private async Task SelectChannelAsync(int index, bool force = false)
    {
        if (index < 0 || index >= _channels.Count)
        {
            return;
        }

        bool changed = index != _channelIndex;

        if (changed)
        {
            SaveCurrentDraft();
            CancelTyping();
            _typingUsers.Clear();
            _replyTarget = null;
            CloseMentionSuggestions();
            _mentionDismissed = false;
        }

        _channelIndex = index;

        if (!force && !changed && _messages.Count > 0)
        {
            return;
        }

        if (changed)
        {
            LoadDraftForCurrentChannel();
        }

        await LoadMessagesAsync();
        await JoinHubChannelAsync();
    }

    private async Task RefreshAsync()
    {
        Guid? serverId = CurrentServer?.Id;
        int? channelId = CurrentChannel?.Id;

        try
        {
            CloseMentionSuggestions();
            _mentionDismissed = false;
            _membersByServer.Clear();

            if (_hub is null || _hub.State == HubConnectionState.Disconnected)
            {
                await ConnectHubAsync();
            }

            List<ServerDto> servers = await _api.GetServersAsync();
            _servers.Clear();
            _servers.AddRange(servers);

            if (_servers.Count == 0)
            {
                SaveCurrentDraft();
                _serverIndex = -1;
                _channels.Clear();
                _channelIndex = -1;
                _messages.Clear();
                _messageIndex = -1;
                ClearComposerInput();
                await LeaveHubChannelAsync();
                SetStatus("Refreshed. No servers yet.");
                return;
            }

            int index = serverId is null
                ? -1
                : _servers.FindIndex(server => server.Id == serverId.Value);

            _serverIndex = index >= 0 ? index : 0;
            await LoadChannelsAsync(channelId);
            SetStatus("Refreshed.");
        }
        catch (Exception ex)
        {
            ShowError("Refresh failed: " + Friendly(ex));
        }
    }

    private async Task HandleKeyAsync(ConsoleKeyInfo key)
    {
        if (key.Key == ConsoleKey.C &&
            key.Modifiers.HasFlag(ConsoleModifiers.Control))
        {
            _quit = true;
            return;
        }

        if (key.Key == ConsoleKey.F1)
        {
            if (_showQuickSwitch)
            {
                CloseQuickSwitch();
            }

            if (_showEmojiPicker)
            {
                CloseEmojiPicker();
            }

            _help = !_help;

            if (_help)
            {
                _helpScroll = 0;
            }

            _showServerDetails = false;
            return;
        }

        if (key.Key == ConsoleKey.F5)
        {
            await RefreshAsync();
            return;
        }

        bool quickSwitchShortcut =
            key.Key == ConsoleKey.F2 ||
            (key.Key == ConsoleKey.K &&
             key.Modifiers.HasFlag(ConsoleModifiers.Control));

        if (quickSwitchShortcut && _modal is null)
        {
            if (_showQuickSwitch)
            {
                CloseQuickSwitch();
            }
            else if (!HasOverlay)
            {
                OpenQuickSwitch();
            }

            return;
        }

        bool emojiShortcut =
            key.Key == ConsoleKey.F3 ||
            (key.Key == ConsoleKey.E &&
             key.Modifiers.HasFlag(ConsoleModifiers.Control));

        if (emojiShortcut && _modal is null)
        {
            if (_showEmojiPicker)
            {
                CloseEmojiPicker();
            }
            else if (!HasOverlay)
            {
                OpenEmojiPicker();
            }

            return;
        }

        bool canPasteImage =
            key.Key == ConsoleKey.F4 &&
            _modal is null &&
            !HasOverlay;

        if (canPasteImage)
        {
            await PasteImageAsync();
            return;
        }

        if (_modal is not null)
        {
            await HandleModalKeyAsync(key);
            return;
        }

        if (key.Key == ConsoleKey.Escape && _showQuickSwitch)
        {
            CloseQuickSwitch();
            return;
        }

        if (key.Key == ConsoleKey.Escape && _showEmojiPicker)
        {
            CloseEmojiPicker();
            return;
        }

        if (key.Key == ConsoleKey.Escape && _help)
        {
            _help = false;
            _dirty = true;
            return;
        }

        if (_help)
        {
            HandleHelpKey(key);
            return;
        }

        if (key.Key == ConsoleKey.Escape && _showSearch)
        {
            CloseSearch();
            return;
        }

        if (key.Key == ConsoleKey.Escape && _showServerDetails)
        {
            _showServerDetails = false;
            return;
        }

        if (key.Key == ConsoleKey.Escape && _showAvatarPicker)
        {
            if (_avatarSearchActive)
            {
                ExitAvatarSearch();
            }
            else
            {
                CloseAvatarPicker();
            }

            return;
        }

        if (key.Key == ConsoleKey.Escape && _showProfile)
        {
            _showProfile = false;
            return;
        }

        if (_showQuickSwitch)
        {
            await HandleQuickSwitchKeyAsync(key);
            return;
        }

        if (_showEmojiPicker)
        {
            await HandleEmojiPickerKeyAsync(key);
            return;
        }

        if (_showSearch)
        {
            HandleSearchKey(key);
            return;
        }

        if (_showServerDetails)
        {
            HandleServerDetailKey(key);
            return;
        }

        if (_showAvatarPicker)
        {
            await HandleAvatarPickerKeyAsync(key);
            return;
        }

        if (_showProfile)
        {
            await HandleProfileKeyAsync(key);
            return;
        }

        if (_focus == Pane.Input)
        {
            await HandleInputKeyAsync(key);
            return;
        }

        await HandleNavigationKeyAsync(key);
    }

    private void HandleHelpKey(ConsoleKeyInfo key)
    {
        int page = Math.Max(1, _helpPageHeight - 2);

        switch (key.Key)
        {
            case ConsoleKey.UpArrow:
                _helpScroll = Math.Max(0, _helpScroll - 1);
                _dirty = true;
                return;
            case ConsoleKey.DownArrow:
                _helpScroll = Math.Min(_helpMaxScroll, _helpScroll + 1);
                _dirty = true;
                return;
            case ConsoleKey.PageUp:
                _helpScroll = Math.Max(0, _helpScroll - page);
                _dirty = true;
                return;
            case ConsoleKey.PageDown:
                _helpScroll = Math.Min(_helpMaxScroll, _helpScroll + page);
                _dirty = true;
                return;
            case ConsoleKey.Home:
                _helpScroll = 0;
                _dirty = true;
                return;
            case ConsoleKey.End:
                _helpScroll = _helpMaxScroll;
                _dirty = true;
                return;
        }

        if (key.KeyChar == '?')
        {
            _help = false;
            _dirty = true;
        }
    }

    private async Task HandleNavigationKeyAsync(ConsoleKeyInfo key)
    {
        switch (key.Key)
        {
            case ConsoleKey.Tab:
                CycleFocus(key.Modifiers.HasFlag(ConsoleModifiers.Shift) ? -1 : 1);
                return;
            case ConsoleKey.LeftArrow:
                MoveFocus(-1);
                return;
            case ConsoleKey.RightArrow:
                MoveFocus(1);
                return;
            case ConsoleKey.UpArrow:
                await MovePaneAsync(-1);
                return;
            case ConsoleKey.DownArrow:
                await MovePaneAsync(1);
                return;
            case ConsoleKey.PageUp:
                ScrollMessages(-1);
                return;
            case ConsoleKey.PageDown:
                ScrollMessages(1);
                return;
            case ConsoleKey.Home:
                _followTail = false;
                _viewStart = 0;
                _ensureSelectionVisible = false;
                return;
            case ConsoleKey.End:
                JumpToLatest();
                return;
            case ConsoleKey.Enter:
                await ActivateAsync();
                return;
        }

        switch (char.ToLowerInvariant(key.KeyChar))
        {
            case 'i':
                _focus = Pane.Input;
                break;
            case '/':
                OpenSearch();
                break;
            case 'f':
                OpenAttachmentPicker();
                break;
            case 'o':
                OpenAttachment();
                break;
            case 'n':
                OpenModal(ModalKind.CreateServer, "New server", "Server name");
                break;
            case 'c':
                OpenChannelModal();
                break;
            case 'j':
                OpenModal(ModalKind.JoinServer, "Join server", "Server ID (GUID)");
                break;
            case 'v':
                await ShowServerDetailsAsync();
                break;
            case 'p':
                await ShowProfileAsync();
                break;
            case 'u':
                await JumpToNextUnreadChannelAsync();
                break;
            case 'r':
                if (_focus == Pane.Messages && CurrentMessage is not null)
                {
                    StartReply();
                }
                else
                {
                    await RefreshAsync();
                }
                break;
            case 'x':
                if (_focus == Pane.Messages && CurrentMessage is not null)
                {
                    OpenEmojiPicker(
                        EmojiPickerMode.Reaction,
                        CurrentMessage.Id);
                }
                break;
            case 'q':
                _quit = true;
                break;
            case '?':
                _help = !_help;

                if (_help)
                {
                    _helpScroll = 0;
                }

                _showServerDetails = false;
                break;
            case 'e':
                OpenEditModal();
                break;
            case 'd':
                OpenDeleteModal();
                break;
        }
    }

    private void OpenQuickSwitch()
    {
        CloseMentionSuggestions();
        _mentionDismissed = false;

        if (_showSearch)
        {
            CloseSearch();
        }

        if (_showAvatarPicker)
        {
            CloseAvatarPicker();
        }

        _showQuickSwitch = true;
        _quickSwitchQuery = "";
        _quickSwitchIndex = 0;
        _help = false;
        _showServerDetails = false;
        _showProfile = false;

        UpdateQuickSwitchResults(resetSelection: true);

        int current = _quickSwitchResults.FindIndex(entry =>
            entry.Kind == QuickSwitchKind.Channel &&
            entry.ServerId == CurrentServer?.Id &&
            entry.ChannelId == CurrentChannel?.Id);

        if (current >= 0)
        {
            _quickSwitchIndex = current;
        }

        ScheduleUnreadPoll();
        SetStatus("Quick switch. Type to filter, Enter to open.");
        _dirty = true;
    }

    private void CloseQuickSwitch()
    {
        _showQuickSwitch = false;
        _quickSwitchQuery = "";
        _quickSwitchResults.Clear();
        _quickSwitchIndex = 0;
        _dirty = true;
    }

    private void UpdateQuickSwitchResults(bool resetSelection = false)
    {
        _quickSwitchResults.Clear();

        string query = _quickSwitchQuery.Trim();
        List<QuickSwitchEntry> results = [];
        int order = 0;

        IEnumerable<int> serverOrder = Enumerable
            .Range(0, _servers.Count)
            .OrderBy(index => index == _serverIndex ? 0 : 1)
            .ThenBy(index => index);

        foreach (int serverIndex in serverOrder)
        {
            ServerDto server = _servers[serverIndex];

            AddQuickSwitchMatch(
                results,
                query,
                QuickSwitchKind.Server,
                server,
                null,
                order++);

            IReadOnlyList<ChannelDto> channels;
            if (serverIndex == _serverIndex)
            {
                channels = _channels;
            }
            else if (_channelsByServer.TryGetValue(
                         server.Id,
                         out List<ChannelDto>? cached))
            {
                channels = cached;
            }
            else
            {
                channels = [];
            }

            foreach (ChannelDto channel in channels)
            {
                AddQuickSwitchMatch(
                    results,
                    query,
                    QuickSwitchKind.Channel,
                    server,
                    channel,
                    order++);
            }
        }

        results.Sort((left, right) =>
        {
            int score = right.Score.CompareTo(left.Score);
            return score != 0
                ? score
                : left.Order.CompareTo(right.Order);
        });

        _quickSwitchResults.AddRange(results);

        if (resetSelection || _quickSwitchIndex >= _quickSwitchResults.Count)
        {
            _quickSwitchIndex = 0;
        }
        else
        {
            _quickSwitchIndex = Math.Clamp(
                _quickSwitchIndex,
                0,
                Math.Max(0, _quickSwitchResults.Count - 1));
        }

        _dirty = true;
    }

    private static void AddQuickSwitchMatch(
        List<QuickSwitchEntry> results,
        string query,
        QuickSwitchKind kind,
        ServerDto server,
        ChannelDto? channel,
        int order)
    {
        string searchText = kind == QuickSwitchKind.Server
            ? server.Name
            : $"{server.Name} #{channel!.Name}";
        int score = 0;

        if (query.Length > 0 &&
            !TryFuzzyMatch(searchText, query, out score, out _))
        {
            return;
        }

        results.Add(new QuickSwitchEntry(
            kind,
            server.Id,
            server.Name,
            channel?.Id ?? 0,
            channel?.Name ?? "",
            score,
            order));
    }

    private async Task HandleQuickSwitchKeyAsync(ConsoleKeyInfo key)
    {
        switch (key.Key)
        {
            case ConsoleKey.Enter:
                await JumpToQuickSwitchResultAsync();
                return;
            case ConsoleKey.UpArrow:
                MoveQuickSwitchSelection(-1);
                return;
            case ConsoleKey.DownArrow:
                MoveQuickSwitchSelection(1);
                return;
            case ConsoleKey.PageUp:
                MoveQuickSwitchSelection(-10);
                return;
            case ConsoleKey.PageDown:
                MoveQuickSwitchSelection(10);
                return;
            case ConsoleKey.Home:
                _quickSwitchIndex = 0;
                _dirty = true;
                return;
            case ConsoleKey.End:
                _quickSwitchIndex = Math.Max(
                    0,
                    _quickSwitchResults.Count - 1);
                _dirty = true;
                return;
            case ConsoleKey.Backspace:
                if (_quickSwitchQuery.Length > 0)
                {
                    _quickSwitchQuery = _quickSwitchQuery[..^1];
                    UpdateQuickSwitchResults(resetSelection: true);
                }

                return;
        }

        char character = key.KeyChar;

        if (character == '\0' || char.IsControl(character))
        {
            return;
        }

        if (_quickSwitchQuery.Length < MaxInputLength)
        {
            _quickSwitchQuery += character;
            UpdateQuickSwitchResults(resetSelection: true);
        }
    }

    private void MoveQuickSwitchSelection(int direction)
    {
        if (_quickSwitchResults.Count == 0)
        {
            return;
        }

        _quickSwitchIndex = Math.Clamp(
            _quickSwitchIndex + direction,
            0,
            _quickSwitchResults.Count - 1);
        _dirty = true;
    }

    private async Task JumpToQuickSwitchResultAsync()
    {
        if (_quickSwitchResults.Count == 0)
        {
            return;
        }

        QuickSwitchEntry entry = _quickSwitchResults[_quickSwitchIndex];
        int serverIndex = _servers.FindIndex(
            server => server.Id == entry.ServerId);

        if (serverIndex < 0)
        {
            CloseQuickSwitch();
            ShowError("Server no longer exists.");
            return;
        }

        CloseQuickSwitch();
        _help = false;
        _showServerDetails = false;
        _showProfile = false;

        if (entry.Kind == QuickSwitchKind.Server)
        {
            _serverIndex = serverIndex;
            await LoadChannelsAsync();
            _focus = Pane.Channels;
            SetStatus($"Switched to {entry.ServerName}.");
            return;
        }

        if (serverIndex != _serverIndex)
        {
            _serverIndex = serverIndex;
            await LoadChannelsAsync(entry.ChannelId);
        }
        else
        {
            int channelIndex = _channels.FindIndex(
                channel => channel.Id == entry.ChannelId);

            if (channelIndex < 0)
            {
                ShowError("Channel no longer exists.");
                return;
            }

            await SelectChannelAsync(channelIndex, force: true);
        }

        if (_channels.All(channel => channel.Id != entry.ChannelId))
        {
            _focus = Pane.Channels;
            SetStatus($"Channel #{entry.ChannelName} no longer exists.");
            return;
        }

        _focus = Pane.Messages;
        SetStatus($"Switched to #{entry.ChannelName}.");
        _dirty = true;
    }

    private void OpenEmojiPicker(
        EmojiPickerMode mode = EmojiPickerMode.Insert,
        int messageId = 0)
    {
        CloseMentionSuggestions();
        _mentionDismissed = false;
        _showEmojiPicker = true;
        _emojiPickerMode = mode;
        _reactionMessageId = messageId;
        _emojiQuery = "";
        _emojiIndex = 0;
        _help = false;
        _showServerDetails = false;
        _showProfile = false;

        UpdateEmojiResults(resetSelection: true);
        SetStatus(mode == EmojiPickerMode.Reaction
            ? "Choose a reaction. Enter adds or removes it."
            : "Emoji picker. Type to search, Enter to insert.");
        _dirty = true;
    }

    private void CloseEmojiPicker()
    {
        _showEmojiPicker = false;
        _emojiPickerMode = EmojiPickerMode.Insert;
        _reactionMessageId = 0;
        _emojiQuery = "";
        _emojiResults.Clear();
        _emojiIndex = 0;
        _dirty = true;
    }

    private void UpdateEmojiResults(bool resetSelection = false)
    {
        _emojiResults.Clear();

        string query = _emojiQuery.Trim();
        List<(EmojiEntry Entry, int Score, int Order)> results = [];
        int order = 0;

        foreach (EmojiEntry entry in EmojiCatalog.Entries)
        {
            int score = 0;

            if (query.Length == 0 ||
                TryFuzzyMatch(
                    $"{entry.Name} {entry.Keywords}",
                    query,
                    out score,
                    out _))
            {
                results.Add((entry, score, order));
            }

            order++;
        }

        results.Sort((left, right) =>
        {
            int score = right.Score.CompareTo(left.Score);
            return score != 0
                ? score
                : left.Order.CompareTo(right.Order);
        });

        _emojiResults.AddRange(results.Select(result => result.Entry));

        if (resetSelection || _emojiIndex >= _emojiResults.Count)
        {
            _emojiIndex = 0;
        }
        else
        {
            _emojiIndex = Math.Clamp(
                _emojiIndex,
                0,
                Math.Max(0, _emojiResults.Count - 1));
        }

        _dirty = true;
    }

    private async Task HandleEmojiPickerKeyAsync(ConsoleKeyInfo key)
    {
        switch (key.Key)
        {
            case ConsoleKey.Enter:
                await InsertSelectedEmojiAsync();
                return;
            case ConsoleKey.UpArrow:
                MoveEmojiSelection(-1);
                return;
            case ConsoleKey.DownArrow:
                MoveEmojiSelection(1);
                return;
            case ConsoleKey.PageUp:
                MoveEmojiSelection(-10);
                return;
            case ConsoleKey.PageDown:
                MoveEmojiSelection(10);
                return;
            case ConsoleKey.Home:
                _emojiIndex = 0;
                _dirty = true;
                return;
            case ConsoleKey.End:
                _emojiIndex = Math.Max(0, _emojiResults.Count - 1);
                _dirty = true;
                return;
            case ConsoleKey.Backspace:
                if (_emojiQuery.Length > 0)
                {
                    _emojiQuery = _emojiQuery[..^1];
                    UpdateEmojiResults(resetSelection: true);
                }

                return;
        }

        char character = key.KeyChar;

        if (character == '\0' || char.IsControl(character))
        {
            return;
        }

        if (_emojiQuery.Length < MaxInputLength)
        {
            _emojiQuery += character;
            UpdateEmojiResults(resetSelection: true);
        }
    }

    private void MoveEmojiSelection(int direction)
    {
        if (_emojiResults.Count == 0)
        {
            return;
        }

        _emojiIndex = Math.Clamp(
            _emojiIndex + direction,
            0,
            _emojiResults.Count - 1);
        _dirty = true;
    }

    private async Task InsertSelectedEmojiAsync()
    {
        if (_emojiResults.Count == 0)
        {
            return;
        }

        string symbol = _emojiResults[_emojiIndex].Symbol;

        if (_emojiPickerMode == EmojiPickerMode.Reaction)
        {
            int messageId = _reactionMessageId;
            CloseEmojiPicker();
            await ToggleReactionAsync(messageId, symbol);
            return;
        }

        if (_input.Length + symbol.Length <= MaxInputLength)
        {
            _input += symbol;
        }

        _historyCursor = -1;
        SignalTyping();
        CloseEmojiPicker();
        _focus = Pane.Input;
        await UpdateMentionSuggestionsAsync();
        _dirty = true;
    }

    private async Task ToggleReactionAsync(int messageId, string emoji)
    {
        try
        {
            ReactionResultDto result = await _api.ToggleReactionAsync(
                messageId,
                emoji);

            int index = _messages.FindIndex(message => message.Id == messageId);

            if (index >= 0)
            {
                _messages[index] = _messages[index] with
                {
                    Reactions = result.Reactions,
                    MyReactions = result.MyReactions
                };
            }

            SetStatus("Reaction updated.");
            _dirty = true;
        }
        catch (Exception ex)
        {
            ShowError("Could not update reaction: " + Friendly(ex));
        }
    }

    private async Task PasteImageAsync()
    {
        ChannelDto? channel = CurrentChannel;

        if (channel is null)
        {
            ShowError("Select a channel first.");
            return;
        }

        string tempDirectory = Path.Combine(
            Path.GetTempPath(),
            "rtchat-paste-" + Guid.NewGuid().ToString("N"));

        try
        {
            (bool success, string imagePath, string error) =
                await ClipboardImage.TryGetImageAsync(tempDirectory);

            if (!success)
            {
                ShowError(error);
                return;
            }

            string caption = _input.Trim();
            bool sent = await SendAttachmentAsync(imagePath, caption);

            if (sent && caption.Length > 0)
            {
                _input = "";
                _draftsByChannel.Remove(channel.Id);
                _historyCursor = -1;
                _draft = "";
            }
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempDirectory))
                {
                    Directory.Delete(tempDirectory, recursive: true);
                }
            }
            catch
            {
            }
        }
    }

    private void OpenSearch()
    {
        if (CurrentChannel is null)
        {
            ShowError("Select a channel first.");
            return;
        }

        _showSearch = true;
        _searchQuery = "";
        _searchIndex = 0;
        _help = false;
        _showServerDetails = false;
        _showProfile = false;

        if (_showAvatarPicker)
        {
            CloseAvatarPicker();
        }

        UpdateSearchResults(resetSelection: true);
        SetStatus("Search messages. Type to filter, Enter to jump.");
        _dirty = true;
    }

    private void CloseSearch()
    {
        _showSearch = false;
        _searchQuery = "";
        _searchResults.Clear();
        _searchIndex = 0;
        _dirty = true;
    }

    private void HandleSearchKey(ConsoleKeyInfo key)
    {
        switch (key.Key)
        {
            case ConsoleKey.Enter:
                JumpToSearchResult();
                return;
            case ConsoleKey.UpArrow:
                MoveSearchSelection(-1);
                return;
            case ConsoleKey.DownArrow:
                MoveSearchSelection(1);
                return;
            case ConsoleKey.PageUp:
                MoveSearchSelection(-10);
                return;
            case ConsoleKey.PageDown:
                MoveSearchSelection(10);
                return;
            case ConsoleKey.Home:
                _searchIndex = 0;
                _dirty = true;
                return;
            case ConsoleKey.End:
                _searchIndex = Math.Max(0, _searchResults.Count - 1);
                _dirty = true;
                return;
            case ConsoleKey.Backspace:
                if (_searchQuery.Length > 0)
                {
                    _searchQuery = _searchQuery[..^1];
                    UpdateSearchResults(resetSelection: true);
                }

                return;
        }

        char character = key.KeyChar;

        if (character == '\0' || char.IsControl(character))
        {
            return;
        }

        if (_searchQuery.Length < MaxInputLength)
        {
            _searchQuery += character;
            UpdateSearchResults(resetSelection: true);
        }
    }

    private void MoveSearchSelection(int direction)
    {
        if (_searchResults.Count == 0)
        {
            return;
        }

        _searchIndex = Math.Clamp(
            _searchIndex + direction, 0, _searchResults.Count - 1);
        _dirty = true;
    }

    private void JumpToSearchResult()
    {
        if (_searchResults.Count == 0)
        {
            return;
        }

        int messageIndex = _searchResults[_searchIndex].MessageIndex;

        if (messageIndex < 0 || messageIndex >= _messages.Count)
        {
            return;
        }

        _showSearch = false;
        _searchQuery = "";
        _searchResults.Clear();
        _searchIndex = 0;
        _focus = Pane.Messages;
        _messageIndex = messageIndex;
        _followTail = messageIndex == _messages.Count - 1;
        _ensureSelectionVisible = true;
        _help = false;
        SetStatus("Jumped to message.");
        _dirty = true;
    }

    private void UpdateSearchResults(bool resetSelection = false)
    {
        _searchResults.Clear();

        string query = _searchQuery.Trim();
        List<SearchEntry> results = [];

        for (int i = 0; i < _messages.Count; i++)
        {
            string content = NormalizeSearchText(_messages[i].Content);

            if (query.Length == 0)
            {
                results.Add(new SearchEntry(i, 0, content, []));
            }
            else if (TryFuzzyMatch(
                content, query, out int score, out List<int> matches))
            {
                results.Add(new SearchEntry(i, score, content, matches));
            }
        }

        results.Sort((left, right) =>
        {
            int score = right.Score.CompareTo(left.Score);
            return score != 0
                ? score
                : right.MessageIndex.CompareTo(left.MessageIndex);
        });

        _searchResults.AddRange(results);

        if (resetSelection || _searchIndex >= _searchResults.Count)
        {
            _searchIndex = 0;
        }
        else
        {
            _searchIndex = Math.Clamp(
                _searchIndex, 0, Math.Max(0, _searchResults.Count - 1));
        }

        _dirty = true;
    }

    private static string NormalizeSearchText(string content)
    {
        return content
            .ReplaceLineEndings(" ")
            .Replace('\t', ' ')
            .Trim();
    }

    private static bool TryFuzzyMatch(
        string text,
        string query,
        out int score,
        out List<int> matches)
    {
        matches = [];
        score = 0;
        int cursor = 0;
        int previous = -2;

        foreach (char rawCharacter in query)
        {
            char wanted = char.ToLowerInvariant(rawCharacter);
            int found = -1;

            for (int i = cursor; i < text.Length; i++)
            {
                if (char.ToLowerInvariant(text[i]) == wanted)
                {
                    found = i;
                    break;
                }
            }

            if (found < 0)
            {
                return false;
            }

            matches.Add(found);
            score += 10;

            if (found == 0 || !char.IsLetterOrDigit(text[found - 1]))
            {
                score += 8;
            }

            if (found == previous + 1)
            {
                score += 6;
            }

            score -= Math.Min(found, 12);
            previous = found;
            cursor = found + 1;
        }

        score -= text.Length / 12;
        return true;
    }

    private bool TryFindMention(out int startIndex, out string query)
    {
        startIndex = -1;
        query = "";

        int at = _input.LastIndexOf('@');

        if (at < 0 ||
            (at > 0 && !char.IsWhiteSpace(_input[at - 1])))
        {
            return false;
        }

        for (int i = at + 1; i < _input.Length; i++)
        {
            char character = _input[i];

            if (char.IsWhiteSpace(character) ||
                !(char.IsLetterOrDigit(character) ||
                  character is '_' or '-' or '.' or '@'))
            {
                return false;
            }
        }

        startIndex = at;
        query = _input[(at + 1)..];
        return true;
    }

    private async Task UpdateMentionSuggestionsAsync()
    {
        if (!TryFindMention(out int startIndex, out string query))
        {
            _mentionDismissed = false;
            CloseMentionSuggestions();
            return;
        }

        if (_mentionDismissed)
        {
            return;
        }

        ServerDto? server = CurrentServer;

        if (server is null)
        {
            CloseMentionSuggestions();
            return;
        }

        if (!_membersByServer.TryGetValue(
                server.Id,
                out List<ServerMemberDto>? members))
        {
            try
            {
                members = await _api.GetMembersAsync(server.Id);
                _membersByServer[server.Id] = members;

                if (CurrentServer?.Id == server.Id)
                {
                    _serverMembers.Clear();
                    _serverMembers.AddRange(members);
                }
            }
            catch (Exception ex)
            {
                ShowError("Could not load members: " + Friendly(ex));
                members = [];
            }
        }

        bool queryChanged = !string.Equals(
            _mentionQuery,
            query,
            StringComparison.Ordinal);

        _mentionStartIndex = startIndex;
        _mentionQuery = query;
        _showMentionSuggestions = true;

        string needle = query.Trim();
        List<(ServerMemberDto Member, int Score, bool Online)> matches = [];

        foreach (ServerMemberDto member in members)
        {
            if (string.IsNullOrWhiteSpace(member.UserName))
            {
                continue;
            }

            bool online = _onlineUserIds.Contains(member.UserId);
            int score = 0;

            if (needle.Length == 0 ||
                TryFuzzyMatch(
                    $"{member.UserName} {member.DisplayName}",
                    needle,
                    out score,
                    out _))
            {
                matches.Add((member, score, online));
            }
        }

        matches.Sort((left, right) =>
        {
            int score = right.Score.CompareTo(left.Score);

            if (score != 0)
            {
                return score;
            }

            int online = right.Online.CompareTo(left.Online);

            return online != 0
                ? online
                : string.Compare(
                    left.Member.DisplayName,
                    right.Member.DisplayName,
                    StringComparison.OrdinalIgnoreCase);
        });

        _mentionResults.Clear();

        foreach ((ServerMemberDto member, _, _) in matches.Take(
                     MaxMentionSuggestions))
        {
            _mentionResults.Add(member);
        }

        if (queryChanged)
        {
            _mentionIndex = 0;
        }
        else
        {
            _mentionIndex = Math.Clamp(
                _mentionIndex,
                0,
                Math.Max(0, _mentionResults.Count - 1));
        }

        _dirty = true;
    }

    private void CloseMentionSuggestions()
    {
        bool wasVisible = _showMentionSuggestions;

        _showMentionSuggestions = false;
        _mentionQuery = "";
        _mentionStartIndex = -1;
        _mentionIndex = 0;
        _mentionResults.Clear();

        if (wasVisible)
        {
            _dirty = true;
        }
    }

    private void MoveMentionSelection(int direction)
    {
        if (_mentionResults.Count == 0)
        {
            return;
        }

        _mentionIndex = Math.Clamp(
            _mentionIndex + direction,
            0,
            _mentionResults.Count - 1);
        _dirty = true;
    }

    private void InsertSelectedMention()
    {
        if (_mentionStartIndex < 0 ||
            _mentionStartIndex > _input.Length ||
            _mentionIndex < 0 ||
            _mentionIndex >= _mentionResults.Count)
        {
            return;
        }

        ServerMemberDto member = _mentionResults[_mentionIndex];

        if (string.IsNullOrWhiteSpace(member.UserName))
        {
            return;
        }

        string prefix = _input[.._mentionStartIndex];
        _input = prefix + "@" + member.UserName + " ";

        if (_input.Length > MaxInputLength)
        {
            _input = _input[..MaxInputLength];
        }

        _historyCursor = -1;
        _mentionDismissed = false;
        CloseMentionSuggestions();
        SignalTyping();
        _dirty = true;
    }

    private async Task HandleInputKeyAsync(ConsoleKeyInfo key)
    {
        if (_showMentionSuggestions)
        {
            switch (key.Key)
            {
                case ConsoleKey.Escape:
                    _mentionDismissed = true;
                    CloseMentionSuggestions();
                    return;
                case ConsoleKey.UpArrow when _mentionResults.Count > 0:
                    MoveMentionSelection(-1);
                    return;
                case ConsoleKey.DownArrow when _mentionResults.Count > 0:
                    MoveMentionSelection(1);
                    return;
                case ConsoleKey.Tab when _mentionResults.Count > 0:
                    InsertSelectedMention();
                    return;
                case ConsoleKey.Enter when _mentionResults.Count > 0:
                    InsertSelectedMention();
                    return;
            }
        }

        switch (key.Key)
        {
            case ConsoleKey.Escape:
                CancelTyping();
                CloseMentionSuggestions();
                _mentionDismissed = false;

                if (_replyTarget is not null)
                {
                    CancelReply();
                    return;
                }

                _focus = Pane.Messages;
                return;
            case ConsoleKey.Enter:
                if (ShouldSendOnEnter(key))
                {
                    await SendAsync();
                }
                else
                {
                    InsertNewline();
                }

                return;
            case ConsoleKey.J when key.Modifiers.HasFlag(ConsoleModifiers.Control):
                InsertNewline();
                return;
            case ConsoleKey.Backspace:
                if (_input.Length > 0)
                {
                    _input = _input[..^1];
                }

                _historyCursor = -1;
                SignalTyping();
                await UpdateMentionSuggestionsAsync();
                return;
            case ConsoleKey.UpArrow:
                RecallHistory(-1);
                await UpdateMentionSuggestionsAsync();
                return;
            case ConsoleKey.DownArrow:
                RecallHistory(1);
                await UpdateMentionSuggestionsAsync();
                return;
            case ConsoleKey.PageUp:
                ScrollMessages(-1);
                return;
            case ConsoleKey.PageDown:
                ScrollMessages(1);
                return;
            case ConsoleKey.Home:
                _followTail = false;
                _viewStart = 0;
                return;
            case ConsoleKey.End:
                JumpToLatest();
                return;
        }

        char character = key.KeyChar;
        if (character == '\0' || char.IsControl(character))
        {
            return;
        }

        if (character == '@')
        {
            _mentionDismissed = false;
        }

        if (_input.Length < MaxInputLength)
        {
            _input += character;
        }

        _historyCursor = -1;
        SignalTyping();
        await UpdateMentionSuggestionsAsync();
    }

    private bool ShouldSendOnEnter(ConsoleKeyInfo key)
    {
        bool control = key.Modifiers.HasFlag(ConsoleModifiers.Control);
        bool shift = key.Modifiers.HasFlag(ConsoleModifiers.Shift);

        return _sendOnCtrlEnter
            ? control
            : !control && !shift;
    }

    private void InsertNewline()
    {
        if (_input.Length < MaxInputLength)
        {
            _input += '\n';
        }

        _historyCursor = -1;
        CloseMentionSuggestions();
        _mentionDismissed = false;
        SignalTyping();
    }

    private void CycleFocus(int direction)
    {
        Pane[] order = [Pane.Servers, Pane.Channels, Pane.Messages, Pane.Input];
        int index = Array.IndexOf(order, _focus);
        index = (index + direction + order.Length) % order.Length;
        _focus = order[index];
        _dirty = true;
    }

    private void MoveFocus(int direction)
    {
        Pane[] order = [Pane.Servers, Pane.Channels, Pane.Messages, Pane.Input];
        int index = Array.IndexOf(order, _focus);
        _focus = order[Math.Clamp(index + direction, 0, order.Length - 1)];
        _dirty = true;
    }

    private async Task MovePaneAsync(int direction)
    {
        switch (_focus)
        {
            case Pane.Servers when _servers.Count > 0:
                int serverIndex = Math.Clamp(
                    _serverIndex + direction, 0, _servers.Count - 1);
                await SelectServerAsync(serverIndex);
                break;
            case Pane.Channels when _channels.Count > 0:
                int channelIndex = Math.Clamp(
                    _channelIndex + direction, 0, _channels.Count - 1);
                await SelectChannelAsync(channelIndex);
                break;
            case Pane.Messages:
                MoveMessageSelection(direction);
                break;
        }
    }

    private void MoveMessageSelection(int direction)
    {
        if (_messages.Count == 0)
        {
            return;
        }

        _messageIndex = Math.Clamp(
            _messageIndex + direction, 0, _messages.Count - 1);
        _followTail = _messageIndex == _messages.Count - 1;
        _ensureSelectionVisible = true;
    }

    private void ScrollMessages(int direction)
    {
        if (_messages.Count == 0)
        {
            return;
        }

        _followTail = false;
        _ensureSelectionVisible = false;
        int step = Math.Max(1, _lastBodyHeight - 1);
        _viewStart = Math.Max(0, _viewStart + direction * step);
    }

    private void JumpToLatest()
    {
        _followTail = true;
        _ensureSelectionVisible = false;
        _messageIndex = _messages.Count - 1;
    }

    private async Task ActivateAsync()
    {
        switch (_focus)
        {
            case Pane.Servers:
                if (CurrentServer is not null)
                {
                    _focus = Pane.Channels;
                }
                break;
            case Pane.Channels:
                if (CurrentChannel is not null)
                {
                    _focus = Pane.Messages;
                }
                break;
            case Pane.Messages:
                _focus = Pane.Input;
                break;
            case Pane.Input:
                await SendAsync();
                break;
        }
    }

    private void RecallHistory(int direction)
    {
        if (_sentHistory.Count == 0)
        {
            return;
        }

        if (direction < 0)
        {
            if (_historyCursor == -1)
            {
                _draft = _input;
                _historyCursor = _sentHistory.Count - 1;
            }
            else
            {
                _historyCursor = Math.Max(0, _historyCursor - 1);
            }

            _input = _sentHistory[_historyCursor];
            return;
        }

        if (_historyCursor == -1)
        {
            return;
        }

        _historyCursor++;

        if (_historyCursor >= _sentHistory.Count)
        {
            _historyCursor = -1;
            _input = _draft;
        }
        else
        {
            _input = _sentHistory[_historyCursor];
        }
    }

    private void SaveCurrentDraft()
    {
        if (_channelIndex < 0 || _channelIndex >= _channels.Count)
        {
            return;
        }

        int channelId = _channels[_channelIndex].Id;
        string draft = _historyCursor >= 0 ? _draft : _input;

        if (string.IsNullOrEmpty(draft))
        {
            _draftsByChannel.Remove(channelId);
        }
        else
        {
            _draftsByChannel[channelId] = draft;
        }
    }

    private void LoadDraftForCurrentChannel()
    {
        _historyCursor = -1;
        _draft = "";

        if (_channelIndex >= 0 &&
            _channelIndex < _channels.Count &&
            _draftsByChannel.TryGetValue(
                _channels[_channelIndex].Id,
                out string? draft))
        {
            _input = draft;
        }
        else
        {
            _input = "";
        }
    }

    private void ClearComposerInput()
    {
        _input = "";
        _draft = "";
        _historyCursor = -1;
    }

    private void ClearCommandInput()
    {
        if (CurrentChannel is { } channel)
        {
            _draftsByChannel.Remove(channel.Id);
        }

        CloseMentionSuggestions();
        _mentionDismissed = false;
        ClearComposerInput();
    }

    private void OpenHelp()
    {
        _help = true;
        _helpScroll = 0;
        _showServerDetails = false;
        _showProfile = false;
        _showSearch = false;
        _showQuickSwitch = false;
        _showEmojiPicker = false;
        SetStatus("Help opened. Use ↑↓ to scroll, Esc to close.");
        _dirty = true;
    }

    private async Task ExecuteCommandAsync(string commandText)
    {
        string command = commandText
            .Split(' ', 2)[0]
            .ToLowerInvariant();

        switch (command)
        {
            case "/help":
            case "/keys":
            case "/?":
                ClearCommandInput();
                OpenHelp();
                return;
            case "/refresh":
                ClearCommandInput();
                await RefreshAsync();
                return;
            case "/quit":
            case "/exit":
                _quit = true;
                return;
            default:
                ShowError(
                    $"Unknown command '{command}'. Type /help for commands.");
                return;
        }
    }

    private async Task SendAsync()
    {
        string text = _input.Trim();

        if (text.StartsWith("//", StringComparison.Ordinal))
        {
            text = text[1..];
        }
        else if (text.StartsWith("/", StringComparison.Ordinal))
        {
            await ExecuteCommandAsync(text);
            return;
        }

        ChannelDto? channel = CurrentChannel;

        if (channel is null)
        {
            ShowError("Select a channel first.");
            return;
        }

        if (text.Length == 0)
        {
            return;
        }

        int? replyToMessageId = _replyTarget?.Id;
        _input = string.Empty;
        _draftsByChannel.Remove(channel.Id);
        _sentHistory.Add(text);

        if (_sentHistory.Count > 100)
        {
            _sentHistory.RemoveAt(0);
        }

        _historyCursor = -1;
        _draft = "";
        CloseMentionSuggestions();
        _mentionDismissed = false;
        CancelTyping();

        try
        {
            MessageDto sent = await _api.SendMessageAsync(
                channel.Id, text, replyToMessageId);
            ApplyIncomingMessage(sent);
            _replyTarget = null;
            _messageIndex = _messages.Count - 1;
            _followTail = true;
            _ensureSelectionVisible = false;
        }
        catch (Exception ex)
        {
            _input = text;
            ShowError("Send failed: " + Friendly(ex));
        }
    }

    private void ApplyReactionEvent(MessageReactionsEvent reactionEvent)
    {
        if (CurrentChannel?.Id != reactionEvent.ChannelId)
        {
            return;
        }

        int index = _messages.FindIndex(
            message => message.Id == reactionEvent.MessageId);

        if (index < 0)
        {
            return;
        }

        MessageDto message = _messages[index];
        List<string> mine = message.MyReactions?.ToList() ?? [];

        if (string.Equals(
                reactionEvent.UserId,
                _session.User.UserId,
                StringComparison.Ordinal))
        {
            if (reactionEvent.IsAdded)
            {
                if (!mine.Contains(reactionEvent.Emoji))
                {
                    mine.Add(reactionEvent.Emoji);
                }
            }
            else
            {
                mine.Remove(reactionEvent.Emoji);
            }
        }

        _messages[index] = message with
        {
            Reactions = reactionEvent.Reactions,
            MyReactions = mine
        };

        _dirty = true;
    }

    private void ApplyTypingEvent(UserTypingEvent typingEvent)
    {
        if (CurrentChannel?.Id != typingEvent.ChannelId ||
            string.Equals(
                typingEvent.UserId,
                _session.User.UserId,
                StringComparison.Ordinal))
        {
            return;
        }

        if (typingEvent.IsTyping)
        {
            string displayName = string.IsNullOrWhiteSpace(typingEvent.DisplayName)
                ? typingEvent.UserId
                : typingEvent.DisplayName;

            _typingUsers[typingEvent.UserId] = new TypingUser(
                displayName,
                DateTime.UtcNow.AddSeconds(6));
        }
        else
        {
            _typingUsers.Remove(typingEvent.UserId);
        }

        _dirty = true;
    }

    private void ExpireTypingUsers()
    {
        if (_typingUsers.Count == 0)
        {
            return;
        }

        DateTime now = DateTime.UtcNow;
        List<string> expired = _typingUsers
            .Where(pair => pair.Value.ExpiresAt <= now)
            .Select(pair => pair.Key)
            .ToList();

        if (expired.Count == 0)
        {
            return;
        }

        foreach (string userId in expired)
        {
            _typingUsers.Remove(userId);
        }

        _dirty = true;
    }

    private void SignalTyping()
    {
        ChannelDto? channel = CurrentChannel;

        if (channel is null ||
            _hub is not { State: HubConnectionState.Connected })
        {
            return;
        }

        DateTime now = DateTime.UtcNow;

        if (!_typingSent ||
            _typingChannelId != channel.Id ||
            now - _typingSentAt > TimeSpan.FromSeconds(3))
        {
            _typingSent = true;
            _typingChannelId = channel.Id;
            _typingSentAt = now;
            _ = SendTypingStateAsync(channel.Id, true);
        }

        _typingStopCts?.Cancel();
        _typingStopCts?.Dispose();
        _typingStopCts = new CancellationTokenSource();
        _ = StopTypingAfterDelayAsync(channel.Id, _typingStopCts.Token);
    }

    private void CancelTyping()
    {
        _typingStopCts?.Cancel();
        _typingStopCts?.Dispose();
        _typingStopCts = null;

        if (!_typingSent)
        {
            return;
        }

        int channelId = _typingChannelId;
        _typingSent = false;
        _typingChannelId = -1;

        if (channelId > 0)
        {
            _ = SendTypingStateAsync(channelId, false);
        }
    }

    private async Task SendTypingStateAsync(int channelId, bool isTyping)
    {
        try
        {
            if (_hub is { State: HubConnectionState.Connected })
            {
                await _hub.SendAsync("Typing", channelId, isTyping);
            }
        }
        catch
        {
        }
    }

    private async Task StopTypingAfterDelayAsync(
        int channelId,
        CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(4), ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (!_typingSent || _typingChannelId != channelId)
        {
            return;
        }

        _typingSent = false;
        _typingChannelId = -1;
        await SendTypingStateAsync(channelId, false);
    }

    private void ApplyIncomingMessage(MessageDto message)
    {
        if (CurrentChannel?.Id != message.ChannelId)
        {
            return;
        }

        if (_messages.Any(existing => existing.Id == message.Id))
        {
            return;
        }

        int index = _messages.Count;
        while (index > 0 && _messages[index - 1].Id > message.Id)
        {
            index--;
        }

        _messages.Insert(index, message);
        MarkChannelRead(message.ChannelId, message.Id);
        RequestAvatarLoad(message.AvatarUrl, ChatAvatarWidth);

        if (IsImageAttachment(message))
        {
            RequestAvatarLoad(message.AttachmentUrl, AttachmentPreviewWidth);
        }

        if (_followTail)
        {
            _messageIndex = _messages.Count - 1;
        }
        else if (_messageIndex >= index)
        {
            _messageIndex++;
        }

        if (_showSearch)
        {
            UpdateSearchResults();
        }

        _dirty = true;
    }

    private void MarkChannelRead(int channelId, int messageId)
    {
        if (messageId <= 0)
        {
            return;
        }

        if (!_lastReadByChannel.TryGetValue(channelId, out int lastRead) ||
            messageId > lastRead)
        {
            _lastReadByChannel[channelId] = messageId;
        }

        if (_unreadByChannel.Remove(channelId))
        {
            UpdateWindowTitle();
            _dirty = true;
        }
    }

    private void ScheduleUnreadPoll()
    {
        if (_unreadPollInFlight || _unreadPollCts.IsCancellationRequested)
        {
            return;
        }

        int? currentChannelId = CurrentChannel?.Id;
        Guid? currentServerId = CurrentServer?.Id;

        List<(Guid ServerId, string ServerName, int ChannelId, string ChannelName)>
            targets = [];
        List<(Guid ServerId, string ServerName)> undiscovered = [];

        foreach (ServerDto server in _servers)
        {
            if (!_channelsByServer.TryGetValue(server.Id, out List<ChannelDto>? channels))
            {
                undiscovered.Add((server.Id, server.Name));
                continue;
            }

            foreach (ChannelDto channel in channels)
            {
                if (server.Id == currentServerId &&
                    channel.Id == currentChannelId)
                {
                    continue;
                }

                targets.Add((
                    server.Id,
                    server.Name,
                    channel.Id,
                    channel.Name));
            }
        }

        if (targets.Count == 0 && undiscovered.Count == 0)
        {
            return;
        }

        string userName = _session.User.UserName;
        string userId = _session.User.UserId;
        CancellationToken token = _unreadPollCts.Token;
        _unreadPollInFlight = true;

        _ = Task.Run(async () =>
        {
            try
            {
                Regex mentionRegex = CreateMentionRegex(userName);
                List<UnreadServerChannels> discovered = [];
                List<UnreadChannelSnapshot> snapshots = [];

                foreach ((Guid serverId, string serverName) in undiscovered)
                {
                    if (token.IsCancellationRequested)
                    {
                        return;
                    }

                    try
                    {
                        List<ChannelDto> channels =
                            await _api.GetChannelsAsync(serverId, token);

                        discovered.Add(new UnreadServerChannels(
                            serverId,
                            channels));

                        foreach (ChannelDto channel in channels)
                        {
                            if (serverId == currentServerId &&
                                channel.Id == currentChannelId)
                            {
                                continue;
                            }

                            targets.Add((
                                serverId,
                                serverName,
                                channel.Id,
                                channel.Name));
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    catch
                    {
                    }
                }

                foreach ((Guid serverId, string serverName, int channelId, string channelName) in targets)
                {
                    if (token.IsCancellationRequested)
                    {
                        return;
                    }

                    try
                    {
                        List<MessageDto> messages = await _api.GetMessagesAsync(
                            channelId,
                            limit: UnreadPageSize,
                            ct: token);

                        List<UnreadMessageInfo> infos = messages
                            .Select(message => new UnreadMessageInfo(
                                message.Id,
                                string.Equals(
                                    message.UserId,
                                    userId,
                                    StringComparison.Ordinal),
                                !string.IsNullOrWhiteSpace(message.Content) &&
                                    mentionRegex.IsMatch(message.Content),
                                message.DisplayName))
                            .ToList();

                        snapshots.Add(new UnreadChannelSnapshot(
                            serverId,
                            serverName,
                            channelId,
                            channelName,
                            infos));
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    catch
                    {
                    }
                }

                _events.Writer.TryWrite(() =>
                {
                    ApplyUnreadPollResult(new UnreadPollResult(
                        discovered,
                        snapshots));
                    return Task.CompletedTask;
                });
            }
            catch
            {
            }
            finally
            {
                _unreadPollInFlight = false;
            }
        });
    }

    private void ApplyUnreadPollResult(UnreadPollResult result)
    {
        foreach (UnreadServerChannels discovered in result.DiscoveredChannels)
        {
            _channelsByServer[discovered.ServerId] =
                [.. discovered.Channels];
        }

        ApplyUnreadSnapshots(result.Snapshots);

        if (_showQuickSwitch && result.DiscoveredChannels.Count > 0)
        {
            UpdateQuickSwitchResults();
        }
    }

    private void ApplyUnreadSnapshots(
        IReadOnlyList<UnreadChannelSnapshot> snapshots)
    {
        int? currentChannelId = CurrentChannel?.Id;
        bool changed = false;
        (string Channel, string Server, string Author)? newMention = null;

        foreach (UnreadChannelSnapshot snapshot in snapshots)
        {
            int latestId = snapshot.Messages.Count == 0
                ? 0
                : snapshot.Messages.Max(message => message.Id);

            if (snapshot.ChannelId == currentChannelId)
            {
                if (latestId > 0)
                {
                    MarkChannelRead(snapshot.ChannelId, latestId);
                }

                continue;
            }

            if (!_lastReadByChannel.TryGetValue(snapshot.ChannelId, out int baseline))
            {
                _lastReadByChannel[snapshot.ChannelId] = latestId;

                if (_unreadByChannel.Remove(snapshot.ChannelId))
                {
                    changed = true;
                }

                continue;
            }

            List<UnreadMessageInfo> unread = snapshot.Messages
                .Where(message => message.Id > baseline && !message.IsMine)
                .ToList();

            _unreadByChannel.TryGetValue(
                snapshot.ChannelId,
                out ChannelUnread? previous);

            if (unread.Count == 0)
            {
                if (_unreadByChannel.Remove(snapshot.ChannelId))
                {
                    changed = true;
                }

                continue;
            }

            bool mentioned = unread.Any(message => message.MentionsMe);
            ChannelUnread next = new(unread.Count, mentioned);

            if (previous != next)
            {
                _unreadByChannel[snapshot.ChannelId] = next;
                changed = true;
            }

            if (mentioned &&
                previous is not { Mentioned: true } &&
                newMention is null)
            {
                UnreadMessageInfo? firstMention =
                    unread.FirstOrDefault(message => message.MentionsMe);

                if (firstMention is not null)
                {
                    newMention = (
                        snapshot.ChannelName,
                        snapshot.ServerName,
                        firstMention.Author);
                }
            }
        }

        if (changed)
        {
            UpdateWindowTitle();
            _dirty = true;
        }

        if (newMention is { } mention)
        {
            SetStatus(
                $"Mention from {mention.Author} in #{mention.Channel} " +
                $"on {mention.Server}. Press u to jump.",
                seconds: 12);
        }
    }

    private ChannelUnread GetServerUnread(Guid serverId)
    {
        int count = 0;
        bool mentioned = false;

        if (_channelsByServer.TryGetValue(serverId, out List<ChannelDto>? channels))
        {
            foreach (ChannelDto channel in channels)
            {
                if (!_unreadByChannel.TryGetValue(
                        channel.Id,
                        out ChannelUnread? unread))
                {
                    continue;
                }

                count += unread.Count;
                mentioned |= unread.Mentioned;
            }
        }

        return new ChannelUnread(count, mentioned);
    }

    private void UpdateWindowTitle()
    {
        int unread = _unreadByChannel.Values.Sum(state => state.Count);
        int mentions = _unreadByChannel.Values.Count(state => state.Mentioned);
        string title = "Qwek";

        if (unread > 0)
        {
            title = $"Qwek — {FormatUnreadCount(unread)} unread";

            if (mentions > 0)
            {
                title += $" ({mentions} mention{(mentions == 1 ? "" : "s")})";
            }
        }

        try
        {
            Console.Title = title;
        }
        catch (IOException)
        {
        }
    }

    private async Task JumpToNextUnreadChannelAsync()
    {
        if (_servers.Count == 0)
        {
            SetStatus("No unread channels.");
            return;
        }

        int baseServerIndex = _serverIndex >= 0 ? _serverIndex : 0;

        for (int serverOffset = 0; serverOffset < _servers.Count; serverOffset++)
        {
            int serverIndex = (baseServerIndex + serverOffset) % _servers.Count;
            ServerDto server = _servers[serverIndex];

            IReadOnlyList<ChannelDto> channels;
            if (serverIndex == _serverIndex)
            {
                channels = _channels;
            }
            else if (_channelsByServer.TryGetValue(
                         server.Id,
                         out List<ChannelDto>? cached))
            {
                channels = cached;
            }
            else
            {
                continue;
            }

            if (channels.Count == 0)
            {
                continue;
            }

            int startChannel = serverIndex == _serverIndex && _channelIndex >= 0
                ? _channelIndex + 1
                : 0;

            for (int channelOffset = 0; channelOffset < channels.Count; channelOffset++)
            {
                int channelIndex = (startChannel + channelOffset) % channels.Count;
                ChannelDto channel = channels[channelIndex];

                if (!_unreadByChannel.TryGetValue(
                        channel.Id,
                        out ChannelUnread? unread) ||
                    unread.Count <= 0)
                {
                    continue;
                }

                if (serverIndex == _serverIndex)
                {
                    int index = _channels.FindIndex(
                        item => item.Id == channel.Id);

                    if (index < 0)
                    {
                        continue;
                    }

                    await SelectChannelAsync(index, force: true);
                }
                else
                {
                    _serverIndex = serverIndex;
                    await LoadChannelsAsync(channel.Id);
                }

                SetStatus(
                    $"#{channel.Name} — {FormatUnreadCount(unread.Count)} unread.");
                return;
            }
        }

        SetStatus("No unread channels.");
    }

    private bool MentionsMe(string? content) =>
        !string.IsNullOrWhiteSpace(content) && MentionRegex().IsMatch(content);

    private Regex MentionRegex()
    {
        string userName = _session.User.UserName;

        if (_mentionRegex is null ||
            !string.Equals(_mentionUserName, userName, StringComparison.Ordinal))
        {
            _mentionUserName = userName;
            _mentionRegex = CreateMentionRegex(userName);
        }

        return _mentionRegex;
    }

    private static Regex CreateMentionRegex(string userName)
    {
        string names = string.IsNullOrWhiteSpace(userName)
            ? "(?:everyone|here)"
            : $"(?:{Regex.Escape(userName)}|everyone|here)";

        return new Regex(
            $@"(?<![\p{{L}}\p{{N}}_-])@{names}(?![\p{{L}}\p{{N}}_-])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private string HighlightMentions(string text)
    {
        Regex regex = MentionRegex();
        MatchCollection matches = regex.Matches(text);

        if (matches.Count == 0)
        {
            return Escape(text);
        }

        StringBuilder builder = new();
        int offset = 0;

        foreach (Match match in matches)
        {
            if (match.Index > offset)
            {
                builder.Append(Escape(text[offset..match.Index]));
            }

            builder
                .Append("[black on ")
                .Append(AmberHex)
                .Append(']')
                .Append(Escape(match.Value))
                .Append("[/]");

            offset = match.Index + match.Length;
        }

        if (offset < text.Length)
        {
            builder.Append(Escape(text[offset..]));
        }

        return builder.ToString();
    }

    private static string FormatUnreadCount(int count) =>
        count > 99 ? "99+" : count.ToString(CultureInfo.InvariantCulture);

    private void ApplyDeletedMessage(int messageId)
    {
        int index = _messages.FindIndex(message => message.Id == messageId);
        if (index < 0)
        {
            return;
        }

        _messages.RemoveAt(index);

        if (_messages.Count == 0)
        {
            _messageIndex = -1;
            _followTail = true;
            _viewStart = 0;

            if (_showSearch)
            {
                UpdateSearchResults();
            }

            _dirty = true;
            return;
        }

        if (_messageIndex >= index)
        {
            _messageIndex = Math.Max(0, _messageIndex - 1);
        }

        _messageIndex = Math.Min(_messageIndex, _messages.Count - 1);

        if (_followTail)
        {
            _messageIndex = _messages.Count - 1;
        }

        if (_showSearch)
        {
            UpdateSearchResults();
        }

        _ensureSelectionVisible = true;
        _dirty = true;
    }

    private void OpenModal(
        ModalKind kind,
        string title,
        string hint,
        string initial = "",
        int messageId = 0,
        string attachmentPath = "")
    {
        _modal = new ModalState
        {
            Kind = kind,
            Title = title,
            Hint = hint,
            Buffer = initial,
            MessageId = messageId,
            AttachmentPath = attachmentPath
        };

        _dirty = true;
    }

    private void OpenChannelModal()
    {
        if (CurrentServer is null)
        {
            ShowError("Select a server first.");
            return;
        }

        OpenModal(ModalKind.CreateChannel, "New channel", "Channel name");
    }

    private void StartReply()
    {
        if (CurrentMessage is not { } message)
        {
            return;
        }

        _replyTarget = message;
        _focus = Pane.Input;
        SetStatus(
            $"Replying to {message.DisplayName}. Esc cancels the reply.");
        _dirty = true;
    }

    private void CancelReply()
    {
        if (_replyTarget is null)
        {
            return;
        }

        _replyTarget = null;
        SetStatus("Reply cancelled.");
        _dirty = true;
    }

    private void OpenEditModal()
    {
        if (_focus != Pane.Messages || CurrentMessage is not { } message)
        {
            return;
        }

        if (message.UserId != _session.User.UserId)
        {
            ShowError("You can only edit your own messages.");
            return;
        }

        OpenModal(
            ModalKind.EditMessage,
            "Edit message",
            "New content",
            message.Content,
            message.Id);
    }

    private void OpenDeleteModal()
    {
        if (_focus != Pane.Messages || CurrentMessage is not { } message)
        {
            return;
        }

        OpenModal(
            ModalKind.ConfirmDelete,
            "Delete message",
            "y / n",
            messageId: message.Id);
    }

    private async Task HandleModalKeyAsync(ConsoleKeyInfo key)
    {
        ModalState modal = _modal!;

        if (key.Key == ConsoleKey.Escape)
        {
            _modal = null;
            return;
        }

        if (modal.Kind == ModalKind.ConfirmDelete)
        {
            char choice = char.ToLowerInvariant(key.KeyChar);

            if (choice == 'y')
            {
                await DeleteMessageAsync(modal.MessageId);
            }
            else if (choice == 'n' || key.Key == ConsoleKey.Enter)
            {
                _modal = null;
            }

            return;
        }

        if (key.Key == ConsoleKey.Enter)
        {
            await SubmitModalAsync(modal);
            return;
        }

        if (key.Key == ConsoleKey.Backspace)
        {
            if (modal.Buffer.Length > 0)
            {
                modal.Buffer = modal.Buffer[..^1];
            }

            return;
        }

        char character = key.KeyChar;
        if (character == '\0' || char.IsControl(character))
        {
            return;
        }

        if (modal.Buffer.Length < MaxInputLength)
        {
            modal.Buffer += character;
        }
    }

    private async Task SubmitModalAsync(ModalState modal)
    {
        string value = modal.Buffer.Trim();

        switch (modal.Kind)
        {
            case ModalKind.CreateServer:
                if (value.Length == 0)
                {
                    ShowError("Server name is required.");
                    return;
                }

                _modal = null;
                await CreateServerAsync(value);
                break;

            case ModalKind.CreateChannel:
                if (CurrentServer is not { } server)
                {
                    _modal = null;
                    ShowError("Select a server first.");
                    return;
                }

                if (value.Length == 0)
                {
                    ShowError("Channel name is required.");
                    return;
                }

                _modal = null;
                await CreateChannelAsync(server, value);
                break;

            case ModalKind.JoinServer:
                if (!Guid.TryParse(value, out Guid serverId))
                {
                    ShowError("Enter a valid server ID (GUID).");
                    return;
                }

                _modal = null;
                await JoinServerAsync(serverId);
                break;

            case ModalKind.EditMessage:
                if (value.Length == 0)
                {
                    ShowError("Message cannot be empty.");
                    return;
                }

                _modal = null;
                await EditMessageAsync(modal.MessageId, value);
                break;

            case ModalKind.EditDisplayName:
                if (value.Length == 0)
                {
                    ShowError("Display name is required.");
                    return;
                }

                _modal = null;
                await UpdateDisplayNameAsync(value);
                break;

            case ModalKind.EditUsername:
                if (value.Length == 0)
                {
                    ShowError("Username is required.");
                    return;
                }

                _modal = null;
                await UpdateUsernameAsync(value);
                break;

            case ModalKind.SendAttachment:
                _modal = null;
                await SendAttachmentAsync(modal.AttachmentPath, value);
                break;
        }
    }

    private async Task<bool> SendAttachmentAsync(string filePath, string content)
    {
        ChannelDto? channel = CurrentChannel;

        if (channel is null)
        {
            ShowError("Select a channel first.");
            return false;
        }

        try
        {
            SetStatus("Uploading file...", seconds: 60);
            MessageDto sent = await _api.SendAttachmentAsync(
                channel.Id, filePath, content);
            ApplyIncomingMessage(sent);
            _messageIndex = _messages.Count - 1;
            _followTail = true;
            _ensureSelectionVisible = false;
            _focus = Pane.Messages;
            SetStatus("File sent.");
            return true;
        }
        catch (Exception ex)
        {
            ShowError("Could not send file: " + Friendly(ex));
            return false;
        }
    }

    private void OpenAttachment()
    {
        if (_focus != Pane.Messages || CurrentMessage is not { } message)
        {
            ShowError("Select a message with an attachment.");
            return;
        }

        if (string.IsNullOrWhiteSpace(message.AttachmentUrl))
        {
            ShowError("This message has no attachment.");
            return;
        }

        string name = string.IsNullOrWhiteSpace(message.AttachmentName)
            ? Path.GetFileName(message.AttachmentUrl)
            : message.AttachmentName;
        name = Path.GetFileName(name);

        foreach (char invalid in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalid, '_');
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            name = "attachment";
        }

        string directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads");
        string destination = UniquePath(directory, name);

        SetStatus($"Downloading {name}...", seconds: 60);
        _ = DownloadAttachmentAsync(message.AttachmentUrl, destination);
    }

    private async Task DownloadAttachmentAsync(
        string url,
        string destination)
    {
        bool saved = await _api.DownloadAsync(url, destination);

        _events.Writer.TryWrite(() =>
        {
            if (!saved)
            {
                ShowError("Could not download the attachment.");
                return Task.CompletedTask;
            }

            bool opened = TryOpenFile(destination);
            SetStatus(
                opened
                    ? $"Opened {Path.GetFileName(destination)}"
                    : $"Saved to {destination}",
                seconds: 15);
            return Task.CompletedTask;
        });
    }

    private static bool TryOpenFile(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string UniquePath(string directory, string fileName)
    {
        string path = Path.Combine(directory, fileName);

        if (!File.Exists(path))
        {
            return path;
        }

        string name = Path.GetFileNameWithoutExtension(fileName);
        string extension = Path.GetExtension(fileName);

        for (int index = 1; ; index++)
        {
            path = Path.Combine(directory, $"{name}-{index}{extension}");

            if (!File.Exists(path))
            {
                return path;
            }
        }
    }

    private async Task CreateServerAsync(string name)
    {
        try
        {
            SetStatus("Creating server...", seconds: 20);
            ServerDto server = await _api.CreateServerAsync(name);

            try
            {
                await _api.CreateChannelAsync(
                    server.Id, "general", "Welcome to " + server.Name);
            }
            catch (ApiException)
            {
            }

            List<ServerDto> servers = await _api.GetServersAsync();
            _servers.Clear();
            _servers.AddRange(servers);

            int index = _servers.FindIndex(item => item.Id == server.Id);
            _serverIndex = index >= 0 ? index : Math.Max(0, _servers.Count - 1);
            await LoadChannelsAsync();
            _focus = Pane.Channels;
            SetStatus($"Server \"{server.Name}\" created.");
        }
        catch (Exception ex)
        {
            ShowError("Could not create server: " + Friendly(ex));
        }
    }

    private async Task CreateChannelAsync(ServerDto server, string name)
    {
        try
        {
            SetStatus("Creating channel...", seconds: 20);
            ChannelDto created = await _api.CreateChannelAsync(
                server.Id, name, topic: null);

            SaveCurrentDraft();
            List<ChannelDto> channels = await _api.GetChannelsAsync(server.Id);
            _channels.Clear();
            _channels.AddRange(channels);
            _channelsByServer[server.Id] = [.. _channels];

            int index = _channels.FindIndex(channel => channel.Id == created.Id);
            _channelIndex = index >= 0 ? index : 0;
            LoadDraftForCurrentChannel();
            await LoadMessagesAsync();
            await JoinHubChannelAsync();
            _focus = Pane.Messages;
            SetStatus($"Channel #{created.Name} created.");
        }
        catch (Exception ex)
        {
            ShowError("Could not create channel: " + Friendly(ex));
        }
    }

    private async Task ShowServerDetailsAsync()
    {
        if (_showServerDetails)
        {
            _showServerDetails = false;
            return;
        }

        ServerDto? server = CurrentServer;

        if (server is null)
        {
            ShowError("Select a server first.");
            return;
        }

        try
        {
            List<ServerMemberDto> members = await _api.GetMembersAsync(server.Id);
            _membersByServer[server.Id] = members;
            _serverMembers.Clear();
            _serverMembers.AddRange(members);
            _serverDetailScroll = 0;
            _serverDetailMaxScroll = 0;
            _showServerDetails = true;
            _help = false;
            _dirty = true;
        }
        catch (Exception ex)
        {
            ShowError("Could not load server details: " + Friendly(ex));
        }
    }

    private void HandleServerDetailKey(ConsoleKeyInfo key)
    {
        switch (key.Key)
        {
            case ConsoleKey.UpArrow:
                ScrollServerDetails(-1);
                return;
            case ConsoleKey.DownArrow:
                ScrollServerDetails(1);
                return;
            case ConsoleKey.PageUp:
                ScrollServerDetails(-1, page: true);
                return;
            case ConsoleKey.PageDown:
                ScrollServerDetails(1, page: true);
                return;
            case ConsoleKey.Home:
                _serverDetailScroll = 0;
                _dirty = true;
                return;
            case ConsoleKey.End:
                _serverDetailScroll = _serverDetailMaxScroll;
                _dirty = true;
                return;
        }

        if (char.ToLowerInvariant(key.KeyChar) == 'v')
        {
            _showServerDetails = false;
        }
    }

    private void ScrollServerDetails(int direction, bool page = false)
    {
        int step = page ? Math.Max(1, _serverDetailPageHeight - 1) : 1;
        _serverDetailScroll = Math.Clamp(
            _serverDetailScroll + direction * step,
            0,
            _serverDetailMaxScroll);
        _dirty = true;
    }

    private async Task ShowProfileAsync()
    {
        if (_showProfile)
        {
            _showProfile = false;
            return;
        }

        try
        {
            ApplyProfile(await _api.GetProfileAsync());
            _showProfile = true;
            _help = false;
            _showServerDetails = false;
            SetStatus("Profile loaded.");
        }
        catch (Exception ex)
        {
            ShowError("Could not load profile: " + Friendly(ex));
        }
    }

    private async Task HandleProfileKeyAsync(ConsoleKeyInfo key)
    {
        switch (char.ToLowerInvariant(key.KeyChar))
        {
            case 'p':
                _showProfile = false;
                break;
            case '?':
                _help = !_help;

                if (_help)
                {
                    _helpScroll = 0;
                }

                break;
            case 'd':
                if (_profile is { } displayNameProfile)
                {
                    OpenModal(
                        ModalKind.EditDisplayName,
                        "Edit display name",
                        "Display name",
                        displayNameProfile.DisplayName);
                }
                break;
            case 'u':
                if (_profile is { } userNameProfile)
                {
                    OpenModal(
                        ModalKind.EditUsername,
                        "Change username",
                        "Username",
                        userNameProfile.UserName);
                }
                break;
            case 'a':
                OpenFilePicker(FilePickerKind.Avatar);
                break;
            case 'x':
                if (!string.IsNullOrWhiteSpace(_profile?.AvatarUrl))
                {
                    await ClearAvatarAsync();
                }
                break;
        }
    }

    private void ApplyProfile(UserProfileDto profile)
    {
        _profile = profile;
        _session = _session with
        {
            User = _session.User with
            {
                UserName = profile.UserName,
                DisplayName = profile.DisplayName
            }
        };

        for (int i = 0; i < _messages.Count; i++)
        {
            if (_messages[i].UserId == profile.UserId)
            {
                _messages[i] = _messages[i] with
                {
                    DisplayName = profile.DisplayName,
                    AvatarUrl = profile.AvatarUrl
                };
            }
        }

        for (int i = 0; i < _serverMembers.Count; i++)
        {
            if (_serverMembers[i].UserId == profile.UserId)
            {
                _serverMembers[i] = _serverMembers[i] with
                {
                    UserName = profile.UserName,
                    DisplayName = profile.DisplayName
                };
            }
        }

        foreach (List<ServerMemberDto> members in _membersByServer.Values)
        {
            for (int i = 0; i < members.Count; i++)
            {
                if (members[i].UserId == profile.UserId)
                {
                    members[i] = members[i] with
                    {
                        UserName = profile.UserName,
                        DisplayName = profile.DisplayName
                    };
                }
            }
        }

        RequestAvatarLoad(profile.AvatarUrl, ProfileAvatarWidth);
        RequestAvatarLoad(profile.AvatarUrl, ChatAvatarWidth);
        _dirty = true;
    }

    private void RequestAvatarLoads()
    {
        foreach (MessageDto message in _messages)
        {
            RequestAvatarLoad(message.AvatarUrl, ChatAvatarWidth);

            if (IsImageAttachment(message))
            {
                RequestAvatarLoad(
                    message.AttachmentUrl, AttachmentPreviewWidth);
            }
        }
    }

    private static bool IsImageAttachment(MessageDto message)
    {
        if (string.IsNullOrWhiteSpace(message.AttachmentUrl))
        {
            return false;
        }

        string extension = Path.GetExtension(
            message.AttachmentName ?? message.AttachmentUrl);

        return PreviewableImageExtensions.Contains(
            extension, StringComparer.OrdinalIgnoreCase);
    }

    private void RequestAvatarLoad(string? url, int width)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        string key = $"{width}|{url}";

        if (!_avatarRequests.Add(key))
        {
            return;
        }

        _ = LoadAvatarAsync(url, width, key);
    }

    private async Task LoadAvatarAsync(string url, int width, string key)
    {
        byte[]? data = await _api.GetImageAsync(url);
        int maxHeight = width <= ChatAvatarWidth ? 4 : 6;
        AvatarArt? art = data is null
            ? null
            : AvatarArtFactory.Create(data, width, maxHeight);

        _events.Writer.TryWrite(() =>
        {
            _avatarCache[key] = art;
            _dirty = true;
            return Task.CompletedTask;
        });
    }

    private AvatarArt? GetAvatarArt(string? url, int width)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        return _avatarCache.TryGetValue($"{width}|{url}", out AvatarArt? art)
            ? art
            : null;
    }

    private async Task UpdateDisplayNameAsync(string displayName)
    {
        try
        {
            SetStatus("Updating display name...", seconds: 20);
            ApplyProfile(await _api.UpdateDisplayNameAsync(displayName));
            SetStatus("Display name updated.");
        }
        catch (Exception ex)
        {
            ShowError("Could not update display name: " + Friendly(ex));
        }
    }

    private async Task UpdateUsernameAsync(string username)
    {
        try
        {
            SetStatus("Updating username...", seconds: 20);
            ApplyProfile(await _api.UpdateUsernameAsync(username));
            SetStatus("Username updated.");
        }
        catch (Exception ex)
        {
            ShowError("Could not update username: " + Friendly(ex));
        }
    }

    private void OpenFilePicker(FilePickerKind kind)
    {
        _filePickerKind = kind;
        string start = kind == FilePickerKind.Avatar
            ? Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)
            : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

        if (!Directory.Exists(start))
        {
            start = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        if (!Directory.Exists(start))
        {
            start = Directory.GetCurrentDirectory();
        }

        if (TryLoadAvatarDirectory(start))
        {
            _avatarSearchActive = false;
            _avatarSearchQuery = "";
            _avatarSearchVersion++;
            _avatarSearchBusy = false;
            _avatarSearchCts?.Cancel();
            _showAvatarPicker = true;
            _help = false;
            _showServerDetails = false;
            _dirty = true;
        }
    }

    private void CloseAvatarPicker()
    {
        _showAvatarPicker = false;
        _avatarSearchActive = false;
        _avatarSearchQuery = "";
        _avatarSearchVersion++;
        _avatarSearchBusy = false;
        _avatarSearchCts?.Cancel();
    }

    private void ExitAvatarSearch()
    {
        _avatarSearchActive = false;
        _avatarSearchQuery = "";
        _avatarSearchVersion++;
        _avatarSearchBusy = false;
        _avatarSearchCts?.Cancel();
        TryLoadAvatarDirectory(_avatarPickerDirectory);
    }

    private void BeginAvatarSearch(string query)
    {
        _avatarSearchActive = true;
        _avatarSearchQuery = query;
        _avatarPickerIndex = 0;

        if (query.Length == 0)
        {
            _avatarSearchVersion++;
            _avatarSearchBusy = false;
            _avatarSearchCts?.Cancel();
            TryLoadAvatarDirectory(_avatarPickerDirectory);
            return;
        }

        ScheduleAvatarSearch();
    }

    private void SetAvatarSearchQuery(string query)
    {
        _avatarSearchQuery = query;
        _avatarPickerIndex = 0;

        if (query.Length == 0)
        {
            _avatarSearchVersion++;
            _avatarSearchBusy = false;
            _avatarSearchCts?.Cancel();
            TryLoadAvatarDirectory(_avatarPickerDirectory);
            return;
        }

        ScheduleAvatarSearch();
    }

    private void ScheduleAvatarSearch()
    {
        int version = ++_avatarSearchVersion;
        string root = _avatarPickerDirectory;
        string query = _avatarSearchQuery;
        string[] extensions = CurrentPickerExtensions();

        _avatarSearchCts?.Cancel();
        CancellationTokenSource cts = new();
        _avatarSearchCts = cts;
        _avatarSearchBusy = true;
        _avatarPickerEntries.Clear();
        _dirty = true;

        Task.Run(() =>
        {
            List<FileSystemInfo> matches = FindPickerFiles(
                root, query, extensions, cts.Token);

            _events.Writer.TryWrite(() =>
            {
                if (version != _avatarSearchVersion)
                {
                    return Task.CompletedTask;
                }

                _avatarPickerEntries.Clear();
                _avatarPickerEntries.AddRange(matches);
                _avatarPickerIndex = 0;
                _avatarSearchBusy = false;
                _dirty = true;
                return Task.CompletedTask;
            });
        });
    }

    private static List<FileSystemInfo> FindPickerFiles(
        string root,
        string query,
        string[] extensions,
        CancellationToken ct)
    {
        const int limit = 300;
        List<FileSystemInfo> matches = [];

        try
        {
            EnumerationOptions options = new()
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                ReturnSpecialDirectories = false
            };

            foreach (string path in Directory.EnumerateFiles(root, "*", options))
            {
                ct.ThrowIfCancellationRequested();

                if (!extensions.Contains(
                        Path.GetExtension(path),
                        StringComparer.OrdinalIgnoreCase) ||
                    !Path.GetFileName(path).Contains(
                        query, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                matches.Add(new FileInfo(path));

                if (matches.Count >= limit)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            return [];
        }
        catch (Exception)
        {
        }

        matches.Sort((left, right) => string.Compare(
            left.FullName, right.FullName, StringComparison.OrdinalIgnoreCase));

        return matches;
    }

    private bool TryLoadAvatarDirectory(string path)
    {
        try
        {
            string fullPath = Path.GetFullPath(path);
            List<FileSystemInfo> entries = [];

            entries.AddRange(Directory
                .EnumerateDirectories(fullPath)
                .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
                .Select(item => (FileSystemInfo)new DirectoryInfo(item)));

            entries.AddRange(Directory
                .EnumerateFiles(fullPath)
                .Where(IsPickerFile)
                .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
                .Select(item => (FileSystemInfo)new FileInfo(item)));

            _avatarPickerDirectory = fullPath;
            _avatarPickerEntries.Clear();
            _avatarPickerEntries.AddRange(entries);
            _avatarPickerIndex = 0;
            _dirty = true;
            return true;
        }
        catch (Exception ex)
        {
            ShowError("Could not open folder: " + ex.Message);
            return false;
        }
    }

    private bool IsPickerFile(string path)
    {
        return CurrentPickerExtensions().Contains(
            Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
    }

    private string[] CurrentPickerExtensions()
    {
        return _filePickerKind == FilePickerKind.Attachment
            ? AttachmentFileExtensions
            : AvatarFileExtensions;
    }

    private async Task HandleAvatarPickerKeyAsync(ConsoleKeyInfo key)
    {
        switch (key.Key)
        {
            case ConsoleKey.UpArrow:
                MoveAvatarPicker(-1);
                return;
            case ConsoleKey.DownArrow:
                MoveAvatarPicker(1);
                return;
            case ConsoleKey.PageUp:
                MoveAvatarPicker(-10);
                return;
            case ConsoleKey.PageDown:
                MoveAvatarPicker(10);
                return;
            case ConsoleKey.Home:
                _avatarPickerIndex = 0;
                _dirty = true;
                return;
            case ConsoleKey.End:
                _avatarPickerIndex = Math.Max(0, _avatarPickerEntries.Count - 1);
                _dirty = true;
                return;
            case ConsoleKey.Backspace:
                if (_avatarSearchActive)
                {
                    if (_avatarSearchQuery.Length > 0)
                    {
                        SetAvatarSearchQuery(_avatarSearchQuery[..^1]);
                    }
                    else
                    {
                        ExitAvatarSearch();
                    }
                }
                else
                {
                    GoToParentAvatarDirectory();
                }

                return;
            case ConsoleKey.LeftArrow:
                if (_avatarSearchActive)
                {
                    ExitAvatarSearch();
                }
                else
                {
                    GoToParentAvatarDirectory();
                }

                return;
            case ConsoleKey.Enter:
                await ActivateAvatarPickerAsync();
                return;
        }

        char character = key.KeyChar;

        if (character == '\0' || char.IsControl(character))
        {
            return;
        }

        if (character == '/')
        {
            if (!_avatarSearchActive)
            {
                BeginAvatarSearch("");
            }

            return;
        }

        if (_avatarSearchActive)
        {
            SetAvatarSearchQuery(_avatarSearchQuery + character);
        }
        else
        {
            BeginAvatarSearch(character.ToString());
        }
    }

    private void MoveAvatarPicker(int direction)
    {
        if (_avatarPickerEntries.Count == 0)
        {
            return;
        }

        _avatarPickerIndex = Math.Clamp(
            _avatarPickerIndex + direction,
            0,
            _avatarPickerEntries.Count - 1);
        _dirty = true;
    }

    private void GoToParentAvatarDirectory()
    {
        DirectoryInfo? parent = Directory.GetParent(_avatarPickerDirectory);

        if (parent is null)
        {
            return;
        }

        string currentName = Path.GetFileName(
            _avatarPickerDirectory.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar));

        if (!TryLoadAvatarDirectory(parent.FullName))
        {
            return;
        }

        int index = _avatarPickerEntries.FindIndex(entry =>
            entry is DirectoryInfo &&
            string.Equals(entry.Name, currentName, StringComparison.OrdinalIgnoreCase));

        if (index >= 0)
        {
            _avatarPickerIndex = index;
        }
    }

    private async Task ActivateAvatarPickerAsync()
    {
        if (_avatarPickerIndex < 0 ||
            _avatarPickerIndex >= _avatarPickerEntries.Count)
        {
            return;
        }

        FileSystemInfo entry = _avatarPickerEntries[_avatarPickerIndex];

        if (entry is DirectoryInfo)
        {
            TryLoadAvatarDirectory(entry.FullName);
            return;
        }

        if (_filePickerKind == FilePickerKind.Attachment)
        {
            CloseAvatarPicker();
            OpenAttachmentModal(entry.FullName);
            return;
        }

        await UploadAvatarAsync(entry.FullName);
    }

    private void OpenAttachmentPicker()
    {
        if (CurrentChannel is null)
        {
            ShowError("Select a channel first.");
            return;
        }

        OpenFilePicker(FilePickerKind.Attachment);
    }

    private void OpenAttachmentModal(string filePath)
    {
        OpenModal(
            ModalKind.SendAttachment,
            "Send file",
            "Caption (optional)",
            attachmentPath: filePath);
    }

    private async Task UploadAvatarAsync(string filePath)
    {
        try
        {
            SetStatus("Uploading avatar...", seconds: 30);
            ApplyProfile(await _api.UploadAvatarAsync(filePath));
            CloseAvatarPicker();
            _showProfile = true;
            SetStatus("Avatar updated.");
        }
        catch (Exception ex)
        {
            ShowError("Could not upload avatar: " + Friendly(ex));
        }
    }

    private async Task ClearAvatarAsync()
    {
        try
        {
            SetStatus("Removing avatar...", seconds: 20);
            ApplyProfile(await _api.UpdateAvatarAsync(null));
            SetStatus("Avatar removed.");
        }
        catch (Exception ex)
        {
            ShowError("Could not remove avatar: " + Friendly(ex));
        }
    }

    private async Task JoinServerAsync(Guid serverId)
    {
        try
        {
            SetStatus("Joining server...", seconds: 20);
            await _api.JoinServerAsync(serverId);

            List<ServerDto> servers = await _api.GetServersAsync();
            _servers.Clear();
            _servers.AddRange(servers);

            int index = _servers.FindIndex(server => server.Id == serverId);
            if (index < 0)
            {
                ShowError("Joined, but the server was not returned by the API.");
                return;
            }

            _serverIndex = index;
            await LoadChannelsAsync();
            _focus = Pane.Channels;
            SetStatus($"Joined server \"{_servers[index].Name}\".");
        }
        catch (Exception ex)
        {
            ShowError("Could not join server: " + Friendly(ex));
        }
    }

    private async Task EditMessageAsync(int messageId, string content)
    {
        try
        {
            await _api.EditMessageAsync(messageId, content);
            await ReloadMessagesQuietAsync();
            SetStatus("Message edited.");
        }
        catch (Exception ex)
        {
            ShowError("Could not edit message: " + Friendly(ex));
        }
    }

    private async Task DeleteMessageAsync(int messageId)
    {
        _modal = null;

        try
        {
            await _api.DeleteMessageAsync(messageId);
            ApplyDeletedMessage(messageId);
            SetStatus("Message deleted.");
        }
        catch (Exception ex)
        {
            ShowError("Could not delete message: " + Friendly(ex));
        }
    }

    private void SetStatus(string message, bool error = false, int seconds = 5)
    {
        _status = message.ReplaceLineEndings(" ");
        _statusIsError = error;
        _statusUntil = DateTime.UtcNow.AddSeconds(seconds);
        _dirty = true;
    }

    private void ShowError(string message) =>
        SetStatus(message, error: true, seconds: 8);

    private static T? Deserialize<T>(JsonElement element)
    {
        try
        {
            return element.Deserialize<T>(HubJson);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private static string Friendly(Exception exception)
    {
        Exception current = exception;

        while (current is AggregateException { InnerExceptions.Count: 1 } aggregate)
        {
            current = aggregate.InnerExceptions[0];
        }

        return current.Message;
    }

    public async ValueTask DisposeAsync()
    {
        _unreadPollCts.Cancel();
        _typingStopCts?.Cancel();
        _typingStopCts?.Dispose();

        if (_hub is not null)
        {
            try
            {
                await _hub.DisposeAsync();
            }
            catch
            {
            }
        }

        _events.Writer.TryComplete();
    }
}
