# Wildcard certificate policy

For every domain assigned to a configured Netcup provider, managed routes use Caddy-managed DNS-01 certificates for wildcard hosts.

- Caddy obtains and renews `*.example.com` through the Netcup DNS-01 provider.
- Direct subdomains reuse the base wildcard certificate instead of requesting separate certificates.
- The route editor accepts a wildcard only as the complete leading DNS label: `*`, `*.os`, or `*.internal.apps` are valid; `foo.*`, `*foo`, and multiple wildcard labels are rejected.
- A nested wildcard route such as subdomain `*.os` on managed domain `example.com` produces the host and certificate subject `*.os.example.com`. It does not incorrectly reuse `*.example.com`, because that certificate covers only one DNS label.
- Nested wildcard route certificates reuse the DNS provider configured for the owning managed domain and are generated through DNS-01.
- Non-wildcard deeper nested hosts keep their existing certificate-mode behavior.
- Existing route data without a certificate mode migrates implicitly to the domain default.

Example for JulOS dynamic web-app hosts:

```text
Managed domain: juloc.de
Subdomain:      *.os
Result:         *.os.juloc.de
```

The generated Caddy site address is `*.os.juloc.de`, so hosts such as `wa123.os.juloc.de` are matched while `foo.bar.os.juloc.de` is not.
