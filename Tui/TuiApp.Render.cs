using System.Text;
using Qwek.Api;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Qwek.Tui;

public sealed partial class TuiApp
{
    private const string AccentHex = "#7aa2f7";
    private const string MutedHex = "#565f89";
    private const string GreenHex = "#9ece6a";
    private const string RedHex = "#f7768e";
    private const string AmberHex = "#e0af68";
    private const string SelectionBgHex = "#292e42";
    private const string DividerHex = "#3b4261";

    private static readonly Color AccentColor = new(122, 162, 247);
    private static readonly Color BorderColor = new(59, 66, 97);
    private static readonly Color AmberColor = new(224, 175, 104);

    private sealed record ChatLine(
        string Markup,
        int MessageIndex,
        int PlainLength = 0);

    private static readonly string[] AvatarColors =
        [AccentHex, GreenHex, AmberHex, "#bb9af7", "#7dcfff", "#f7768e"];

    private IRenderable Render()
    {
        return Render(GetConsoleWidth(), GetConsoleHeight());
    }

    private IRenderable Render(int width, int height)
    {
        if (width < 64 || height < 18)
        {
            return new Panel(
                new Markup(
                    $"[yellow]Terminal too small[/] [grey]({width}x{height})[/]\n\n" +
                    "[grey]Qwek needs at least 64 columns and 18 rows.[/]"))
            {
                Header = new PanelHeader(" Qwek "),
                Border = BoxBorder.Rounded,
                BorderStyle = new Style(foreground: AmberColor),
                Padding = new Padding(1, 0, 1, 0),
                Expand = true
            };
        }

        int serverWidth = Math.Clamp(width / 6, 18, 24);
        int channelWidth = Math.Clamp(width / 4, 24, 34);
        int chatWidth = width - serverWidth - channelWidth;
        int composerHeight = GetComposerHeight();
        int panelContentHeight = height - 4 - composerHeight - 2;

        Layout root = new Layout("root")
            .SplitRows(
                new Layout("header").Size(3),
                new Layout("body").SplitColumns(
                    new Layout("servers").Size(serverWidth),
                    new Layout("channels").Size(channelWidth),
                    new Layout("chat")),
                new Layout("composer").Size(composerHeight),
                new Layout("footer").Size(1));

        root["header"].Update(RenderHeader(width));
        root["servers"].Update(RenderServers(serverWidth, panelContentHeight));
        root["channels"].Update(RenderChannels(channelWidth, panelContentHeight));
        root["chat"].Update(RenderChat(chatWidth, panelContentHeight));
        root["composer"].Update(RenderComposer(width, composerHeight));
        root["footer"].Update(RenderFooter(width));

        return root;
    }

    private IRenderable RenderHeader(int width)
    {
        int inner = Math.Max(1, width - 4);
        string serverName = CurrentServer?.Name
            ?? (_servers.Count == 0 ? "no servers yet" : "no server selected");
        string state = _live ? "live" : "offline";
        string dot = _live ? "●" : "○";
        string user = _session.User.DisplayName;

        string fullRight = $"{dot} {state}  ·  {user}";
        string shortRight = $"{dot} {state}";
        bool showUser = inner >= fullRight.Length + 20;
        string rightPlain = showUser ? fullRight : shortRight;

        const string leftPrefix = "Qwek  ›  ";
        int nameRoom = Math.Max(3, inner - rightPlain.Length - leftPrefix.Length - 1);
        string shownServer = Crop(serverName, nameRoom);
        string leftPlain = leftPrefix + shownServer;

        if (leftPlain.Length + rightPlain.Length + 1 > inner)
        {
            int allowed = Math.Max(0, inner - rightPlain.Length - 1 - leftPrefix.Length);
            shownServer = Crop(serverName, allowed);
            leftPlain = leftPrefix + shownServer;
        }

        int gap = Math.Max(1, inner - leftPlain.Length - rightPlain.Length);

        string leftMarkup =
            $"[bold {AccentHex}]Qwek[/] [grey]›[/] [bold]{Escape(shownServer)}[/]";
        string rightMarkup =
            $"[{( _live ? GreenHex : RedHex)}]{dot}[/] [grey]{state}[/]" +
            (showUser ? $" [grey]·[/] [bold]{Escape(user)}[/]" : "");

        string line = leftMarkup + new string(' ', gap) + rightMarkup;

        return MakePanel(line, null, focused: false);
    }

    private IRenderable RenderServers(int width, int height)
    {
        int inner = Math.Max(1, width - 4);
        int footerRows = 3;
        int capacity = Math.Max(1, height - footerRows);
        List<IRenderable> rows = [];

        void AddContent(string markup)
        {
            if (rows.Count < capacity)
            {
                rows.Add(new Markup(markup));
            }
        }

        if (_servers.Count == 0)
        {
            AddContent("");
            AddContent($"[grey]{Crop("No servers yet", inner)}[/]");
            AddContent($"[grey]{Crop("Create or join", inner)}[/]");
        }
        else
        {
            (int start, int count) = VisibleWindow(
                _servers.Count,
                Math.Max(0, _serverIndex),
                capacity);

            for (int i = start; i < start + count; i++)
            {
                AddContent(RenderServerRow(_servers[i], i, inner));
            }
        }

        while (rows.Count < capacity)
        {
            rows.Add(new Markup(""));
        }

        rows.Add(Divider(inner));
        rows.Add(new Markup(HintLine(("n", "new"), ("j", "join"))));
        rows.Add(new Markup(HintLine(("v", "details"))));

        string header = $" Servers ({_servers.Count}) ";
        return MakePanel(new Rows(rows), header, _focus == Pane.Servers);
    }

    private string RenderServerRow(ServerDto server, int index, int width)
    {
        bool selected = index == _serverIndex;
        bool focused = _focus == Pane.Servers;

        ChannelUnread unread = GetServerUnread(server.Id);
        bool hasUnread = unread.Count > 0;
        bool mentioned = hasUnread && unread.Mentioned;

        string marker = selected ? "▸ " : "  ";
        string badge = hasUnread
            ? (mentioned ? "@" : "") + FormatUnreadCount(unread.Count)
            : "";
        int badgeWidth = hasUnread ? badge.Length + 2 : 0;
        int leftWidth = Math.Max(1, width - badgeWidth);
        string name = Crop(
            server.Name,
            Math.Max(1, leftWidth - marker.Length));
        string leftPlain = PadRight(marker + name, leftWidth);

        if (selected && focused)
        {
            string plain = leftPlain +
                (hasUnread ? " " + badge + " " : "");
            return $"[black on {AccentHex}]{Escape(plain)}[/]";
        }

        string badgeMarkup = hasUnread
            ? mentioned
                ? $"[black on {AmberHex}] {Escape(badge)} [/]"
                : $"[black on {AccentHex}] {Escape(badge)} [/]"
            : "";

        string markerColor = selected ? AccentHex : MutedHex;
        string nameColor = selected
            ? AccentHex
            : hasUnread
                ? "bold white"
                : "white";

        return
            $"[{markerColor}]{Escape(marker)}[/]" +
            $"[{nameColor}]{Escape(name)}[/]" +
            new string(' ', Math.Max(0, leftWidth - marker.Length - name.Length)) +
            badgeMarkup;
    }

