# Wildcard certificate policy

For every domain assigned to a configured Netcup provider, managed routes use a Caddy-managed wildcard certificate by default.

- Caddy obtains and renews `*.example.com` through the Netcup DNS-01 provider.
- Direct subdomains reuse the wildcard certificate instead of requesting separate certificates.
- Apex domains and deeper nested hosts use DNS-01 certificates because a single-label wildcard does not cover them.
- A route can explicitly select `Individual certificate`; Caddy then uses Netcup DNS-01 with `force_automate` for that host.
- Existing route data without a certificate mode migrates implicitly to the wildcard default.
