# Security Policy

## Supported versions

Security fixes are applied to the latest release and the `main` branch.

| Version | Supported |
| --- | --- |
| 1.0.x | Yes |
| Earlier versions | No |

## Reporting a vulnerability

Do not open a public issue for a suspected vulnerability. Use GitHub's private vulnerability reporting for this repository. Include the affected version, reproduction steps, impact, and any suggested mitigation. Please do not include API keys, access tokens, transcript contents, or other personal data.

## Security boundaries

AgentPet reads local Codex and Claude Code history and can optionally run a loopback-only API proxy. Imported pet packages and proxy target settings are treated as untrusted input. The proxy is not intended to be exposed outside the local machine.
