# UI screen checklist (Lane B, 2026-10-28)

Reference boards (byte-identical copies in `docs/design/references/`, originals in `designs/`):

- A — `SwiftAid Health Surveillance UI Showcase.png` (6 web + 6 mobile screens)
- B — `SwiftAid One Health UI Storyboard-1.png` (6 web + 6 mobile screens)
- C — `SwiftAid Health Management UI Showcase-2.png` (6 web + 6 mobile screens)

Status key: Implemented (route/component exists) · Partial (some coverage) · Missing (no route/component yet). "Implemented" means the route/component exists, not that visual parity or device behavior was verified.

## Web screens

| # | Reference screen | Implemented route | Components | Status |
|---|---|---|---|---|
| A1/B1/C1 | Dashboard (stat tiles, cases-by-disease, map, recent alerts/cases) | `/` (`Home.razor`) | `SwiftAidPageHeading`, `SwiftAidStatTile` (new), `SwiftAidSectionCard` (new), `SwiftAidStatusChip` (new), `NotificationFeed` | Partial |
| A2/B2 | Case Management / Case List (filter human/animal, status pills, assignees) | `/intake-coordination` (`IntakeCoordination.razor`) + `IntakeCoordinationQueue` | `IntakeCoordinationQueue`, `SwiftAidStatusChip` (new) | Partial |
| A3/B3/C2 | Case Details (summary, immutable history, follow-ups, AI insights) | `/case/{CaseId:guid}` (`CaseDetail.razor`) | `CaseTimelineView`, `SwiftAidSectionCard` (new), `SwiftAidStatusChip` (new) | Partial |
| A4/B4/C3 | Surveillance & Maps (geospatial clusters, signals) | — (no dedicated route) | `SwiftAidSectionCard` (new, shell only) | Missing |
| A5/B5/C5 | Community Education & Sessions (schedule, share, reminders) | `/sessions` (`Sessions.razor`) | `SwiftAidPageHeading`, `SwiftAidSectionCard` (new) | Partial |
| A6/C6 | Reports & Analytics (disease trends, outcomes donut) | — (no dedicated route) | `SwiftAidStatTile` (new, shell only) | Missing |
| B6 | Education Resources library (Human/Animal/One Health topics) | `/education` (`Education.razor`) | `SwiftAidPageHeading`, `SwiftAidBodyText`, `SwiftAidSectionCard` (new) | Partial |
| C4 | Outbreak Intelligence (AI-detected signals, analysis summary) | — (no dedicated route) | `SwiftAidSectionCard` (new, shell only); `SwiftAidServiceBoundaryNotice` for truthful unavailable states | Missing |

## Mobile screens (Android-first, offline capable; MAUI host owns layout)

| # | Reference screen | Implemented route/component | Components | Status |
|---|---|---|---|---|
| A-M1/B-M1/C-M1 | Home Dashboard (quick actions, sync state, recent activity) | Mobile shell (host-local) | `SwiftAidLoader`, `SwiftAidSectionCard` (new), `SwiftAidStatTile` (new) | Partial |
| A-M2/B-M2/C-M2 | Start/Report a Case (human/animal, speak/type/photo, steps) | `/report` (`Report.razor`) → `/managed-report` (`ManagedReport.razor`) | `PublicReportForm`, `PublicReportPlaceField`, `CaseReportRegistrationForm` | Partial |
| A-M3/C-M3 | Voice Recording / Voice Capture | Part of report flow | `SwiftAidLoader` (recording indicator pattern) | Partial |
| A-M4/C-M4 | AI Extracted Information / Case Summary Review | Part of report flow | `CaseTimelineView` (review pattern), `SwiftAidServiceBoundaryNotice` | Partial |
| A-M5/B-M4 | Confirmation / Submission (case ID, next steps, offline sync note) | Receipt step | `PublicReportReceipt`, `SwiftAidLoader` (pending-sync state) | Partial |
| A-M6/B-M5/C-M5 | My Cases & Follow-ups (status pills, follow-up due) | `/intake-coordination`, `/case/{id}` | `IntakeCoordinationQueue`, `SwiftAidStatusChip` (new) | Partial |
| B-M6/C-M6 | Education & Community Sessions (topics, join session) | `/education`, `/sessions` | `SwiftAidPageHeading`, `SwiftAidBodyText` | Partial |

## Notes

- No placeholder or lorem-ipsum text on any new UI surface; new components render caller-supplied text only.
- MudBlazor is the renderer for all new shared components. No Syncfusion component is introduced; see `UI-COMPONENTS.md` for the Syncfusion gate.
- Missing items (dedicated Surveillance/Map, Reports & Analytics, Outbreak Intelligence routes) are out of scope for this lane; shells (`SwiftAidSectionCard`, `SwiftAidStatTile`) are available for them.
- Visual parity, browser rendering, Android device display, screen-reader behavior, and rendered contrast acceptance are NOT VERIFIED.
