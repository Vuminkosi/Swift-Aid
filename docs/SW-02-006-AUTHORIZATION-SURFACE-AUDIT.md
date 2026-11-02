# SW-02-006 authorization surface audit - 2 November 2026

## Outcome

Shared case authorization contracts and several resource-specific authorizers exist. Gateway endpoints apply staff authorization or resource checks in the reviewed route groups. This is a targeted source map, not proof that one identical intersected policy is enforced on every read/write/sync/media/search/export/count/map/deep-link/tool surface. Keep SW-02-006 open pending a complete route-to-policy crosswalk and security regression tests.

## Evidence map

| Boundary | Source evidence | Limit |
|---|---|---|
| Shared case policy | `src/SwiftAid.Application/Authorization/CaseAccessPolicy.cs`, `ICaseAccessPolicy.cs`, `ScopedAccessGrant.cs`, `CaseAccessDescriptor.cs` | Encodes scoped decision inputs for the case access path; not every domain surface calls this exact interface. |
| Current grants and descriptors | `src/SwiftAid.Infrastructure/Authorization/PostgresCurrentAccessGrantStore.cs`, `PostgresCaseAccessDescriptorStore.cs` | PostgreSQL implementations exist; production grants/roles and all deployment data are not independently verified here. |
| Case routes | Gateway case command, detail, timeline, queue, classification, translation, import and map endpoint groups | Selected route groups require staff policy or call resource services. A comprehensive endpoint inventory, including all methods and deep links, was not completed. |
| Specialized resources | Intake review/coordination authorizers and `NotificationResourceAuthorizer.cs`; public intake/session routes have separate boundaries | Resource-specific policies exist; their parity and intersection semantics with the shared case policy need route-level review. |
| Sync and manifests | Contracts under `src/SwiftAid.Contracts/Sync/`; `ScopeManifest`/grant manifest tests exist | This audit did not prove stale/revoked scope cleanup for all sync consumers or offline actions. |
| Tests | Authorization policy, case authority, intake, notification and map test sources across test projects | No tests were run for this audit; source presence is not current route coverage or tenant-isolation proof. |

## Required closure

Create a route/resource matrix covering API, queries, Datasync/scope manifests, counts, attachments/media, AI tools, memory/cache, search, exports, maps, subscriptions and deep links. For each, record principal/scope inputs, organization/workspace/jurisdiction/sector/sensitivity intersection, revocation/freshness boundary, expected deny behavior and focused tests. Independently review tenant isolation and verify that UI visibility never substitutes for API authorization.

No data, permission or membership was changed. No tests or active security scan were run for this inventory.