    private IRenderable RenderChannels(int width, int height)
    {
        int inner = Math.Max(1, width - 4);
        ServerDto? server = CurrentServer;
        int footerRows = server is null ? 0 : 2;
        int capacity = Math.Max(1, height - footerRows);
        List<IRenderable> rows = [];

        void AddContent(string markup)
        {
            if (rows.Count < capacity)
            {
                rows.Add(new Markup(markup));
            }
        }

        if (server is null)
        {
            AddContent("");
            AddContent($"[grey]{Crop("Select a server", inner)}[/]");
            AddContent($"[grey]{Crop("Ctrl+K to switch", inner)}[/]");
        }
        else if (_channels.Count == 0)
        {
            AddContent("");
            AddContent($"[grey]{Crop("No channels yet", inner)}[/]");
            AddContent($"[grey]{Crop("Create a channel", inner)}[/]");
        }
        else
        {
            (int start, int count) = VisibleWindow(
                _channels.Count,
                Math.Max(0, _channelIndex),
                capacity);

            for (int i = start; i < start + count; i++)
            {
                AddContent(RenderChannelRow(_channels[i], i, inner));
            }
        }

        while (rows.Count < capacity)
        {
            rows.Add(new Markup(""));
        }

        if (server is not null)
        {
            rows.Add(Divider(inner));
            rows.Add(new Markup(_channels.Count > 0
                ? HintLine(("c", "new"), ("u", "unread"))
                : HintLine(("c", "new"))));
        }

        string header = server is null
            ? " Channels "
            : $" #{Escape(Crop(server.Name, Math.Max(1, width - 6)))} ";

        return MakePanel(new Rows(rows), header, _focus == Pane.Channels);
    }

    private string RenderChannelRow(ChannelDto channel, int index, int width)
    {
        bool selected = index == _channelIndex;
        bool focused = _focus == Pane.Channels;

        _unreadByChannel.TryGetValue(channel.Id, out ChannelUnread? unread);
        bool hasUnread = unread is { Count: > 0 };
        bool mentioned = unread is { Count: > 0, Mentioned: true };

        string marker = selected ? "▸ " : "  ";
        string badge = hasUnread
            ? (mentioned ? "@" : "") + FormatUnreadCount(unread!.Count)
            : "";
        int badgeWidth = hasUnread ? badge.Length + 2 : 0;
        int leftWidth = Math.Max(1, width - badgeWidth);
        string name = Crop(
            "#" + channel.Name,
            Math.Max(1, leftWidth - marker.Length));
        string leftPlain = PadRight(marker + name, leftWidth);

        if (selected && focused)
        {
            string plain = leftPlain +
                (hasUnread ? " " + badge + " " : "");
            return $"[black on {AccentHex}]{Escape(plain)}[/]";
        }

        string badgeMarkup = hasUnread
            ? mentioned
                ? $"[black on {AmberHex}] {Escape(badge)} [/]"
                : $"[black on {AccentHex}] {Escape(badge)} [/]"
            : "";

        string markerColor = selected ? AccentHex : MutedHex;
        string nameColor = selected
            ? AccentHex
            : hasUnread
                ? "bold white"
                : "white";

        return
            $"[{markerColor}]{Escape(marker)}[/]" +
            $"[{nameColor}]{Escape(name)}[/]" +
            new string(' ', Math.Max(0, leftWidth - marker.Length - name.Length)) +
            badgeMarkup;
    }

