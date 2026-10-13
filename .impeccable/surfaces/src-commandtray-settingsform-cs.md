---
version: 1
slug: "src-commandtray-settingsform-cs"
primary_target: "src/CommandTray/SettingsForm.cs"
related_targets: ["src/CommandTray/LogViewerForm.cs","src/CommandTray/UiTheme.cs"]
---

# Service workspace

Mode: Operate. Scope: Windows service manager, log viewer, tray language.
Audience: people running local command-line services on Windows.
Job: find a service, understand its real supervisor state, operate it, safely edit configuration, inspect output.
Primary actions: start/stop/restart and save; logs and website stay near service actions.
Direction: restrained Windows workspace with a service rail, flat keyboard-accessible configuration tabs, persistent save feedback, separate application settings, and dark log output. Service actions use a stable Start/Stop position, clear button hierarchy, and explicit progress states.
Signature: language switches immediately across open windows and the tray without losing edits.
Constraints: maintain process ownership, local-only MCP, DPAPI secrets, safe export, native keyboard/DPI behavior, and hide-to-tray close semantics.
Open decisions: none in the confirmed implementation scope.
