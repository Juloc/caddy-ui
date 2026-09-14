# Managed mini-sites

Caddy UI can manage small public static sites directly in the generated Caddy configuration. This is intended for short landing pages, OAuth branding pages, privacy policies, terms, and similar content that does not justify another web container.

## Model

A mini-site is stored as a normal managed route with kind `static_site`. The route configuration is the canonical source for title, optional contact email, home text, privacy text, and terms text. No separate host directory, sidecar container, or hand-written Caddy fragment is required.

Mini-sites always use the host root (`/`) and remain public. The generated document is reachable at `/`, `/privacy`, and `/terms`; the browser selects the matching section while the full legal content remains present in the HTML source. User-entered content is HTML-encoded before rendering.

## Apply behavior

The editor follows the existing managed-route workflow:

- **Save** persists the desired state without changing the active Caddy configuration.
- **Save and activate** creates a revision, validates the complete generated Caddy configuration, applies it atomically, verifies the reload, and uses the existing rollback behavior on failure.
- Deleting a mini-site removes the desired route. If the route is still active, the UI reports that a removal apply is required.

## Google OAuth example

For an OAuth branding site, create a mini-site such as `paperless-backup.example.com`, then use these public URLs in the provider configuration:

- Home: `https://paperless-backup.example.com/`
- Privacy: `https://paperless-backup.example.com/privacy`
- Terms: `https://paperless-backup.example.com/terms`

The editor includes a small Google OAuth starter template. Review and adapt the generated text before publishing it.
