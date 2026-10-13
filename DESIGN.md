---
name: CommandTray
description: A clear, modern Windows service workspace
colors:
  primary: "#245DDA"
  primary-deep: "#194AB8"
  canvas: "#F6F8FC"
  surface: "#FFFFFF"
  text: "#202B3D"
  muted: "#58677C"
  border: "#DAE1ED"
  selected: "#E8EFFF"
  console: "#182131"
typography:
  body:
    fontFamily: Segoe UI
    fontSize: 10pt
  title:
    fontFamily: Segoe UI
    fontSize: 18pt
    fontWeight: 700
  code:
    fontFamily: Consolas
    fontSize: 10pt
rounded:
  control: 6px
spacing:
  tight: 8px
  group: 16px
  section: 24px
---

# Design System: CommandTray

## Overview

**Creative North Star: "The service workspace"**

A restrained Windows workspace for repeated daily operations. Cool neutral surfaces frame a clear service list; blue identifies the selected service and primary actions. Actual service state, commands, and logs supply the content. The physical scene is a Windows desktop used alongside editors and browsers, with a light workspace and dark log reading surface.

## Colors

Use the primary color for actionable emphasis and selection. Green, amber, and red accompany explicit state labels. Muted copy must remain readable. The console uses separate foreground colors for normal, warning, and error output.

## Typography

Segoe UI carries native controls, headings, and explanatory copy. Consolas is reserved for commands, endpoints, environment entries, and logs. UI text follows the selected Chinese or English language; avoid repeating both translations on every control.

## Layout

Use a fixed service rail beside a flexible workspace. Separate the header, editing surface, and persistent save bar. Group configuration into basic settings, behavior, and environment tabs. Application settings have their own workspace entry. Native DPI scaling applies to every layout and painted control. Smaller windows retain scrollable editing and readable actions.

Explicit heading and code fonts scale from a 96-DPI baseline after native layout. Repeated monitor changes must not accumulate font scaling. Toolbar rows wrap before overlapping the editor, and tabs and list rows scale with the monitor.

## Elevation & Depth

Use tonal surfaces and quiet separators. Avoid ornamental shadows and nested cards.

## Shapes

Buttons have modest curved corners. Inputs remain native text fields. State badges have compact curved corners and no interaction affordance.

## Components

- Tabs: a flat white strip, quiet baseline, and blue underline with bold text for selection; no native 3-D frame. Widths reserve bold text space to prevent movement. Only the selected tab is in the Tab order; arrows and Home/End navigate the strip, Ctrl+Tab and Ctrl+Shift+Tab cycle pages. Pages retain unsaved input and expose accessible tab/selection roles.
- Buttons: 40px targets (32px for inline actions), distinct hover/pressed/disabled states and an outer keyboard focus ring. Blue identifies the primary service action and the separate save action; bordered secondary actions and quiet utility actions establish hierarchy. Delete is a separate red text action with confirmation.
- Service operations: Start and Stop share one position and change with service state. Keep the active command in place until completion, show localized progress text, reserve its width, and disable repeat execution while busy.
- MCP settings: put the setting title and status together on the left with a centered toggle on the right. Give the read-only connection address its own label and vertically centered copy action. Copy feedback keeps the same button width; toggles retain a 40px target and visible keyboard focus. DPI-driven list refreshes must preserve the settings page.
- Service list: two lines per service, ellipsis for long names, state dot plus words, distinct selected background, keyboard focus.
- Inputs: visible labels, short helper text, native selection and keyboard editing; errors attach to the relevant field.
- Save bar: dirty/saved/error feedback, discard and save controls; errors never disappear behind the list.
- Logs: dark output area with a light action toolbar, search, follow/pause, wrapping, copy, and clear-view actions.

## Do's and Don'ts

- Do keep service actions visible while editing.
- Do preserve unsaved edits across search and global settings navigation.
- Do provide explicit Chinese and English language choices.
- Don't use color alone to communicate state.
- Don't expose secrets in exports or fabricate service health statistics.
