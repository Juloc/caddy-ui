# UI and UX Design

Status: mandatory

## Design system

Microsoft Fluent 2 Web is the application-wide design system for Caddy UI. It is implemented with server-rendered Razor Pages, semantic HTML, and a single CSS token alias layer. The application is not a React application and must not acquire SPA navigation, a client-rendered component tree, or a second component/CSS system.

Fluent Web Components are standards-based and may be used only as progressive enhancement when they provide clear value. A route, form, table, dialog, authentication flow, or management action must remain functional with the server-rendered HTML and its native fallback.

The implementation maps Fluent global and alias tokens to Caddy UI semantic aliases (`--ui-*`). Pages and components use only Caddy UI aliases, never hard-coded Fluent palette values or local hex values. The canonical role mapping is maintained in [UI_DESIGN_CONTRACT.md](UI_DESIGN_CONTRACT.md).

## Mandatory implementation rules

- Use native semantic elements first: landmarks, headings, links, buttons, forms, tables, lists, and dialogs.
- Use one Fluent System Icons outline family. Icon-only controls require an accessible name and a tooltip; decorative icons are hidden from assistive technology.
- Use native tables for server-rendered tabular data. Wrap wide tables and diffs in an internal horizontal scroll container; do not transform operational tables into cards solely because the viewport is narrow.
- Use a visible page title, a compact command area, and at most one dominant primary action per working surface. The shell has no persistent top bar.
- Treat every state as a designed state: loading, empty, partial failure, validation failure, unauthorized, rate-limited, offline, and successful completion.
- Preserve URL, form and browser-history behavior for every management flow. JavaScript only enhances interaction after the baseline flow works.

## Themes and tokens

The available theme modes are exactly System, Light, and Dark. System is the default, follows `prefers-color-scheme`, and is not persisted as a resolved Light or Dark value. An explicit choice is stored per user and applied using `data-theme`; `color-scheme` must match it. Do not override `forced-colors`.

Use the Fluent spacing ramp (4px base), modest radii, semantic foreground/background/stroke roles, and restrained shadows. Shadows are reserved for overlays, navigation surfaces, and dialogs. Standard text must meet 4.5:1 contrast, large text 3:1, and interactive/non-text controls 3:1 against adjacent colors.

## Components and interaction

| Area | Required behavior |
| --- | --- |
| Buttons | Buttons perform actions; links navigate. Use concrete German labels, one primary action, and separation plus confirmation for destructive actions. |
| Fields | Associate every control with a visible label. Put helper and validation text adjacent to the field; placeholders are supplementary only. |
| Tables and logs | Keep them dense, flat and semantic. Make headers clear, preserve readable disabled rows, put row actions at the end, and keep horizontal overflow contained. |
| Status | Combine a semantic status color with short text and, when useful, an icon. Never encode meaning in color alone. |
| Toolbars | Group related commands; do not wrap onto a second line. Move overflow actions into an accessible, labeled menu. |
| Dialogs | Use them for focused create/edit/confirmation work, not routine feedback. Do not nest dialogs. Desktop dialogs become full-screen task surfaces on small screens. |
| Messages | Use inline validation first, message bars for actionable page/section state, and toasts only for noncritical temporary feedback. |

## Shell, responsive design, and accessibility

The shell uses an `aside`/`nav`/`main` structure and a skip link. The inline desktop navigation is 260px wide. Below the 1024px desktop size class it becomes an accessible overlay so the dense operational workspace keeps enough width; Escape closes it and focus returns to its trigger. Desktop can use compact density, but touch targets on coarse pointers are at least 44 by 44 pixels.

Design for 320px width/400% zoom and 200% text zoom without clipping, data loss, or page-level horizontal scrolling. Reflow commands and form fields; keep table and diff overflow local. At 640–1023px use one-column workspaces where needed; from 1024px onward use the complete shell.

Keyboard focus must be visible, ordered, and restored when a dialog, drawer, popover, or mobile navigation closes. Dialogs must have a meaningful title, a safe exit action, focus management, and no nested dialog workflow. Use logical heading order, real landmarks, labels, `aria-current`, concise German accessible names, and deliberate live-region politeness. Respect `prefers-reduced-motion`; only short, functional state transitions are allowed.

## Official sources

- [Fluent 2 Design Tokens](https://fluent2.microsoft.design/design-tokens)
- [Fluent 2 Color Tokens](https://fluent2.microsoft.design/color-tokens)
- [Fluent 2 Typography](https://fluent2.microsoft.design/typography)
- [Fluent 2 Layout](https://fluent2.microsoft.design/layout)
- [Fluent 2 Accessibility](https://fluent2.microsoft.design/accessibility)
- [Fluent 2 Nav guidance](https://fluent2.microsoft.design/components/web/react/core/nav/usage)
- [Fluent 2 Dialog guidance](https://fluent2.microsoft.design/components/web/react/core/dialog/usage)
- [Fluent UI Web Components](https://learn.microsoft.com/en-us/fluent-ui/web-components/)

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

- Desktop: compact side navigation, page-level title/command area, scrollable content workspace; no persistent top bar.
- Mobile: collapsed navigation drawer by default and full-screen create/edit dialogs.
- Tables use sticky headers where useful, clear row selection, compact icon actions, and overflow menus.
- Dense request-log tables retain their table semantics on narrow screens and use an internal horizontal scroll container when needed.
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
