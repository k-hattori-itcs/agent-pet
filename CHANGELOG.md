# Changelog

All notable changes are documented here.

## Unreleased

- Anchor the settings window above the tray-menu selection and clamp it to the selected monitor work area.
- Preserve the character's physical multi-monitor position across hide/show without forcing it visible when Settings opens.
- Preserve customized character files while restoring missing bundled files.
- Regenerate public UI assets and add encoding and documentation-image checks to CI.

## 1.0.0 - 2026-07-24

- Rename the product to AgentCompanion and align the executable, project, UI, and documentation.
- Harden character package import against path traversal, unsafe image paths, and ZIP resource exhaustion.
- Add single-instance activation and resilient startup behavior.
- Harden the loopback proxy with fail-closed routing, synchronized settings, size limits, timeouts, and strict HTTP parsing.
- Add atomic configuration/history persistence, tests, CI quality gates, dependency monitoring, and public security documentation.