    private IRenderable RenderChat(int width, int height)
    {
        int inner = Math.Max(1, width - 4);

        if (_showQuickSwitch)
        {
            return MakePanel(
                new Rows(BuildQuickSwitchRows(inner, height)),
                " Quick Switch ",
                focused: false);
        }

        if (_showEmojiPicker)
        {
            return MakePanel(
                new Rows(BuildEmojiPickerRows(inner, height)),
                _emojiPickerMode == EmojiPickerMode.Reaction
                    ? " Add Reaction "
                    : " Emoji ",
                focused: false);
        }

        if (_help)
        {
            List<IRenderable> helpRows = BuildHelpRows(inner);
            int viewHeight = Math.Max(1, height);
            _helpPageHeight = viewHeight;
            _helpMaxScroll = Math.Max(0, helpRows.Count - viewHeight);
            _helpScroll = Math.Clamp(_helpScroll, 0, _helpMaxScroll);

            List<IRenderable> visible = helpRows
                .Skip(_helpScroll)
                .Take(viewHeight)
                .ToList();

            string helpHeader = _helpMaxScroll > 0
                ? " Help  ↑↓ "
                : " Help ";

            return MakePanel(
                new Rows(visible),
                helpHeader,
                focused: false);
        }

        if (_showServerDetails)
        {
            List<IRenderable> detailRows = BuildServerDetailRows(inner);
            int viewHeight = Math.Max(1, height);
            _serverDetailPageHeight = viewHeight;
            _serverDetailMaxScroll = Math.Max(0, detailRows.Count - viewHeight);
            _serverDetailScroll = Math.Clamp(
                _serverDetailScroll, 0, _serverDetailMaxScroll);

            List<IRenderable> visible = detailRows
                .Skip(_serverDetailScroll)
                .Take(viewHeight)
                .ToList();

            string detailHeader = _serverDetailMaxScroll > 0
                ? " Server Detail  ↑↓ "
                : " Server Detail ";

            return MakePanel(new Rows(visible), detailHeader, focused: false);
        }

        if (_showSearch)
        {
            return MakePanel(
                new Rows(BuildSearchRows(inner, height)),
                " Search Messages ",
                focused: false);
        }

        if (_showAvatarPicker)
        {
            string pickerTitle = _filePickerKind == FilePickerKind.Attachment
                ? " Choose File "
                : " Choose Avatar ";
            return MakePanel(
                new Rows(BuildAvatarPickerRows(inner, height)),
                pickerTitle,
                focused: false);
        }

        if (_showProfile)
        {
            return MakePanel(
                new Rows(BuildProfileRows(inner)),
                " My Profile ",
                focused: false);
        }

        if (CurrentServer is null)
        {
            return MakeNotice(
                " Chat ",
                ["No server selected.", "", "Press n to create a server", "or j to join one by GUID."],
                width);
        }

        if (CurrentChannel is not { } channel)
        {
            return MakeNotice(
                " Chat ",
                ["No channel selected.", "", "Create one with c", "or pick another server."],
                width);
        }

        List<ChatLine> lines = BuildMessageLines(inner);
        List<IRenderable> rows = [];

        if (_messages.Count == 0)
        {
            rows.Add(new Markup(""));
            rows.Add(new Markup($"[grey]{Crop($"No messages in #{channel.Name} yet.", inner)}[/]"));
            rows.Add(new Markup($"[grey]{Crop("Press Enter or i, then type a message.", inner)}[/]"));
        }
        else
        {
            int viewHeight = Math.Max(1, height);
            (int start, int count) = ComputeMessageWindow(lines, viewHeight);
            _lastBodyHeight = viewHeight;
            _viewStart = start;

            for (int i = 0; i < Math.Max(0, viewHeight - count); i++)
            {
                rows.Add(new Markup(" "));
            }

            for (int i = start; i < start + count; i++)
            {
                rows.Add(new Markup(lines[i].Markup));
            }
        }

        string header = BuildChatHeader(channel, width);
        return MakePanel(new Rows(rows), header, _focus == Pane.Messages);
    }

    private (int Start, int Count) ComputeMessageWindow(
        List<ChatLine> lines,
        int height)
    {
        int total = lines.Count;
        if (total <= height)
        {
            _ensureSelectionVisible = false;
            return (0, total);
        }

        if (_followTail)
        {
            _ensureSelectionVisible = false;
            return (total - height, height);
        }

        int start = Math.Clamp(_viewStart, 0, total - height);

        if (_ensureSelectionVisible && _messageIndex >= 0)
        {
            int selStart = -1;
            int selEnd = -1;

            for (int i = 0; i < lines.Count; i++)
            {
                if (lines[i].MessageIndex != _messageIndex)
                {
                    continue;
                }

                if (selStart < 0)
                {
                    selStart = i;
                }

                selEnd = i;
            }

            if (selStart >= 0)
            {
                if (selStart < start)
                {
                    start = selStart;
                }
                else if (selEnd >= start + height)
                {
                    start = Math.Min(selEnd - height + 1, total - height);
                }

                start = Math.Clamp(start, 0, total - height);
            }

            _ensureSelectionVisible = false;
        }

        return (start, height);
    }

    private List<ChatLine> BuildMessageLines(int width)
    {
        List<ChatLine> lines = [];
        int avatarWidth = ChatAvatarWidth;
        int textWidth = Math.Max(8, width - avatarWidth - 1);
        int maxAuthor = Math.Clamp(textWidth / 3, 6, 14);
        string blankAvatar = new(' ', avatarWidth);

        for (int i = 0; i < _messages.Count; i++)
        {
            MessageDto message = _messages[i];

            if (i > 0)
            {
                lines.Add(new ChatLine(" ", -1));
            }

            bool mine = message.UserId == _session.User.UserId;
            string author = !string.IsNullOrWhiteSpace(message.DisplayName)
                ? message.DisplayName
                : mine
                    ? _session.User.DisplayName
                    : ShortId(message.UserId);
            author = Crop(author, maxAuthor);

            string time = message.CreatedAt.ToLocalTime().ToString("HH:mm");
            string content = message.Content.Replace("\t", "    ");
            if (message.EditedAt is not null)
            {
                content += " (edited)";
            }

            bool selected = _focus == Pane.Messages && i == _messageIndex;
            bool mentionsMe = MentionsMe(message.Content);
            string authorColor = mine ? GreenHex : AccentHex;
            string mentionMarker = mentionsMe ? $"[{AmberHex}]@[/] " : "";
            List<ChatLine> textLines = [];

            if (message.ReplyTo is { } reply)
            {
                string replyAuthor = string.IsNullOrWhiteSpace(reply.DisplayName)
                    ? "Unknown"
                    : reply.DisplayName;
                string replyContent = string.IsNullOrWhiteSpace(reply.Content)
                    ? "(deleted)"
                    : reply.Content.ReplaceLineEndings(" ");
                string replyTitle = $"↳ {replyAuthor}";
                string replySnippet = Crop(
                    replyContent,
                    Math.Max(0, textWidth - replyTitle.Length - 2));
                string replyPlain = replyTitle + ": " + replySnippet;

                textLines.Add(new ChatLine(
                    $"[{MutedHex}]{Escape(replyTitle)}:[/] " +
                    $"[grey]{Escape(replySnippet)}[/]",
                    i,
                    replyPlain.Length));
            }

            textLines.Add(new ChatLine(
                mentionMarker +
                $"[grey]{time}[/] " +
                $"[{authorColor}]{Escape(author)}[/]",
                i,
                (mentionsMe ? 2 : 0) + time.Length + 1 + author.Length));

            if (!string.IsNullOrWhiteSpace(content))
            {
                foreach (MarkdownLine line in MarkdownRenderer.Render(
                             content,
                             textWidth,
                             HighlightMentions))
                {
                    textLines.Add(new ChatLine(
                        line.Markup,
                        i,
                        line.PlainLength));
                }
            }

            if (!string.IsNullOrWhiteSpace(message.AttachmentUrl))
            {
                textLines.Add(BuildAttachmentLine(message, i, textWidth));

                if (IsImageAttachment(message))
                {
                    AvatarArt? attachment = GetAvatarArt(
                        message.AttachmentUrl, AttachmentPreviewWidth);

                    if (attachment is not null)
                    {
                        foreach (string artLine in attachment.Lines)
                        {
                            textLines.Add(new ChatLine(
                                artLine, i, AttachmentPreviewWidth));
                        }
                    }
                }
            }

            if (message.Reactions is { Count: > 0 })
            {
                List<string> chips = [];
                int plainLength = 0;

                foreach (ReactionDto reaction in message.Reactions)
                {
                    bool mineReaction =
                        message.MyReactions?.Contains(reaction.Emoji) == true;
                    string text = $"{reaction.Emoji} {reaction.Count}";

                    chips.Add(mineReaction
                        ? $"[black on {AccentHex}] {Escape(text)} [/]"
                        : $"[on {SelectionBgHex}] {Escape(text)} [/]");

                    plainLength += text.Length + 2;
                }

                plainLength += Math.Max(0, chips.Count - 1);
                textLines.Add(new ChatLine(
                    string.Join(" ", chips),
                    i,
                    plainLength));
            }

            IReadOnlyList<string> avatarLines = GetAvatarLines(
                message.AvatarUrl, message.DisplayName, message.UserId,
                avatarWidth);
            int height = Math.Max(textLines.Count, avatarLines.Count);

            for (int row = 0; row < height; row++)
            {
                string avatarPart = row < avatarLines.Count
                    ? avatarLines[row]
                    : blankAvatar;
                ChatLine? text = row < textLines.Count ? textLines[row] : null;
                string composed = avatarPart +
                    (text is null ? "" : " " + text.Markup);

                if (selected)
                {
                    int plainLength = avatarWidth +
                        (text is null ? 0 : 1 + text.PlainLength);
                    int pad = Math.Max(0, width - plainLength);
                    composed = $"[on {SelectionBgHex}]{composed}" +
                        $"{new string(' ', pad)}[/]";
                }

                lines.Add(new ChatLine(composed, i));
            }
        }

        return lines;
    }

    private static ChatLine BuildAttachmentLine(
        MessageDto message,
        int messageIndex,
        int width)
    {
        string name = string.IsNullOrWhiteSpace(message.AttachmentName)
            ? Path.GetFileName(message.AttachmentUrl ?? "file")
            : message.AttachmentName;
        string size = message.AttachmentSize is { } bytes
            ? FormatSize(bytes)
            : "";
        const string suffix = " · o open";
        int fixedWidth = 2 + suffix.Length +
            (size.Length > 0 ? 3 + size.Length : 0);
        int nameRoom = Math.Max(4, width - fixedWidth);
        name = Crop(name, nameRoom);

        string plain = "▤ " + name +
            (size.Length > 0 ? " · " + size : "") +
            suffix;
        string markup =
            $"[grey]▤[/] [bold]{Escape(name)}[/]" +
            (size.Length > 0 ? $"[grey] · {Escape(size)}[/]" : "") +
            $"[grey]{Escape(suffix)}[/]";

        return new ChatLine(markup, messageIndex, plain.Length);
    }

    private IReadOnlyList<string> GetAvatarLines(
        string? url,
        string displayName,
        string userId,
        int width)
    {
        AvatarArt? art = GetAvatarArt(url, width);
        return art?.Lines ?? BuildInitialsAvatar(displayName, userId, width);
    }

    private IReadOnlyList<string> BuildInitialsAvatar(
        string displayName,
        string userId,
        int width)
    {
        string initials = Crop(GetInitials(displayName, userId), width);
        string color = GetFallbackAvatarColor(userId);
        int left = Math.Max(0, (width - initials.Length) / 2);
        int right = Math.Max(0, width - initials.Length - left);

        return
        [
            $"[on {color}]{new string(' ', width)}[/]",
            $"[black on {color}]{new string(' ', left)}" +
            $"{Escape(initials)}{new string(' ', right)}[/]"
        ];
    }

    private static string GetInitials(string displayName, string userId)
    {
        string name = string.IsNullOrWhiteSpace(displayName)
            ? userId
            : displayName.Trim();

        if (string.IsNullOrWhiteSpace(name))
        {
            return "?";
        }

        string[] words = name.Split(
            [' ', '.', '_', '-'],
            StringSplitOptions.RemoveEmptyEntries);

        string initials = words.Length switch
        {
            0 => "?",
            1 => words[0].Length >= 2 ? words[0][..2] : words[0],
            _ => $"{words[0][0]}{words[1][0]}"
        };

        return initials.ToUpperInvariant();
    }

    private string GetFallbackAvatarColor(string userId)
    {
        if (_fallbackAvatarColors.TryGetValue(userId, out string? color))
        {
            return color;
        }

        color = AvatarColors[Random.Shared.Next(AvatarColors.Length)];
        _fallbackAvatarColors[userId] = color;
        return color;
    }

    private string BuildChatHeader(ChannelDto channel, int width)
    {
        int max = Math.Max(8, width - 6);
        string label = "#" + channel.Name;
        string shownLabel = Crop(label, max);
        int room = max - shownLabel.Length - 3;

        string typing = GetTypingSummary();

        if (typing.Length > 0 && room >= 5)
        {
            return
                $" [bold default]{Escape(shownLabel)}[/] " +
                $"[{GreenHex}]· {Escape(Crop(typing, room))}[/] ";
        }

        if (string.IsNullOrWhiteSpace(channel.Topic) || room < 5)
        {
            return $" [bold default]{Escape(shownLabel)}[/] ";
        }

        string shownTopic = Crop(channel.Topic, room);
        return
            $" [bold default]{Escape(shownLabel)}[/] " +
            $"[{MutedHex}]· {Escape(shownTopic)}[/] ";
    }

    private string GetTypingSummary()
    {
        if (_typingUsers.Count == 0)
        {
            return "";
        }

        List<string> names = _typingUsers
            .Values
            .Select(user => user.DisplayName)
            .Distinct()
            .ToList();

        return names.Count switch
        {
            0 => "",
            1 => $"{names[0]} is typing…",
            2 => $"{names[0]} and {names[1]} are typing…",
            _ => $"{names[0]} and {names.Count - 1} others are typing…"
        };
    }

    private int GetComposerHeight()
    {
        if (_modal is not null)
        {
            return 3;
        }

        int lines = 1;

        foreach (char character in _input)
        {
            if (character == '\n')
            {
                lines++;
            }
        }

        int replyLines = _replyTarget is null ? 0 : 1;
        int mentionLines = _showMentionSuggestions
            ? Math.Max(1, _mentionResults.Count)
            : 0;

        return Math.Clamp(
            lines + 2 + replyLines + mentionLines,
            3 + replyLines + mentionLines,
            6 + replyLines + mentionLines);
    }

    private IRenderable RenderComposer(int width, int height)
    {
        int inner = Math.Max(1, width - 4);
        string content;
        string header;
        Color border;

        if (_modal is not null)
        {
            border = AmberColor;
            header = $" {_modal.Title} ";

            if (_modal.Kind == ModalKind.ConfirmDelete)
            {
                string preview = CurrentMessage is { } message
                    ? Crop(message.Content, Math.Max(5, inner - 24))
                    : $"#{_modal.MessageId}";
                content =
                    $"[{RedHex}]Delete[/] [grey]{Escape(preview)}[/] " +
                    $"[grey]—[/] [{AmberHex}]y[/][grey]/[/][{AmberHex}]n[/]";
            }
            else
            {
                string hint = _modal.Hint;
                string buffer = _modal.Buffer;
                int room = Math.Max(5, inner - hint.Length - 4);
                buffer = CropTail(buffer, room);
                content =
                    $"[grey]{Escape(hint)}:[/] " +
                    $"[bold]{Escape(buffer)}[/]" +
                    $"[{AccentHex}]▌[/]";
            }
        }
        else
        {
            bool typing = _focus == Pane.Input;
            border = typing ? AccentColor : BorderColor;
            header = " Message ";
            int replyLines = _replyTarget is null ? 0 : 1;
            int mentionLines = _showMentionSuggestions
                ? Math.Max(1, _mentionResults.Count)
                : 0;

            if (_replyTarget is null &&
                !_showMentionSuggestions &&
                _input.Length == 0 &&
                !typing)
            {
                content = $"[grey]› press i or Enter to type · type /help for commands[/]";
            }
            else
            {
                int rows = Math.Max(1, height - 2 - replyLines - mentionLines);
                int room = Math.Max(5, inner - 4);
                string[] lines = _input.Split('\n');
                int start = Math.Max(0, lines.Length - rows);
                List<string> contentLines = [];

                if (_replyTarget is { } reply)
                {
                    string snippet = Crop(
                        reply.Content.ReplaceLineEndings(" "),
                        Math.Max(5, inner - 12));
                    contentLines.Add(
                        $"[{AccentHex}]↳[/] [{MutedHex}]reply to[/] " +
                        $"[bold]{Escape(reply.DisplayName)}[/] " +
                        $"[grey]{Escape(snippet)}[/]");
                }

                if (_showMentionSuggestions)
                {
                    if (_mentionResults.Count == 0)
                    {
                        contentLines.Add(
                            $"[grey]{Crop("No matching members", inner)}[/]");
                    }
                    else
                    {
                        for (int i = 0; i < _mentionResults.Count; i++)
                        {
                            contentLines.Add(RenderMentionSuggestionRow(
                                _mentionResults[i],
                                i,
                                inner));
                        }
                    }
                }

                for (int i = start; i < lines.Length; i++)
                {
                    string buffer = CropTail(lines[i], room);
                    bool firstVisible = i == start;
                    bool lastLine = i == lines.Length - 1;
                    string prefix;

                    if (i == 0)
                    {
                        prefix = typing
                            ? $"[{AccentHex}]›[/] "
                            : "[grey]›[/] ";
                    }
                    else
                    {
                        prefix = firstVisible ? "[grey]…[/] " : "  ";
                    }

                    string lineMarkup = typing
                        ? $"[bold]{Escape(buffer)}[/]"
                        : Escape(buffer);

                    if (typing && lastLine)
                    {
                        lineMarkup += $"[{AccentHex}]▌[/]";
                    }

                    contentLines.Add(prefix + lineMarkup);
                }

                content = string.Join("\n", contentLines);
            }
        }

        return new Panel(new Markup(content) { Overflow = Overflow.Crop })
        {
            Header = new PanelHeader(header, Justify.Left),
            Expand = true,
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(foreground: border),
            Padding = new Padding(1, 0, 1, 0)
        };
    }

    private IRenderable RenderFooter(int width)
    {
        int inner = Math.Max(1, width - 1);
        string state = _live ? "live" : "offline";
        string dot = _live ? "●" : "○";
        string baseLeft = dot + " " + state;

        string hints = _modal is not null
            ? (_modal.Kind == ModalKind.ConfirmDelete
                ? "y delete  ·  n cancel"
                : "Enter confirm  ·  Esc cancel")
            : _help
                ? (inner >= 60
                    ? "↑↓ scroll · PgUp/PgDn page · Home/End · F1/?/Esc close"
                    : "↑↓ scroll · Esc close")
            : _showQuickSwitch
                ? (inner >= 70
                    ? "type to filter · ↑↓ move · PgUp/PgDn · Enter open · Esc close"
                    : "↑↓ move · Enter open · Esc close")
            : _showEmojiPicker
                ? (inner >= 70
                    ? $"type to filter · ↑↓ move · PgUp/PgDn · Enter {(_emojiPickerMode == EmojiPickerMode.Reaction ? "react" : "insert")} · Esc close"
                    : _emojiPickerMode == EmojiPickerMode.Reaction
                        ? "↑↓ move · Enter react · Esc close"
                        : "↑↓ move · Enter insert · Esc close")
            : _showSearch
                ? (inner >= 70
                    ? "type to filter · ↑↓ move · PgUp/PgDn · Enter jump · Esc close"
                    : "↑↓ move · Enter jump · Esc close")
            : _showAvatarPicker
                ? (_avatarSearchActive
                    ? (inner >= 80
                        ? "type to search · Enter upload · Backspace edit · Esc clear search"
                        : "Enter upload · Esc clear search")
                    : (inner >= 85
                        ? "Enter open/upload · type to search · Backspace up · Esc cancel"
                        : inner >= 60
                            ? "Enter open · type search · Esc close"
                            : "Enter open · Esc close"))
            : _showProfile
                ? (inner >= 75
                    ? "d display name · u username · a avatar · x clear · Esc close"
                    : "d/u/a edit · x clear · Esc close")
            : _showServerDetails
                ? (inner >= 85
                    ? "↑↓ scroll · PgUp/PgDn page · Home/End · v/Esc close"
                    : "↑↓ scroll · v/Esc close")
            : _showMentionSuggestions
                ? (inner >= 65
                    ? "↑↓ select · Tab/Enter insert · Esc dismiss"
                    : "↑↓ select · Enter insert")
                : _focus == Pane.Input
                    ? (inner >= 100
                        ? (_sendOnCtrlEnter
                            ? "Ctrl+Enter send · Enter newline · Esc browse · ↑↓ history · Ctrl+E/F3 emoji · F4 paste · /help commands"
                            : "Enter send · Shift+Enter newline · Esc browse · ↑↓ history · Ctrl+E/F3 emoji · F4 paste · /help commands")
                        : _sendOnCtrlEnter
                            ? "Ctrl+Enter send · Enter newline · /help commands"
                            : "Enter send · Shift+Enter newline · /help commands")
                    : (inner >= 110
                        ? "Tab focus · ↑↓ move · Enter open · i type · / search · Ctrl+K/F2 switch · Ctrl+E/F3 emoji · F4 paste · f file · o open · n server · c channel · j join · p profile · u unread · r reply · x react · F5 refresh · ? help · q quit"
                        : inner >= 70
                            ? "Tab focus · i type · / search · Ctrl+K/F2 switch · Ctrl+E/F3 emoji · n server · c channel · p profile · u unread · ? help · q quit"
                            : "Tab · i type · ? help · q quit");

        if (hints.Length > 0 &&
            baseLeft.Length + 3 + 8 + hints.Length + 1 > inner)
        {
            hints = _showServerDetails
                ? (inner >= 55 ? "↑↓ scroll · v/Esc close" : "↑↓ · v/Esc")
                : inner switch
                {
                    >= 85 => "Tab focus · i type · n server · c channel · q quit",
                    >= 60 => "Tab · i type · ? help · q quit",
                    _ => ""
                };
        }

        if (baseLeft.Length + hints.Length + 1 > inner)
        {
            hints = "";
        }

        bool hasStatus = _statusUntil != DateTime.MinValue &&
                         DateTime.UtcNow < _statusUntil;
        int statusRoom = inner - baseLeft.Length - hints.Length - (hints.Length > 0 ? 2 : 0) - 3;
        string status = hasStatus && statusRoom >= 6
            ? Crop(_status, statusRoom)
            : "";

        string leftMarkup =
            $"[{(_live ? GreenHex : RedHex)}]{dot}[/] [grey]{state}[/]";
        int leftPlainLength = baseLeft.Length;

        if (status.Length > 0)
        {
            leftMarkup +=
                $" [grey]│[/] " +
                $"[{(_statusIsError ? RedHex : MutedHex)}]{Escape(status)}[/]";
            leftPlainLength += 3 + status.Length;
        }

        int gap = Math.Max(0, inner - leftPlainLength - hints.Length);
        string line = leftMarkup;

        if (gap > 0)
        {
            line += new string(' ', gap);
        }

        if (hints.Length > 0)
        {
            line += $"[{MutedHex}]{Escape(hints)}[/]";
        }

        return new Markup(line) { Overflow = Overflow.Crop };
    }

    private List<IRenderable> BuildQuickSwitchRows(int width, int height)
    {
        List<IRenderable> rows = [];
        string query = CropTail(_quickSwitchQuery, Math.Max(1, width - 4));
        rows.Add(new Markup(
            $"[bold {AccentHex}]›[/] [bold]{Escape(query)}[/]" +
            $"[{AccentHex}]▌[/]"));

        string summary = _quickSwitchResults.Count switch
        {
            0 => "No matching servers or channels",
            1 => "1 result · ↑↓ move · Enter open · Esc close",
            _ => $"{_quickSwitchResults.Count} results · ↑↓ move · " +
                 "Enter open · Esc close"
        };
        rows.Add(new Markup($"[grey]{Escape(Crop(summary, width))}[/]"));

        if (_quickSwitchResults.Count == 0)
        {
            return rows;
        }

        int capacity = Math.Max(1, height - 2);
        (int start, int count) = VisibleWindow(
            _quickSwitchResults.Count,
            _quickSwitchIndex,
            capacity);

        for (int i = start; i < start + count; i++)
        {
            rows.Add(new Markup(RenderQuickSwitchRow(i, width)));
        }

        return rows;
    }

    private string RenderQuickSwitchRow(int resultIndex, int width)
    {
        QuickSwitchEntry entry = _quickSwitchResults[resultIndex];
        bool selected = resultIndex == _quickSwitchIndex;
        bool isServer = entry.Kind == QuickSwitchKind.Server;
        bool isCurrentChannel = !isServer &&
            entry.ServerId == CurrentServer?.Id &&
            entry.ChannelId == CurrentChannel?.Id;

        ChannelUnread unread = isServer
            ? GetServerUnread(entry.ServerId)
            : _unreadByChannel.TryGetValue(
                entry.ChannelId,
                out ChannelUnread? state)
                ? state
                : new ChannelUnread(0, false);

        bool hasUnread = unread.Count > 0;
        bool mentioned = hasUnread && unread.Mentioned;
        string badge = hasUnread
            ? (mentioned ? "@" : "") + FormatUnreadCount(unread.Count)
            : "";
        int badgeWidth = hasUnread ? badge.Length + 2 : 0;
        int leftWidth = Math.Max(1, width - badgeWidth);

        string marker = isServer
            ? "▣ "
            : isCurrentChannel
                ? "● "
                : "  ";
        string label = isServer
            ? entry.ServerName
            : $"#{entry.ChannelName}  ·  {entry.ServerName}";
        label = Crop(label, Math.Max(1, leftWidth - marker.Length));
        int gap = Math.Max(0, leftWidth - marker.Length - label.Length);

        string markerMarkup = isServer
            ? $"[{AccentHex}]{Escape(marker)}[/]"
            : isCurrentChannel
                ? $"[{GreenHex}]{Escape(marker)}[/]"
                : $"[{MutedHex}]{Escape(marker)}[/]";
        string nameColor = selected || isServer || hasUnread
            ? "bold white"
            : "white";
        string nameMarkup = $"[{nameColor}]{Escape(label)}[/]";
        string badgeMarkup = hasUnread
            ? mentioned
                ? $"[black on {AmberHex}] {Escape(badge)} [/]"
                : $"[black on {AccentHex}] {Escape(badge)} [/]"
            : "";

        string markup =
            markerMarkup +
            nameMarkup +
            new string(' ', gap) +
            badgeMarkup;

        if (selected)
        {
            markup = $"[on {SelectionBgHex}]{markup}[/]";
        }

        return markup;
    }

    private List<IRenderable> BuildEmojiPickerRows(int width, int height)
    {
        List<IRenderable> rows = [];
        string query = CropTail(_emojiQuery, Math.Max(1, width - 4));
        rows.Add(new Markup(
            $"[bold {AccentHex}]›[/] [bold]{Escape(query)}[/]" +
            $"[{AccentHex}]▌[/]"));

        string action = _emojiPickerMode == EmojiPickerMode.Reaction
            ? "react"
            : "insert";
        string summary = _emojiResults.Count switch
        {
            0 => "No matching emoji",
            1 => $"1 emoji · ↑↓ move · Enter {action} · Esc close",
            _ => $"{_emojiResults.Count} emoji · ↑↓ move · " +
                 $"Enter {action} · Esc close"
        };
        rows.Add(new Markup($"[grey]{Escape(Crop(summary, width))}[/]"));

        if (_emojiResults.Count == 0)
        {
            return rows;
        }

        int capacity = Math.Max(1, height - 2);
        (int start, int count) = VisibleWindow(
            _emojiResults.Count,
            _emojiIndex,
            capacity);

        for (int i = start; i < start + count; i++)
        {
            rows.Add(new Markup(RenderEmojiRow(i, width)));
        }

        return rows;
    }

    private string RenderMentionSuggestionRow(
        ServerMemberDto member,
        int index,
        int width)
    {
        bool selected = index == _mentionIndex;
        bool online = _onlineUserIds.Contains(member.UserId);
        bool you = member.UserId == _session.User.UserId;

        string username = "@" + member.UserName;
        int usernameRoom = Math.Max(3, width - 2 - (you ? 6 : 0));
        username = Crop(username, usernameRoom);

        string display = member.DisplayName;

        if (string.Equals(
                display,
                member.UserName,
                StringComparison.OrdinalIgnoreCase))
        {
            display = "";
        }

        string suffix = you ? " (you)" : "";
        int displayRoom = Math.Max(
            0,
            width - 2 - username.Length - suffix.Length - 1);
        display = Crop(display, displayRoom);

        string dot = online ? "●" : "○";
        string plain = dot + " " + username;

        if (display.Length > 0)
        {
            plain += " " + display;
        }

        plain += suffix;
        plain = Crop(plain, width);

        if (selected)
        {
            return $"[black on {AccentHex}]" +
                $"{Escape(PadRight(plain, width))}[/]";
        }

        string markup =
            $"[{(online ? GreenHex : MutedHex)}]{dot}[/] " +
            $"[bold {AccentHex}]{Escape(username)}[/]";

        if (display.Length > 0)
        {
            markup += $" [grey]{Escape(display)}[/]";
        }

        if (suffix.Length > 0)
        {
            markup += $"[grey]{Escape(suffix)}[/]";
        }

        return markup;
    }

    private string RenderEmojiRow(int index, int width)
    {
        EmojiEntry emoji = _emojiResults[index];
        bool selected = index == _emojiIndex;
        string label = $"{emoji.Symbol}  {emoji.Name}";

        if (width > label.Length + 12)
        {
            label += $"  {emoji.Keywords}";
        }

        label = Crop(label, width);
        string plain = Escape(label) +
            new string(' ', Math.Max(0, width - label.Length));

        return selected
            ? $"[on {SelectionBgHex}][bold white]{plain}[/][/]"
            : $"[white]{plain}[/]";
    }

    private List<IRenderable> BuildSearchRows(int width, int height)
    {
        List<IRenderable> rows = [];
        string query = CropTail(_searchQuery, Math.Max(1, width - 4));
        rows.Add(new Markup(
            $"[bold {AccentHex}]›[/] [bold]{Escape(query)}[/]" +
            $"[{AccentHex}]▌[/]"));

        string summary = _searchResults.Count switch
        {
            0 => "No matching messages",
            1 => "1 message · ↑↓ move · Enter jump",
            _ => $"{_searchResults.Count} messages · ↑↓ move · Enter jump"
        };
        rows.Add(new Markup($"[grey]{Escape(Crop(summary, width))}[/]"));

        if (_searchResults.Count == 0)
        {
            return rows;
        }

        int capacity = Math.Max(1, height - 2);
        (int start, int count) = VisibleWindow(
            _searchResults.Count, _searchIndex, capacity);

        for (int i = start; i < start + count; i++)
        {
            rows.Add(new Markup(RenderSearchRow(i, width)));
        }

        return rows;
    }

    private string RenderSearchRow(int resultIndex, int width)
    {
        SearchEntry entry = _searchResults[resultIndex];

        if (entry.MessageIndex < 0 || entry.MessageIndex >= _messages.Count)
        {
            return "";
        }

        MessageDto message = _messages[entry.MessageIndex];
        bool mine = message.UserId == _session.User.UserId;
        string author = !string.IsNullOrWhiteSpace(message.DisplayName)
            ? message.DisplayName
            : mine
                ? _session.User.DisplayName
                : ShortId(message.UserId);
        author = Crop(author, Math.Clamp(width / 4, 6, 14));

        string time = message.CreatedAt.ToLocalTime().ToString("HH:mm");
        string prefixPlain = $"{time} {author} › ";
        string prefix =
            $"[grey]{time}[/] " +
            $"[{(mine ? GreenHex : AccentHex)}]{Escape(author)}[/] " +
            $"[grey]›[/] ";

        int available = width - prefixPlain.Length;
        string content = entry.Content;
        int start = 0;

        if (available > 3 && content.Length > available)
        {
            int firstMatch = entry.Matches.Count > 0
                ? entry.Matches[0]
                : 0;

            if (firstMatch >= available)
            {
                start = Math.Clamp(
                    firstMatch - available / 3,
                    0,
                    content.Length - available);
            }
        }

        bool truncatedStart = start > 0;
        int room = Math.Max(1, available - (truncatedStart ? 1 : 0));

        if (start + room < content.Length)
        {
            room = Math.Max(1, room - 1);
        }

        string visible = available > 3
            ? content.Substring(
                start, Math.Min(room, content.Length - start))
            : "";
        bool truncatedEnd = start + visible.Length < content.Length;
        string preview = BuildSearchPreview(
            entry, visible, start, truncatedStart, truncatedEnd);

        bool selected = resultIndex == _searchIndex;
        string markup = prefix + preview;

        if (selected)
        {
            int plainLength = prefixPlain.Length +
                visible.Length +
                (truncatedStart ? 1 : 0) +
                (truncatedEnd ? 1 : 0);
            int pad = Math.Max(0, width - plainLength);
            markup = $"[on {SelectionBgHex}]{markup}" +
                $"{new string(' ', pad)}[/]";
        }

        return markup;
    }

    private static string BuildSearchPreview(
        SearchEntry entry,
        string visible,
        int start,
        bool truncatedStart,
        bool truncatedEnd)
    {
        StringBuilder builder = new();

        if (truncatedStart)
        {
            builder.Append("[grey]…[/]");
        }

        int matchPointer = 0;

        while (matchPointer < entry.Matches.Count &&
               entry.Matches[matchPointer] < start)
        {
            matchPointer++;
        }

        for (int i = 0; i < visible.Length; i++)
        {
            int position = start + i;
            bool matched = matchPointer < entry.Matches.Count &&
                entry.Matches[matchPointer] == position;

            if (matched)
            {
                matchPointer++;
                builder.Append("[bold ")
                    .Append(AccentHex)
                    .Append(']')
                    .Append(Escape(visible[i].ToString()))
                    .Append("[/]");
            }
            else
            {
                builder.Append(Escape(visible[i].ToString()));
            }
        }

        if (truncatedEnd)
        {
            builder.Append("[grey]…[/]");
        }

        return builder.ToString();
    }

    private List<IRenderable> BuildAvatarPickerRows(int width, int height)
    {
        List<IRenderable> rows = [];

        if (_avatarSearchActive)
        {
            int room = Math.Max(1, width - 10);
            string query = CropTail(_avatarSearchQuery, room);
            rows.Add(new Markup(
                $"[bold {AccentHex}]Search:[/] [bold]{Escape(query)}[/]" +
                $"[{AccentHex}]▌[/]"));

            string info = _avatarSearchBusy
                ? $"in {_avatarPickerDirectory} · searching..."
                : $"in {_avatarPickerDirectory} · {_avatarPickerEntries.Count} found";
            rows.Add(new Markup($"[grey]{Escape(Crop(info, width))}[/]"));
        }
        else
        {
            rows.Add(new Markup($"[grey]{Escape(Crop(_avatarPickerDirectory, width))}[/]"));
            rows.Add(new Markup(""));
        }

        if (_avatarPickerEntries.Count == 0)
        {
            string noun = _filePickerKind == FilePickerKind.Attachment
                ? "files"
                : "image files";
            string message = !_avatarSearchActive
                ? $"No supported {noun} in this folder."
                : _avatarSearchBusy
                    ? "Searching..."
                    : _avatarSearchQuery.Length == 0
                        ? $"Type to search {noun}."
                        : $"No {noun} match \"{_avatarSearchQuery}\".";
            rows.Add(new Markup($"[grey]{Escape(Crop(message, width))}[/]"));
            return rows;
        }

        int capacity = Math.Max(1, height - 2);
        (int start, int count) = VisibleWindow(
            _avatarPickerEntries.Count,
            _avatarPickerIndex,
            capacity);

        for (int i = start; i < start + count; i++)
        {
            rows.Add(new Markup(
                RenderAvatarPickerRow(_avatarPickerEntries[i], i, width)));
        }

        return rows;
    }

    private string RenderAvatarPickerRow(
        FileSystemInfo entry,
        int index,
        int width)
    {
        bool selected = index == _avatarPickerIndex;
        bool directory = entry is DirectoryInfo;
        string marker = selected ? "▸ " : "  ";
        string name = directory
            ? entry.Name + "/"
            : _avatarSearchActive
                ? Path.GetRelativePath(_avatarPickerDirectory, entry.FullName)
                : entry.Name;
        string suffix = entry is FileInfo file
            ? "  " + FormatSize(file.Length)
            : "";

        int room = Math.Max(1, width - marker.Length - suffix.Length);
        string shown = Crop(name, room);

        if (suffix.Length > 0 && shown.Length < name.Length)
        {
            suffix = "";
        }

        string plain = PadRight(marker + shown + suffix, width);

        if (selected)
        {
            return $"[black on {AccentHex}]{Escape(plain)}[/]";
        }

        string markup =
            $"[{MutedHex}]{Escape(marker)}[/]" +
            $"[{(directory ? AccentHex : "white")}]{Escape(shown)}[/]";

        if (suffix.Length > 0)
        {
            markup += $"[grey]{Escape(suffix)}[/]";
        }

        return markup;
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} B";
        }

        if (bytes < 1024 * 1024)
        {
            return $"{bytes / 1024.0:0.#} KB";
        }

        return $"{bytes / (1024.0 * 1024.0):0.#} MB";
    }

