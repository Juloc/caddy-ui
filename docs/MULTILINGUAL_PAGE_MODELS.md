# Localized page-model feedback

Razor Page models use `LocalizedPageModel` for request-culture-aware status, validation, and operation feedback. Fixed UI messages use English resource keys. External provider and Caddy diagnostics remain unchanged when no localized resource exists.

This keeps the English fallback deterministic while allowing German and future resource catalogs to translate the same messages without language-specific branches in handlers.
