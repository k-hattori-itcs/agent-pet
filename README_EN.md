# AgentCompanion

[![Release](https://img.shields.io/github/v/release/k-hattori-itcs/agent-companion?label=version)](https://github.com/k-hattori-itcs/agent-companion/releases/latest)
[![Build](https://github.com/k-hattori-itcs/agent-companion/actions/workflows/build.yml/badge.svg)](https://github.com/k-hattori-itcs/agent-companion/actions/workflows/build.yml)

AgentCompanion is a Windows desktop companion app that shows Codex / Claude activity and usage around a draggable desktop character.

It is a customized derivative of [sugar301/TokenPet](https://github.com/sugar301/TokenPet). AgentCompanion adds local Codex / Claude status monitoring, always-visible token rings, app launch/focus behavior, Japanese settings UI, and bundled Koharu / Luna characters.

## UI Images

Public UI image generated from spritesheet-based character previews, with status bubbles and token rings:

![AgentCompanion desktop view](docs/assets/agentcompanion-desktop.png)

Settings UI image for character appearance, animations, status monitoring, and startup:

![AgentCompanion character settings](docs/assets/settings-characters.png)

![AgentCompanion connection settings](docs/assets/settings-connection.png)

## Features

- Shows the latest Codex task status in a bubble
- Shows the latest Claude Code session status in a bubble
- Displays usage with rings around the character
- Supports Claude short-window and weekly usage rings
- Switches character actions for working, completion, and error states
- Drag the character across monitors
- Double-click to open or focus Codex / VSCode
- Switch between Koharu and Luna
- Tray menu for show/hide, settings, and exit
- Optional per-install Windows startup registration

## Requirements

- Windows 10 / 11
- The GitHub Actions artifact is self-contained and does not require a separate .NET Runtime install
- .NET 8 SDK for building from source

## Quick Start

```powershell
dotnet restore AgentCompanion.sln
dotnet publish AgentCompanion.csproj -c Release -r win-x64 --self-contained true -o .\publish\AgentCompanion
.\publish\AgentCompanion\AgentCompanion.exe
```

To publish with a specific default executable icon, pass `-p:AgentCompanionIcon=favicon-koharu.ico` or `-p:AgentCompanionIcon=favicon-luna.ico`. The character appearance and Codex / Claude status provider are still selected separately in the settings window.

See [SETUP.md](./SETUP.md) for detailed Japanese setup instructions, including how to add character packages.

## Claude Monitoring Limitations

Claude monitoring targets local history written by Claude Code CLI, including sessions launched from the VSCode integrated terminal. AgentCompanion reads `projects/**/*.jsonl` for activity status and uses Claude Code OAuth to request the exact five-hour and weekly utilization from Anthropic.

Limitations:

- It does not directly monitor Claude Web, Claude Desktop, or VSCode extension-only UI state.
- If Claude Code CLI does not write local JSONL history, the status bubble will not update.
- Usage priority is: Claude Code OAuth usage response, Claude Code statusline cache (`agentcompanion-rate-limits.json`, with the legacy `agentpet-rate-limits.json` accepted during migration), then local-history estimates. Estimated labels include `~`. The OAuth usage endpoint is not a documented public API and may change; AgentCompanion automatically falls back when it is unavailable.
- Set `Claude Home` and `VSCode Workspace` explicitly when they differ from the defaults.

## Privacy and local data

AgentCompanion sends no product telemetry and does not call an external LLM to summarize activity. Codex monitoring reads `%USERPROFILE%/.codex/sessions/**/rollout-*.jsonl`. Claude monitoring reads local Claude Code history and credentials, then sends a read-only authenticated GET request to `https://api.anthropic.com/api/oauth/usage`. The OAuth token is never copied, persisted, or logged by AgentCompanion.

Settings, token history, proxy targets, character packages, and logs stay under the executable folder's `pet_data` directory. `agentcompanion.log` is capped and rotated at 1 MB. `debug.log` is written only when proxy debug logging is explicitly enabled and is rotated at 2 MB.

The optional API proxy listens only on `127.0.0.1`. It forwards the caller's Authorization header to the validated upstream TLS endpoint but does not persist API keys or request/response bodies. Unknown prefixes fail closed instead of falling back to another target. The proxy is limited to Content-Length based OpenAI-compatible JSON APIs; Transfer-Encoding and HTTP pipelining requests are rejected.
## License

MIT License.

AgentCompanion is derived from TokenPet by sugar301. See [LICENSE](./LICENSE), [NOTICE](./NOTICE), and [THIRD_PARTY_NOTICES.md](./THIRD_PARTY_NOTICES.md). The internal namespace is `AgentCompanion`, while the upstream TokenPet attribution remains in the license documents.