    private List<IRenderable> BuildProfileRows(int width)
    {
        List<IRenderable> rows = [];
        UserProfileDto? profile = _profile;

        if (profile is null)
        {
            rows.Add(new Markup($"[grey]{Escape(Crop("Profile unavailable.", width))}[/]"));
            rows.Add(new Markup(""));
            rows.Add(new Markup($"[grey]{Escape(Crop("Press p to reload or Esc to close.", width))}[/]"));
            return rows;
        }

        int avatarWidth = ProfileAvatarWidth;
        int gutter = 2;
        int textWidth = Math.Max(8, width - avatarWidth - gutter);

        List<string> fieldLines = [];
        AddProfileFieldLines(
            fieldLines, "Display name", profile.DisplayName, textWidth);
        AddProfileFieldLines(
            fieldLines, "Username", "@" + profile.UserName, textWidth);
        AddProfileFieldLines(
            fieldLines,
            "Email",
            string.IsNullOrWhiteSpace(profile.Email) ? "(none)" : profile.Email,
            textWidth);
        AddProfileFieldLines(
            fieldLines, "User ID", profile.UserId, textWidth);

        IReadOnlyList<string> avatarLines = GetAvatarLines(
            profile.AvatarUrl, profile.DisplayName, profile.UserId, avatarWidth);
        int height = Math.Max(avatarLines.Count, fieldLines.Count);
        string blankAvatar = new(' ', avatarWidth);

        for (int row = 0; row < height; row++)
        {
            string avatarPart = row < avatarLines.Count
                ? avatarLines[row]
                : blankAvatar;
            string textPart = row < fieldLines.Count ? fieldLines[row] : "";
            string markup = textPart.Length > 0
                ? avatarPart + new string(' ', gutter) + textPart
                : avatarPart;
            rows.Add(new Markup(markup));
        }

        return rows;
    }

