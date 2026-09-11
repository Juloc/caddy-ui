# Multilingual UI contract

Caddy UI supports German (`de`) and English (`en`). The configured product default is German.

English remains the neutral source language for localization keys. When a translated resource entry is missing, ASP.NET localization therefore displays the English source key; this source-key fallback is not the same as the user's default UI culture.

## Culture resolution

For an authenticated request, the UI culture is resolved in this order:

1. the explicit culture cookie when it contains a supported culture;
2. the authenticated user's stored language preference when it contains a supported culture;
3. `Localization:DefaultCulture` from runtime configuration, currently `de`.

A missing, removed or unsupported stored preference must fall back to the configured default culture, not directly to English.

The currently supported cultures are configured under `Localization:SupportedCultures`. English is always available as the source-key fallback so untranslated keys remain readable.

## User preference

Authenticated users select their interface language under **User settings**. The preference is stored on the user account and mirrored to an essential HTTP-only culture cookie after saving.

Until a user saves a valid preference, the settings page must show the configured default culture as selected. Saving a supported preference takes effect on the redirected request and subsequent sessions.

## Resource ownership

Shared application-shell and cross-feature strings use `IStringLocalizer<SharedResource>`.

Large feature surfaces may use a focused resource marker when this keeps ownership clear, for example `RoutingResource` for the domain-first routing overview and `SettingsResource` for settings-specific copy. Resource keys are written in English and culture-specific `.resx` files provide translations.

Do not add language-specific conditionals to Razor pages. Browser timestamps use the active document language and local device time zone.

## Adding a language

1. Add the culture to `Localization:SupportedCultures`.
2. Add matching culture-specific resource files for the shared and feature resource catalogs used by the relevant UI.
3. Translate the English source keys.
4. Add localization and rendering tests for the new culture.
5. Verify the language selector, culture cookie and authenticated preference flow in browser acceptance.

## Boundaries

External provider and Caddy error payloads remain verbatim diagnostic evidence when no matching resource key exists. Secrets are never passed into localization resources, HTML, logs or diagnostics.
