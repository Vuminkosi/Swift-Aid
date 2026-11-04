# Ellai specialist tool access matrix

This inventory describes the source implementation, not a claim that every role is complete, deployed, or operational. The specification's 19 roles are listed below. The supervisor is separate. Interactive allowlists are defined by `EllaiSpecialistRegistry`; the broker also intersects them with the parent session's authorized tools. A feature flag never grants record access. Tool arguments are defined in `EllaiToolContractCatalog` and execution is mediated by `EllaiToolBroker`.

## Interactive specialists

The following 11 profiles are enabled in the checked-in Gateway defaults. Runtime configuration, provider readiness, authorization and service deployment still determine actual availability.

| Identity | Role | Admitted model-visible tools | Implemented scope and material gaps |
|---|---|---|---|
| `core.case` | Case Intelligence | `ellai_state` (queue projection), `ellai_case_timeline` | Authorized owned-case queue and case history; optionally reads current related-case suggestions when both cases are readable. General case search and evidence tools remain incomplete. |
| `core.analytics` | Analytics | `ellai_owned_case_count` | Authorized owned-case count over a requested window. Comparisons, denominators and broader metrics are missing. |
| `core.geospatial` | Geospatial Intelligence | `ellai_locations` | Bounded location registry lookup. Spatial queries, map selection and comparisons are missing. |
| `core.reporting` | Reporting | `ellai_ui_catalogue`, `ellai_ui_observe` | Grounded report UI discovery. This delegate does not itself save or submit a report. |
| `core.ui` | UI Intelligence | `ellai_ui_catalogue`, `ellai_ui_observe` | Entitled control discovery and current observation; guidance has separate admission and validation. Prepared navigation and selected guest report steps are partial. |
| `core.surveillance` | Surveillance Intelligence | `ellai_classification_snapshot`, `ellai_surveillance_signals` | Reads authorized persisted classifications and candidate signals. It does not establish an incident or send alerts. |
| `core.workflow` | Workflow | `ellai_receipts` | Reads receipts bound to the current session. General workflow planning, enqueue and cancellation tools are missing. |
| `core.knowledge` | Knowledge | `ellai_docs_search`, `ellai_docs_get` | Approved technical-document references. Permission-scoped Qdrant knowledge retrieval is missing. |
| `ops.spatial` | Spatial Analysis | `ellai_owned_region_counts` | Officer-owned regional counts over 30 days, with suppression. Scheduled PostGIS hotspots and broader spatial analysis are missing. |
| `community.sessions` | Community Sessions | `ellai_public_sessions` | Reads published public sessions. The delegate does not create, schedule or invite. |
| `community.health-research` | Health Knowledge Research | `ellai_approved_health_sources` | Returns current approved source metadata from an explicitly configured registry; the default registry is empty. Source text retrieval and evidence packets are missing. |

## Operational roles

These are bounded background workflows, not model-visible tool grants. Their schedulers are disabled by default and require explicit workspace, organization and policy configuration. Their checked-in code does not prove a live worker is running. See `docs/ellai-operational-schedule-configuration.md` for configuration and rollout requirements.

| Identity | Role | Existing implementation boundary | Material gaps |
|---|---|---|---|
| `ops.enrichment` | Case Enrichment | `EllaiEnrichmentWork`, classification backfill and result registry | Classification recovery and committed attribution only; complete translation, location and enrichment orchestration is missing. |
| `ops.organization` | Case Organization | `EllaiOrganizationWork`, organization suggestion and decision services | Configured sector-to-queue proposals and owner decisions only; actual assignment, grouping and merges are not performed. |
| `ops.followup` | Case Follow-up | `EllaiFollowUpWork`, follow-up review service | Bounded due-work and configured clarification proposals; outbound consent and communication workflows are missing. |
| `ops.quality` | Data Quality | `EllaiQualityWork`, report quality rules and case timeline projection | Bounded deterministic quality assessment; full repair proposals and quality workflow are incomplete. |
| `ops.correlation` | Case Correlation | `EllaiCorrelationWork`, `CaseCorrelationQuery`, correlation decision service | Unconfirmed recent candidates and authorized review; source identifier and Qdrant matching, confirmed links and full timeline linkage are missing. |

## Deferred roles

The user directed that these three roles remain unimplemented while the others are enriched. Stored community-feedback aggregate and review endpoints are partial infrastructure and do not constitute a working research specialist.

| Identity | Role | Current specialist tools | Status |
|---|---|---|---|
| `community.research` | Community Research | MISSING | No operational specialist or Ellai research retrieval. |
| `community.education` | Educational Content | MISSING | Unimplemented; repository policy prohibits AI-authored health education. |
| `community.tips` | Tips & Prevention | MISSING | Unimplemented; repository policy prohibits medical advice and AI-authored health education. |

## Shared enforcement and outstanding work

The logical supervisor delegates within the existing Ellai session. Its own broker access does not override the child's allowlist or the caller's permissions. Each interactive profile has a server-owned call budget and timeout; tool-specific result bounds, replay checks and authorization remain enforced in the broker and application services. Operational roles use fenced schedules, bounded work and domain authorization, not unrestricted model execution. This matrix does not claim that every conceptual semantic tool in the design is implemented: missing tools and complete output contracts remain work against the specification.

`EllaiSpecialistIdentities` supplies descriptive IDs, names, current boundaries and implementation kinds for all 19 roles; it does not enable roles or grant tools. `EllaiSpecialistRegistry` supplies the 11 interactive profile flags, allowlists and limits. A required-permission manifest, handoff policy, per-tool audit/timeout/side-effect catalog, and operational role registry are still missing. Verify each boundary against the current source and actual environment before treating it as operational.