    private static void AddProfileFieldLines(
        List<string> lines,
        string label,
        string value,
        int width)
    {
        string prefix = label + ": ";
        List<string> wrapped = WrapText(
            value, Math.Max(1, width - prefix.Length));

        for (int i = 0; i < wrapped.Count; i++)
        {
            string lead = i == 0 ? prefix : new string(' ', prefix.Length);
            lines.Add(
                $"[grey]{Escape(lead)}[/][bold]{Escape(wrapped[i])}[/]");
        }
    }

    private List<IRenderable> BuildServerDetailRows(int width)
    {
        List<IRenderable> rows = [];
        ServerDto? server = CurrentServer;

        if (server is null)
        {
            rows.Add(new Markup($"[grey]{Escape(Crop("No server selected.", width))}[/]"));
            return rows;
        }

        rows.Add(new Markup($"[bold {AccentHex}]{Escape(Crop(server.Name, width))}[/]"));
        rows.Add(new Markup(" "));
        rows.Add(new Markup("[grey]Server ID[/]"));

        foreach (string line in WrapText(server.Id.ToString(), width))
        {
            rows.Add(new Markup($"[bold]{Escape(line)}[/]"));
        }

        rows.Add(new Markup(" "));
        rows.Add(new Markup($"[bold {AccentHex}]Owner[/]"));

        ServerMemberDto? owner = _serverMembers.FirstOrDefault(
            member => member.UserId == server.OwnerId);

        if (owner is not null)
        {
            rows.AddRange(BuildMemberBlock(
                owner,
                "owner",
                width,
                _onlineUserIds.Contains(owner.UserId)));
        }
        else
        {
            foreach (string line in WrapText(server.OwnerId, width))
            {
                rows.Add(new Markup($"[grey]{Escape(line)}[/]"));
            }

            rows.Add(new Markup($"[{AmberHex}](owner)[/]"));
        }

        List<ServerMemberDto> others = _serverMembers
            .Where(member => member.UserId != server.OwnerId)
            .ToList();

        int onlineOthers = others.Count(
            member => _onlineUserIds.Contains(member.UserId));

        rows.Add(new Markup(" "));
        rows.Add(new Markup(
            $"[bold {AccentHex}]Other Members ({others.Count}, " +
            $"{onlineOthers} online)[/]"));

        if (_serverMembers.Count == 0)
        {
            rows.Add(new Markup(" "));
            rows.Add(new Markup("[grey]No member details available.[/]"));
        }
        else if (others.Count == 0)
        {
            rows.Add(new Markup(" "));
            rows.Add(new Markup("[grey]No other members.[/]"));
        }
        else
        {
            rows.Add(new Markup(" "));

            foreach (ServerMemberDto member in others)
            {
                rows.AddRange(BuildMemberBlock(
                    member,
                    "member",
                    width,
                    _onlineUserIds.Contains(member.UserId)));
            }
        }

        return rows;
    }

