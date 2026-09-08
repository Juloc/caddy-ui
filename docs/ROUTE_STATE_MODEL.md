# Route desired and runtime state

Status: active  
Introduced for: issue #72

## Purpose

PostgreSQL stores the desired route configuration. It does not by itself prove which route configuration Caddy is currently serving.

The route overview therefore treats these as separate states:

- **Desired state** — current managed routes in PostgreSQL.
- **Active state** — the content currently present in the configured managed Caddy fragment.
- **Revision state** — immutable generated route revisions used to identify known managed-fragment contents.

## Runtime source of truth

The active managed fragment is read from `Routing:ManagedFragmentPath` regardless of whether the current write mode is `active`, `shadow` or `disabled`.

Its normalized SHA-256 digest is matched against stored route revisions.

This deliberately does not rely only on the `route_revisions.applied` flag. A rollback can restore an older managed fragment, and the content actually present on disk is authoritative for the route overview.

If a non-empty managed fragment does not match any stored revision, the runtime state is **Unknown**. The UI must not invent an active-route count in that case.

## Per-route fingerprints

Revision manifests include a deterministic SHA-256 fingerprint for every enabled route. The fingerprint covers the route definition and the certificate/access inputs that influence generated route behavior.

This allows the UI to distinguish a route that is still unchanged and active from another route that has pending changes in the same global revision.

Older manifests without fingerprints are supported conservatively: when the global revision differs, an existing route is shown as requiring Apply instead of being falsely shown as applied.

## Route states

| State | Meaning |
| --- | --- |
| `Applied` | The route exists in the active known revision and its fingerprint matches the desired route. |
| `ApplyRequired` | The route is active, but its desired generated state differs. |
| `NewDraft` | The route is enabled in PostgreSQL but is not present in the active revision. |
| `PendingRemoval` | The desired route is disabled, or has been deleted, while the active revision still contains it. |
| `Disabled` | The desired route is disabled and no active revision contains it. |
| `Unknown` | The managed fragment cannot be mapped safely to a known revision. |

Deleted routes that are still active remain visible in a dedicated **pending removal** section until Apply removes them from the managed fragment.

## Apply failure behavior

A failed Apply must leave the previous managed fragment active through the existing automatic rollback path.

When the failed revision still represents the current desired state, the route overview surfaces the failed Apply and continues to show the previous active revision as runtime truth.

## Convergence

After a successful Apply:

1. the managed fragment digest matches the applied revision;
2. desired and active global digests match;
3. unchanged enabled routes resolve to `Applied`;
4. disabled routes resolve to `Disabled`;
5. deleted pending-removal entries disappear.

Regression coverage for this lifecycle is in `RouteRuntimeStateIntegrationTests`.
