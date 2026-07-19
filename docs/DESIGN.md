# UI and UX Design

## Direction

The interface is a modern, calm desktop web application in the Microsoft Fluent 2 and Windows 11 style. Neutral light/dark surfaces, restrained depth, compact controls, clear hierarchy, and sparse use of a Caddy blue/teal accent create a native professional-tool feel.

Dashboard areas may use lightly elevated cards. Real workspaces such as route tables, logs, DNS records, editors, users, and audit entries stay flat, dense, and task-focused.

## Tokens

- Font: Segoe UI with system fallbacks.
- Radius: modest; avoid oversized pills and excessive rounding.
- Spacing: compact 4/8/12/16/24 scale.
- Accent: Caddy blue/teal by default, user-configurable.
- Semantic colors: success, warning, danger, information; never rely on color alone.
- Shadows: only for navigation surfaces, dashboard cards, dialogs, and transient overlays.

## Layout

- Desktop: fixed compact side navigation, command/title bar, scrollable content workspace.
- Mobile: navigation drawer and full-screen create/edit dialogs.
- Tables use sticky headers where useful, clear row selection, compact icon actions, and overflow menus.
- Forms use a simple primary section and collapsed Advanced settings.
- Destructive actions require explicit confirmation and state the affected object.

## Themes

The three-state selector is System, Light, Dark. System is the default. User choice is persisted per user. All components and charts use semantic theme tokens.

## Icons and motion

- Use one Fluent 2 icon family.
- No emojis or mixed icon libraries.
- Motion is brief and functional.
- Respect `prefers-reduced-motion`.

## Accessibility

- Full keyboard navigation and visible focus.
- Proper labels, headings, landmarks, dialog semantics, and live regions.
- WCAG AA contrast.
- Touch targets remain usable even in compact density.
- Loading, empty, partial failure, permission denied, validation, and offline states are designed explicitly.
