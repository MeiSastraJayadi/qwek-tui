# Contributing to Qwek

Thanks for taking the time to contribute. This guide covers the basics for
reporting issues and submitting changes.

## Requirements

- .NET 10 SDK
- Windows 10+ or Linux
- A running Qwek-compatible backend for manual testing

## Development setup

Fork and clone the repository, then build the project from the repository root:

```bash
dotnet build Qwek.csproj
```

Run the TUI against a local backend:

```bash
dotnet run -- --url http://localhost:5206
```

See the [README](README.md) for configuration, keybindings, and backend details.

## Making changes

1. Open an issue first for large features or behavioral changes so the approach
   can be discussed.
2. Create a focused branch from `master`, for example
   `fix/composer-newline` or `feat/quick-switcher`.
3. Keep pull requests small and limited to a single concern.
4. Follow the existing C# style and project conventions. Nullable reference
   types are enabled.
5. Avoid adding new dependencies unless they are necessary and discussed in the
   issue first.
6. Do not commit secrets, tokens, profile data, or generated build output.

## Testing

There is no automated test suite yet. Before opening a pull request:

- Make sure `dotnet build Qwek.csproj` succeeds with no new warnings.
- Manually smoke-test the affected flows against a backend.
- Check both Windows and Linux behavior when your change is platform-specific.

Include the commands you ran and the scenarios you tested in the pull request
description.

## Commit messages

Use short, imperative subject lines, for example:

```text
Fix composer cursor after emoji insert
Add unread badge to channel list
```

Add context in the body when the change is not obvious.

## Pull requests

- Describe the problem and the solution.
- Link related issues.
- Include screenshots or terminal recordings for visible TUI changes.
- Confirm that you have tested the change.

By contributing, you agree that your contributions are licensed under the
[MIT License](LICENSE).