    private static List<IRenderable> BuildMemberBlock(
        ServerMemberDto member,
        string role,
        int width,
        bool online)
    {
        const string indent = "  ";
        const string status = "● ";
        int inner = Math.Max(1, width - indent.Length - status.Length);
        string displayName = string.IsNullOrWhiteSpace(member.DisplayName)
            ? string.IsNullOrWhiteSpace(member.UserName)
                ? "Unknown"
                : member.UserName
            : member.DisplayName;
        string userName = string.IsNullOrWhiteSpace(member.UserName)
            ? ""
            : member.UserName;
        string label = $"({role})";
        string labelColor = role == "owner" ? AmberHex : MutedHex;

        int fixedWidth = label.Length + 1 + (userName.Length > 0 ? 3 : 0);
        int available = Math.Max(1, inner - fixedWidth);
        int userRoom = Math.Max(1, available / 2);
        string shownUser = Crop(userName, userRoom);
        int displayRoom = Math.Max(1, available - shownUser.Length);
        string shownDisplay = Crop(displayName, displayRoom);

        string statusMarkup = online
            ? $"[{GreenHex}]●[/] "
            : $"[{MutedHex}]○[/] ";
        string markup =
            $"{indent}{statusMarkup}[{labelColor}]{label}[/] " +
            $"[bold white]{Escape(shownDisplay)}[/]";

        if (shownUser.Length > 0)
        {
            markup += $" [grey]-[/] [{AccentHex}]{Escape(shownUser)}[/]";
        }

        return [new Markup(markup)];
    }

