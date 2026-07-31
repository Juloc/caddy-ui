# Multilingual UI contract

Caddy UI is English-first. The default and fallback culture is `en`.

## User preference

Authenticated users select their interface language under **User settings**. The preference is stored on the user account and restored into the authenticated session and an essential HTTP-only culture cookie. Unsupported or removed culture values fall back to English.

## Adding a language

1. Add the culture name and display name to `UiCultureCatalog` or the configured supported-culture list.
2. Add `Resources/SharedResource.<culture>.resx`.
3. Translate the shared resource keys. English resource keys are the source text and fallback.
4. Add localization and rendering tests for the new culture.

Do not add language-specific conditionals to Razor pages. UI text uses `IStringLocalizer<SharedResource>` and browser timestamps use the active document language and local device time zone.

## Boundaries

External provider and Caddy error payloads remain verbatim diagnostic evidence when no matching resource key exists. Secrets are never passed into localization resources, HTML, logs, or diagnostics.
