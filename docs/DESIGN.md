# UI and UX Design

## Direction

The interface is a modern, calm desktop web application in the Microsoft Fluent 2 and Windows 11 style. Neutral light/dark surfaces, restrained depth, compact controls, clear hierarchy, and sparse use of a Caddy blue/teal accent create a native professional-tool feel.

Dashboard areas may use lightly elevated cards. Real workspaces such as route tables, analytics, logs, security events, DNS records, editors, users, and audit entries stay flat, dense, and task-focused.

## Tokens

- Font: Segoe UI with system fallbacks.
- Radius: modest; avoid oversized pills and excessive rounding.
- Spacing: compact 4/8/12/16/24 scale.
- Accent: Caddy blue/teal by default, user-configurable.
- Semantic colors: success, warning, danger, information; never rely on color alone.
- Shadows: only for navigation surfaces, dashboard cards, dialogs, and transient overlays.

## Layout

- Desktop: fixed compact side navigation, command/title bar, scrollable content workspace.
- Mobile: collapsed navigation drawer by default and full-screen create/edit dialogs.
- Tables use sticky headers where useful, clear row selection, compact icon actions, and overflow menus.
- Dense request-log tables transform into stacked card-like rows on narrow screens rather than requiring a wide horizontal table.
- Forms use a simple primary section and collapsed Advanced settings.
- Destructive actions require explicit confirmation and state the affected object.

## Analytics

- Analytics is a dedicated workspace rather than an overloaded dashboard section.
- KPI cards appear first and link to filtered detail views where useful.
- Time-range controls are compact chips with `15m`, `1h`, `6h`, `24h`, `7d`, `30d`, `1y`, and Custom.
- Structured filters collapse into a compact panel and active filters remain visible as removable chips.
- Overview, Performance, Traffic, Endpoints, and Clients/IPs use one consistent tab pattern.
- Charts use locally shipped dependency-free SVG rendering, semantic theme colors, direct labels/tooltips, and drill-down links.
- Graphs stack vertically on narrow screens and resize without requiring horizontal scrolling.
- Static assets are visually separated from API/page performance where relevant.
- Human, bot, internal, and unknown client classifications use text labels in addition to any color treatment.

## Logs

- Quick filters expose common diagnostic paths such as 4xx, 5xx, slow requests, recent requests, and bots.
- Structured filters use native inputs/selects and remain URL-addressable.
- Live mode is explicit and pausable; new rows receive only a brief highlight so continuous motion does not dominate the page.
- IP addresses, normalized endpoints, metrics, and chart points link to contextual drill-downs rather than opening unrelated modal dialogs.
- Export actions are secondary and administrator-only.

## Security

- The top of Security always communicates the active protection level: Off, Balanced, Strict, or Custom.
- Overview summarizes security events and active temporary restrictions without presenting every automated event as an urgent notification.
- Threat explanations show concrete reasons and observed counts where available.
- Blocked IPs, rate limits, and login protection are separate tabs to avoid mixing observation with policy editing.
- Administrator policy forms state that changes are validated and rolled back on failure.
- Automatic decisions are presented as temporary and explainable; no UI suggests that Caddy UI provides upstream volumetric DDoS mitigation.

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
- Charts provide accessible labels and retain textual KPI/table alternatives.
- Loading, empty, partial failure, permission denied, validation, rate-limited, and offline states are designed explicitly.