    private List<IRenderable> BuildHelpRows(int width)
    {
        (string Text, bool Header)[] entries =
        [
            ("Keyboard", true),
            ("", false),
            ("Tab / Shift+Tab   focus", false),
            ("Left / Right      move focus", false),
            ("Up / Down         move", false),
            ("PgUp / PgDn       scroll chat", false),
            ("Home              jump to oldest", false),
            ("End               jump to latest", false),
            ("", false),
            ("Ctrl+K / F2       quick switch", false),
            ("i                 start typing", false),
            ("/                 search messages", false),
            ("type @             autocomplete members", false),
            (_sendOnCtrlEnter
                ? "Ctrl+Enter        send"
                : "Enter             send / open / jump", false),
            (_sendOnCtrlEnter
                ? "Enter             newline"
                : "Shift+Enter       newline", false),
            ("Ctrl+J            newline", false),
            ("Ctrl+E / F3       emoji picker", false),
            ("F4                paste clipboard image", false),
            ("Esc               stop typing / close", false),
            ("", false),
            ("n                 create server", false),
            ("c                 create channel", false),
            ("j                 join server by GUID", false),
            ("u                 next unread channel", false),
            ("r                 reply to selected message", false),
            ("x                 add/remove reaction", false),
            ("v                 show server details", false),
            ("p                 my profile", false),
            ("  d / u           edit name / username", false),
            ("  a               avatar (type to search)", false),
            ("  x               remove avatar", false),
            ("f                 send a file", false),
            ("o                 open attachment", false),
            ("e                 edit selected message", false),
            ("d                 delete selected message", false),
            ("F5                refresh everything", false),
            ("F1 / ?            toggle this help", false),
            ("q / Ctrl+C        quit", false),
            ("", false),
            ("Commands", true),
            ("", false),
            ("/help or /keys    show this help", false),
            ("//text            send a message starting with /", false),
            ("/refresh          refresh everything", false),
            ("/quit             quit", false)
        ];

        List<IRenderable> rows = [];
        foreach ((string text, bool header) in entries)
        {
            string cropped = Crop(text, width);
            rows.Add(new Markup(header
                ? $"[bold {AccentHex}]{Escape(cropped)}[/]"
                : $"[grey]{Escape(cropped)}[/]"));
        }

        if (rows.Count == 0)
        {
            rows.Add(new Markup(""));
        }

        return rows;
    }

