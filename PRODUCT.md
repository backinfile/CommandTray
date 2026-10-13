# CommandTray product

<!-- impeccable:product-schema 1 -->

## Platform

Windows 10/11 x64 desktop application, built with .NET 10 Windows Forms.

## Users

People running local command-line services who need to start, stop, inspect, and configure them from the Windows notification area. The user confirmed the service manager and log viewer as the redesign scope.

## Product Purpose

Keep local services reachable without keeping terminal windows open. A successful session makes the selected service's state clear and its next action easy to find.

## Capabilities and Constraints

Preserve service lifecycle operations, process-tree ownership, configurable commands and directories, environment variables with DPAPI-protected secrets, automatic startup/recovery, rolling logs, JSON import/export, and the local MCP endpoint. Closing the manager hides it in the tray. Chinese and English must both be fully supported, including a language selector.

## Brand Commitments

Keep the CommandTray name and existing application icon. Use concise, actionable desktop UI copy.

## Evidence on Hand

README.md, README.en.md, src/CommandTray, and the existing tests describe the shipped functionality. No service statistics or health measurements beyond supervisor state and PID are available.

## Product Principles

- Put everyday service actions ahead of infrequently used application settings.
- Preserve edits and explain validation failures before executing a service.
- Show status in words as well as color.
- Respect Windows keyboard, focus, scaling, and tray conventions.