    private static Markup Divider(int width) =>
        new($"[{DividerHex}]{new string('─', Math.Max(1, width))}[/]");

    private static string HintLine(
        params (string Key, string Label)[] hints) =>
        string.Join(
            "  ",
            hints.Select(hint =>
                $"[bold {AccentHex}]{Escape(hint.Key)}[/] " +
                $"[grey]{Escape(hint.Label)}[/]"));

    private static IRenderable MakeNotice(
        string header,
        IReadOnlyList<string> lines,
        int width)
    {
        int inner = Math.Max(1, width - 4);
        List<IRenderable> rows = [new Markup("")];

        foreach (string line in lines)
        {
            rows.Add(new Markup($"[grey]{Escape(Crop(line, inner))}[/]"));
        }

        return MakePanel(new Rows(rows), header, focused: false);
    }

    private static Panel MakePanel(
        string markup,
        string? header,
        bool focused)
    {
        return new Panel(new Markup(markup) { Overflow = Overflow.Crop })
        {
            Header = header is null ? null : new PanelHeader(header, Justify.Left),
            Expand = true,
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(foreground: focused ? AccentColor : BorderColor),
            Padding = new Padding(1, 0, 1, 0)
        };
    }

    private static Panel MakePanel(
        Renderable rows,
        string header,
        bool focused)
    {
        return new Panel(rows)
        {
            Header = new PanelHeader(header, Justify.Left),
            Expand = true,
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(foreground: focused ? AccentColor : BorderColor),
            Padding = new Padding(1, 0, 1, 0)
        };
    }

    private static (int Start, int Count) VisibleWindow(
        int total,
        int selected,
        int capacity)
    {
        if (total <= 0 || capacity <= 0)
        {
            return (0, 0);
        }

        if (total <= capacity)
        {
            return (0, total);
        }

        int start = Math.Clamp(selected - capacity / 2, 0, total - capacity);
        return (start, capacity);
    }

    private static List<string> WrapText(string text, int width)
    {
        width = Math.Max(1, width);
        List<string> result = [];
        string normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');

        foreach (string rawLine in normalized.Split('\n'))
        {
            if (rawLine.Length == 0)
            {
                result.Add("");
                continue;
            }

            string remaining = rawLine;

            while (remaining.Length > width)
            {
                int split = remaining.LastIndexOf(' ', Math.Min(width, remaining.Length - 1));
                if (split <= 0)
                {
                    split = width;
                }

                result.Add(remaining[..split].TrimEnd());
                remaining = remaining[split..].TrimStart();
            }

            result.Add(remaining);
        }

        return result.Count == 0 ? [""] : result;
    }

    private static string ShortId(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return "unknown";
        }

        return userId.Length <= 8 ? userId : userId[..8];
    }

    private static string Crop(string text, int width)
    {
        if (width <= 0)
        {
            return "";
        }

        return text.Length <= width ? text : text[..width];
    }

    private static string CropTail(string text, int width)
    {
        if (width <= 0)
        {
            return "";
        }

        return text.Length <= width ? text : text[^width..];
    }

    private static string PadRight(string text, int width)
    {
        return text.Length >= width
            ? Crop(text, width)
            : text + new string(' ', width - text.Length);
    }

    private static string Escape(string text) => Markup.Escape(text);

    private static int GetConsoleWidth()
    {
        try
        {
            return Math.Max(20, Console.WindowWidth);
        }
        catch (IOException)
        {
            return Math.Max(20, AnsiConsole.Profile.Width);
        }
    }

    private static int GetConsoleHeight()
    {
        try
        {
            return Math.Max(10, Console.WindowHeight);
        }
        catch (IOException)
        {
            return Math.Max(10, AnsiConsole.Profile.Height);
        }
    }
}
