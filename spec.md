# SwiftAid 2.0 — Product Specification

> **SwiftAid: From human and animal field reports to accountable support and verified follow-through.**
>
> Every reported human or animal health concern receives a private, traceable intake record. Managed cases have an accountable staff owner; unmatched reports show their routing state honestly. Both retain evidence, next actions and synchronization state.

| Document control | Value |
|---|---|
| File | `spec.md` |
| Version | 1.4 — Multi-turn reasoning loops, product access, federation, Qdrant memory and cache |
| Prepared | 26 October 2026 |
| Product | SwiftAid 2.0 |
| Target communities | Initial organizational service focus: Zimbabwe and Eswatini; account-free reporting from any country |
| Primary application | Android-first native .NET MAUI Blazor Hybrid application |
| Supporting application | Officer, operations, and public web surfaces |
| Companion | [`implementation.md`](implementation.md) |
| Intended readers | Product owner, designers, the owner's existing multi-agent development system, engineers, testers, and eventual health/veterinary reviewers |
| Status | Specification for implementation and review; not evidence of implemented features or authorization for real-world clinical use |
| Demonstration boundary | Synthetic cases, fictitious operational identities, consented demonstration recordings, and explicitly labelled demonstration knowledge |

## How to read this specification

This document defines **what SwiftAid must do, who can do it, what people must see, and how acceptance is demonstrated**. `implementation.md` defines the implementation tasks, interfaces, packages, repository layout, and execution sequence. Read both; do not replace either with the interface illustrations.

**Source basis.** This revision builds on product spec v1.3 and implementation plan v2.3, which in turn started from product spec v1.2 and implementation plan v2.2, and applies the owner's subsequent product-first execution instruction and explicit Qdrant, federation/SSO, and account-free global-reporting requirements. The inherited Participant Guide [C01] and bonus slide [C02] remain relevant only to a separately chosen event submission. It preserves the historical origin of the earlier requirements from the complete 1,836-line original `implementation.md`. That plan is dated 25 October 2026. Its SHA-256 at preparation was `274e7226d48cc5489a2cd071e06a76c8e9143ebe99a7cf577e7a02fd6daf4849`. References such as **[I §8; P3.2]** refer to that plan's section and task IDs. The medical and event source register is inherited, not independently re-verified. Newly consulted technical documentation is listed separately in `docs/TECHNICAL-SOURCES.md`; it does not establish live account permissions, tested package compatibility, model/encoder access, prices, or clinical approval.

**Requirement language.** **MUST/MUST NOT** are acceptance obligations within the stated release scope. **SHOULD** is a preferred behavior that may be deferred with an explicit recorded reason. **MAY** is optional. A requirement is not complete merely because its screen exists. Each requirement has a stable `SA-…` identifier.

**Scope labels.** **P0** means demonstration-critical; **P1** means an extension after the complete P0 journey works; **P2** means post-hackathon/production work. **Gate** means a capability must stay disabled until its prerequisite is evidenced. These labels do not imply clinical certification.

**Explicit product clarifications.** Some interaction details were not fixed in the plan. They are identified below as **PC-01 through PC-08**, listed in §28. They are proposed defaults for implementation review, not previously approved clinical policy. No clarification invents a disease threshold, dose, legal retention period, provider capability, or institution's referral procedure.

**Conflict handling.** Apply the owner-directed product revision above and §§3.4, 6.5–6.6, 14.5, 15.7 and 18.4 over the earlier staff-only intake and cache assumptions. Historical competition clauses apply only to a separately authorized submission profile. Preserve §15.6 nonclinical action boundaries. Preserve all agreed safety, privacy, original-voice, and immutable-history constraints. Where this document adds detail absent from the plan, use the labelled clarification provisionally and record it. Where the documents genuinely disagree, log the discrepancy and resolve it explicitly before implementing the disputed behavior. Do not silently weaken either document or claim that this specification retroactively changes a tested API contract.

**Execution tooling.** Use the product owner's existing end-to-end multi-agent development system. No Superpowers installation, special subagent command, coding assistant, or orchestration product is required by SwiftAid. The runtime AI/MCP capabilities described here are application features, not a requirement to change development tooling.

---

**Revision boundary:** This is a substantive scope update, not a cosmetic rename. AI diagnosis, symptom-derived severity/priority, medical/veterinary advice, advice-generating RAG, and AI-authored health education are excluded even when a professional could approve the output. An unchanged original recording or faithful transcript remains attributed evidence, not SwiftAid's recommendation. The main AI demonstration is administrative case continuity; the full foundational support system remains intact. Eligibility is not certified by these documents. Read [competition source and decision record](docs/RULES-SOURCE-REGISTER.md).

**Paired revision:** Use this spec **v1.4** with implementation **v2.4**. Stable requirement/task IDs are retained but superseded capability behavior is explicitly replaced. Read the [change log](docs/CHANGELOG.md) before resuming work.

## Product revision decisions — 26 October 2026

**Owner-directed changes:** Qdrant is the AI memory/cache backend; login supports Facebook and Google plus organization SSO; public visitors can report human or animal concerns from any country without an account. Login is required for organizational functions, event subscriptions, assignments and escalations. **Public intake does not imply public case data.**

**Implementation choices in this revision:** use a separate private intake review/routing stage; OIDC-first organization SSO; maintain PostgreSQL authority; restrict case-specific caching to exact context matches; use guarded receipt proof for guest tracking. These implement the owner's access decisions without granting guests staff privileges. Detailed provider/session/library choices are design recommendations, not deployed integrations.

**Product execution scope:** following the owner's subsequent instruction, event dates, public-repository submission, prizes and organizer responses are not prerequisites to implement the core product. Historical event sections remain a conditional submission profile only. Medical-advice exclusions, privacy, authorization, technical tests and rights review remain product boundaries. Optional health-pattern inference stays OFF pending a separate product decision and validation; any event submission must also satisfy its own rules. New factual technical references are separated in [the technical source register](docs/TECHNICAL-SOURCES.md).

## Contents

- [1. Product purpose and outcomes](#1-product-purpose-and-outcomes)
- [2. Release scope, priorities, and non-goals](#2-release-scope-priorities-and-non-goals)
- [3. Users, organizations, and access boundaries](#3-users-organizations-and-access-boundaries)
- [4. Product information model and terminology](#4-product-information-model-and-terminology)
- [5. Case lifecycle and state semantics](#5-case-lifecycle-and-state-semantics)
- [6. Case reporting and registration](#6-case-reporting-and-registration)
- [7. Case tracking, history, and corrections](#7-case-tracking-history-and-corrections)
- [8. Assignment and coordination](#8-assignment-and-coordination)
- [9. Assistance, referrals, and support fulfilment](#9-assistance-referrals-and-support-fulfilment)
- [10. Monitoring and operational prioritization](#10-monitoring-and-operational-prioritization)
- [11. Follow-up management](#11-follow-up-management)
- [12. Escalation, resolution, and reopening](#12-escalation-resolution-and-reopening)
- [13. Communication, original voice, and transcripts](#13-communication-original-voice-and-transcripts)
- [14. Offline behavior, synchronization, and conflict recovery](#14-offline-behavior-synchronization-and-conflict-recovery)
- [15. AI assistance and human control](#15-ai-assistance-and-human-control)
- [16. Surveillance and One Health investigation](#16-surveillance-and-one-health-investigation)
- [17. Health knowledge, education, and source governance](#17-health-knowledge-education-and-source-governance)
- [18. Community education sessions, Facebook, and WhatsApp](#18-community-education-sessions-facebook-and-whatsapp)
- [19. Human and animal condition coverage](#19-human-and-animal-condition-coverage)
- [20. Web and mobile experience specification](#20-web-and-mobile-experience-specification)
- [21. Notifications, operational analytics, and exports](#21-notifications-operational-analytics-and-exports)
- [22. Privacy, security, and trust requirements](#22-privacy-security-and-trust-requirements)
- [23. Architecture constraints and integration contracts](#23-architecture-constraints-and-integration-contracts)
- [24. Quality, accessibility, and service behavior](#24-quality-accessibility-and-service-behavior)
- [25. Demonstration data and evaluation](#25-demonstration-data-and-evaluation)
- [26. End-to-end acceptance scenarios](#26-end-to-end-acceptance-scenarios)
- [27. Milestones, release criteria, and scope control](#27-milestones-release-criteria-and-scope-control)
- [28. Decisions, proposed clarifications, and unresolved gates](#28-decisions-proposed-clarifications-and-unresolved-gates)
- [29. Traceability to the implementation plan](#29-traceability-to-the-implementation-plan)
- [30. How to use this specification with the implementation plan](#30-how-to-use-this-specification-with-the-implementation-plan)
- [Appendix A. Inherited knowledge-source register](#appendix-a-inherited-knowledge-source-register)
- [Appendix B. Document provenance and review record](#appendix-b-document-provenance-and-review-record)

---

## 1. Product purpose and outcomes

### 1.1 Product definition

SwiftAid is an **AI-powered Community Health Support and One Health Case Coordination System**, not just a reporting form, an outbreak map, or a general-purpose chatbot.

Its operational responsibility is to keep concerns from being lost between initial reporting and meaningful action. A human case or animal-health event can be reported, reviewed, assigned, assisted, monitored, followed up, escalated, resolved, and reopened. Authorized descriptive views may summarize reported observations. AI health-pattern interpretation is a separately gated, optional proposal, not part of the default competition demonstration. Operational continuity is the flagship.

### 1.2 Problems the product is designed to address

The following are product problem statements supplied by the brief, not quantified research findings:

- Reporting and collaboration must remain usable when connectivity is interrupted.
- A report needs an accountable person, a next action, and a visible record of what was actually done.
- Voice and local-language observations should not be lost when transcriptions are inaccurate or unavailable.
- Human-health and animal-health teams need separate operational permissions but a controlled way to investigate potentially related signals.
- Community education should be connected to approved knowledge and officer-led sessions without exposing private cases.
- Workers should not repeatedly type known context or guess a diagnosis merely to save a concern.

### 1.3 Intended outcomes

| Outcome | Evidence SwiftAid must make possible | Not an acceptable substitute |
|---|---|---|
| Continuity | A case shows its owner, open assistance, next follow-up, escalation state, and history | A total-case chart with no actionable work |
| Accountability | Assignment acknowledgement, assistance receipt, follow-up outcome, and closure reason are recorded | Assuming that a notification means the work happened |
| Resilient reporting | Offline drafts and original recordings survive restart and reconcile after reconnecting | A success message before server acceptance |
| Reduced entry burden | Known context is reused; AI proposals are reviewed instead of retyped | Guessing unknown fields to shorten a form |
| Explainable intelligence | Factual evidence and contradictory/missing records explain an administrative continuity plan; executed actions have verifiable receipts | A diagnosis, severity label or a proposed action described as completed |
| Safe community engagement | Officers can schedule and share generic approved education invitations | Posting case discussions or health status to social channels |

### 1.4 Product invariants

**SA-VIS-01 — P0.** Each server-accepted managed case MUST have an accountable staff owner and a traceable history. Guest intake is private and traceable before promotion, with an intake queue owner or explicit NeedsRouting/NoServiceCoverage state; it must not invent a field assignee.

**SA-VIS-02 — P0.** An active case MUST display its next recorded action or explicitly show that a next action has not been arranged; the interface MUST NOT imply that nothing is outstanding merely because AI is unavailable.

**SA-VIS-03 — P0.** Human and animal reports MUST share support workflows while retaining distinct subject fields, permissions, observations, and counting semantics.

**SA-VIS-04 — P0.** AI MUST NOT generate medical/veterinary advice or clinical triage for the entry. Its factual proposals and administrative plans remain evidence-bound; authorized application services execute only separately approved administrative actions.

**SA-VIS-05 — P0.** Original evidence, uncertainty, synchronization status, and review state MUST remain visible rather than being hidden for a cleaner demonstration.

**Basis:** [I §§1–3, 16, 25].

## 2. Release scope, priorities, and non-goals

### 2.1 P0: the complete demonstration slice

The P0 product is a thin but real implementation of all ten core modules. It must demonstrate one complete human case journey, one complete animal-health journey, one nonclinical administrative continuity run, and one public education session; cross-sector health-pattern inference is not required and remains gated.

| Module | P0 obligation | Expansion after the core journey |
|---|---|---|
| Reporting and registration | Offline human/animal intake, minimal fields, typed/voice evidence, review and submission | Broader capture channels and validated languages |
| Case tracking | Immutable revisions, operational state, full readable timeline | External case-system interoperability |
| Assignment and coordination | One owner, explicit reassignment, acknowledgement, permitted collaborators | Workload optimization and inter-institution transfers |
| Assistance and support | Need, request, owner, delivery, receipt, and unfulfilled state | Inventories, partner fulfilment and resource procurement |
| Monitoring | Latest observation, open needs, due/overdue work, unanswered escalations | Validated condition-specific pathways |
| Escalation | Human-raised concern, authorized recipient, acknowledgement, action, closure | Institution-approved escalation automation |
| Follow-up | Assigned schedule, outcome, missed/unreachable handling, reschedule history | Reviewed recurring follow-up templates |
| Education | Source-linked library, offline pack, visible review/version state | Reviewed translations and institution-maintained content |
| AI/operations | Real Meta factual extraction, report-quality audit, continuity plan, approved administrative execution and verification | Optional product-approved and validated relationship exploration; event profile has separate eligibility conditions; no AI medical advice |
| Communication | Text and original-voice comments; transcript as optional derivative | Additional media and notification channels |

### 2.2 Release modes

**SA-SCP-01 — P0.** `DEMO_MODE` MUST use synthetic operational data and clearly identify it. Reference education remains human-authored/static and source-linked, with `DemoOnly` where no clinical review exists; it is not generated medical guidance.

**SA-SCP-02 — Gate.** A production configuration MUST reject demonstration-only detection rules and MUST NOT present demonstration-reviewed content as clinically approved guidance.

**SA-SCP-03 — P0.** Unsupported integrations MUST be disabled or clearly marked unavailable. Mock data, sandbox deliveries, manual sharing, and recorded demo results MUST never be represented as live verified capabilities.

**SA-SCP-04 — P0.** The nine requested human conditions and nine starter animal conditions MUST be present as operational catalogue entries. Catalogue coverage MUST NOT be described as diagnostic validation for all those conditions.

**SA-SCP-05 — P0.** Android installation and the officer web workflow MUST not depend on app-store approval, a Facebook app review, WhatsApp provisioning, access to real patients, or unpublished national datasets.

### 2.3 Explicit non-goals for the demonstration

No autonomous diagnosis, treatment selection, prescribing, disease-specific dosing, official outbreak declarations, animal movement restrictions, national forecasting, or emergency-dispatch guarantee. No comprehensive hospital EHR, billing, drug dispensing, veterinary inventory, insurance system, social network, or real-time conferencing platform.

No iOS/desktop release requirement, offline local LLM inference, automatic Facebook/WhatsApp group creation, unrestricted cross-border record exchange, custom generic replication protocol, or immediate remote erasure of disconnected devices.

**SA-SCP-06 — P0.** These non-goals MUST remain outside the critical path unless the owner explicitly changes scope and the safety and dependency impacts are recorded.

**Basis:** [I §§1.2, 2, 12.4, 18.3, 20].

### 2.4 Competition-specific scope and submission obligations

**Conditional historical profile:** this section documents optional event submission, not a calendar or organizer prerequisite for core product development. SA-CMP items about dates, publication, awards and video apply only when preparing that submission. Nonclinical, privacy and provenance requirements remain product safeguards.

**Source-derived facts.** The supplied Participant Guide requires a project fitting a problem statement and incorporating Meta models; public repositories; at most four participants, including solo entries; identifiable newly built event contributions; rights to code/data/assets; and a three-minute demo video. It lists AI-generated medical advice, basic RAG applications, simple image analyzers and simple AI-for-education chatbots in its anti-project examples. The guide's startup reuse wording is not assumed to grant the same exception to this solo indie entry. [C01 §§1, 4, 7]

**Our design response, not an organizer exemption.** Enter under **Build for Your World**. Keep the human/animal case-support platform. Constrain the AI to faithful reporting, factual data-quality review and administrative continuity. Do not generate medical/veterinary guidance, even from WHO/WOAH content or with human approval. A qualified officer may record their own decisions through ordinary case management. Health-pattern interpretation and cross-sector AI inference remain optional and disabled by default pending product approval/validation; an event submission additionally requires organizer clarification. Request a written determination of the operational-only proposal before representing it as eligible; record any narrower conditions. [C01 §4]

| ID | Priority | Requirement |
|---|---|---|
| SA-CMP-01 | P0 | Show meaningful runtime use of an eligible Meta model in a problem-statement-aligned working product; an SDK import or coding-agent use alone does not demonstrate the intended runtime contribution. |
| SA-CMP-02 | P0 | Exclude AI-generated medical/veterinary advice, diagnosis, treatment, symptom-based clinical triage, medical follow-up intervals, and advice-generating education/RAG. A disclaimer, grounded citation or human approval cannot bypass this product boundary. |
| SA-CMP-03 | P0 | Keep a source-linked reference library and human-led sessions, not an AI medical/education chatbot. AI may prepare nonmedical invitation logistics, never author or translate health recommendations for this entry. |
| SA-CMP-04 | P0 | Make the submitted repository publicly accessible and verify unauthenticated access to the repository and three-minute video. Public source code does not make runtime case data public. |
| SA-CMP-05 | P0 | Demonstrate only newly built event functionality, keep a start/end commit and contribution ledger, and disclose the earlier idea/repository/plans/assets accurately. Do not assume the startup reuse exception applies to indie participants. |
| SA-CMP-06 | P0 | Preserve the guide's October 27–November 4 building-period restriction and November 4, 9:00 PM Eastern submission deadline; exact start time remains unspecified. Planning before the event and reusable components must be transparently identified, with organizer clarification where needed. |
| SA-CMP-07 | P0 | Provide a three-minute video, code and accessible demonstration evidence showing actual new features; identify synthetic data, manual shares and any recorded result. Do not present screenshots or simulated inference as live execution. |
| SA-CMP-08 | P0 | Review code/model/data/voice/asset rights before public submission. Exclude secrets, patient/farmer information, unconsented recordings and third-party documents lacking required rights. Use synthetic controlled demo accounts. |
| SA-CMP-09 | Gate | Record organizer questions, responses, source and scope in a decision ledger. Cross-sector health-pattern AI stays disabled unless specifically clarified; no-response is not approval. No document can guarantee the judges' eligibility decision. |
| SA-CMP-10 | P0 | Use the stated rubric—Impact 30%, Meta tooling 25%, Technical execution 20%, Demo 25%—to organize evidence, not invent extra points for self-hosting or a promised winning score. |
| SA-CMP-11 | P1/Gate | Claim an open-weight bonus attempt only with real Muse Glimmer or Muse Spark Open execution on own hardware or partner inference and a verification route acceptable to organizers. API-only usage or a configured but unused fallback is insufficient evidence. |
| SA-CMP-12 | P0 | Demonstrate bounded autonomy, task effectiveness and practical usefulness for the agentic use-case goal; do not remove protected approvals to inflate an autonomy claim. |
| SA-CMP-13 | P1 | Viral/community participation is opt-in submission/promotion work, not a product automation requirement. Submit the X demo post link when pursuing that award; respect the stated engagement cutoff and one vote per verified account. No fabricated engagement or automated voting. |
| SA-CMP-14 | P0 | Treat the guide's billing spend-limit notification as a notification, not a documented hard cap. Verify credited balance and impose gateway budgets; separate partner credits and GPU/hosting costs. |

### 2.5 Bonus-prize strategy from the supplied guide and slide

| Prize | Stated amount | Source condition | SwiftAid decision |
|---|---|---|---|
| Most Viral Project | $20,000 | Demo posted on X and link submitted; engagement counted through November 8, 11:59 PM ET on the slide (guide says EOD). | Optional honest promotion after a working demo; no case data. |
| Best Build on Meta Open Weights | $10,000 | Muse Glimmer or Muse Spark Open on own hardware or partner inference; flagged in round one and infrastructure partner verifies. | Optional P1 real inference lane; no claim that Meta API credit covers GPU or partner cost. Ask how own-hardware verification is handled. |
| Community Vote | $10,000 | Most gallery votes; one vote per verified account; winner November 16. | Prepare an accessible public project page/video; do not automate votes. |
| Best Agentic Use-Case | $5,000 | Meaningful tasks/decisions or multi-step workflows; autonomy, effectiveness and practical usefulness. | Primary bonus target: evidence-backed administrative plan → approval → execution → verification. |
| Best Creative Build | $5,000 | Original creative project using Muse Image or Muse Wonder. | Not a SwiftAid target; do not add decorative model use just to claim eligibility. |

The five amounts total $50,000. Bonus selection, possible stacking, and own-hardware verification details not specified by the supplied material remain organizer questions. The main rubric does not award a stated self-hosting uplift. These are target choices, not claims of eligibility, award entry acceptance or probability of winning. [C01 §§8–9; C02]

## 3. Users, organizations, and access boundaries

### 3.1 Personas

| Persona | Primary job | Default surface | Data scope |
|---|---|---|---|
| Community health worker | Report concerns and act on assigned human cases | Android | Active assignments/collaborations plus appropriate sector and sensitivity grants |
| Animal-health/veterinary field worker | Report individual or grouped animal events; carry out assigned support | Android | Active assignments/collaborations within animal-health capability |
| Health officer | Review regional work, assign responsibility, follow up escalations, review signals | Web; authorized mobile functions where implemented | Authorized country/organization/regions, permitted sector and sensitivity |
| Veterinary officer | Coordinate animal-health work and veterinary review | Web | Authorized regional animal-health scope |
| Authorized One Health reviewer | Investigate cross-sector evidence without unrestricted case browsing | Web | Explicitly approved, initially minimized signal evidence |
| Public visitor / guest reporter | Read approved information and submit a private concern without an account | Public web and mobile guest mode | Public content plus submission-specific receipt; no staff case access |
| Signed-in public member | Subscribe to events, manage own preferences and explicitly linked receipt tracking | Mobile/web | Own subscription/account records; no organization privilege without approved membership |
| Platform operator | Provision access and monitor technical failures | Restricted operations web | Operational metadata; no automatic case-content entitlement |
| Background service | Perform a named job on approved inputs | Internal service | Specific job/input scope; never a staff role |

These are personas and capabilities, not assertions of professional licensure. A One Health reviewer may be an officer with additional grants rather than another unrestricted top-level role.

### 3.2 Permission model

**SA-ACL-01 — P0.** Protected organization/case access MUST be the intersection of identity, membership/jurisdiction, sector, resource scope, sensitivity clearance and action capability. Anonymous routes grant only bounded intake/public access; a role label or successful social login alone is insufficient.

**SA-ACL-02 — P0.** Workers MUST see only actively assigned or explicitly shared cases. An authorized creator receives initial ownership atomically with acceptance of the first report.

**SA-ACL-03 — P0.** Officers MUST be constrained to authorized regions, countries, sectors, and sensitivity levels. A regional officer is not entitled to all HIV or otherwise restricted records.

**SA-ACL-04 — P0.** Public users MAY submit their own private human/animal reports without an account via the intake path. They MUST NOT read other reports, managed-case records, staff recordings/transcripts/discussions or raw surveillance evidence. A scoped receipt permits only the minimal acknowledgement/status defined in §6.5.

**SA-ACL-05 — P0.** The same resource policy MUST apply to API responses, synchronization, attachments, AI tools, search, exports, and counts. Hiding a button is not authorization.

**SA-ACL-06 — P0.** Staff roles MUST be provisioned through controlled administration. Selecting “Health Officer” in a UI MUST NOT grant privileges. The authentication screen MUST not replicate any illustrative self-assigned-role interaction.

**SA-ACL-07 — P0.** Cross-sector signals MUST start with minimized evidence. Access to a linked underlying case requires an independent appropriate grant.

**SA-ACL-08 — P0.** Regional or national switches MUST offer only authorized workspaces. Zimbabwe and Eswatini MUST NOT automatically share identifiable records.

**SA-ACL-09 — P0.** Revoked server access MUST take effect on subsequent server requests. Offline access limitations and cache behavior are governed by §14; immediate disconnected-device erasure MUST NOT be promised.

### 3.3 Action matrix

Login establishes identity, not professional or organizational authority. This matrix supersedes the earlier blanket public-intake prohibition; existing protected staff actions remain constrained by PC-01.

| Action | No account | Signed-in member, no organization | Authorized organization worker/officer |
|---|---|---|---|
| Browse approved education and public sessions | Yes | Yes | Yes |
| Report human/animal concern from any country | Yes, private guest intake | Yes, public-member intake | Yes; use staff case path only within granted workspace |
| Track own receipt | Minimal status with receipt proof | Same, optionally linked with proof | Assigned/regional case access separately authorized |
| Read private managed cases, notes or original recordings | No | No | Assigned/region/sector/sensitivity/action scope only |
| Subscribe to events and reminders | Login required | Own subscriptions | Own subscriptions; publishing separately authorized |
| Create/join/manage organizations | Login required | Request/create pending verification | Approved organization capabilities only |
| Receive/perform assignments and follow-ups | Login and membership required | No | Valid assignment and action scope |
| Raise/acknowledge official escalations | Login and membership required | No | Valid case and action scope |
| Reassign, resolve/reopen, publish, record official classifications | No | No | Explicit appropriate officer/professional capability |
| Read another tenant's AI memory or cached answers | No | No | Never by default; no generic cross-tenant search |

Staff, operator and One Health-reviewer roles retain the sector/sensitivity restrictions described in §3.2. A public report may express a concern, but is not an official escalation. No authenticated member can self-assign privileged roles.

### 3.4 Federated identity and organizational SSO

| ID | Priority | Requirement |
|---|---|---|
| SA-IDN-01 | P0 | Provide Facebook Login, Sign in with Google, and organization SSO through configured OIDC providers. A provider must be visibly unavailable until real registration and callback tests pass. |
| SA-IDN-02 | P0 | Map a verified external identity to an internal SwiftAid account using provider/issuer and stable subject identifiers, not an email address as the identity key. |
| SA-IDN-03 | P0 | Login creates or retrieves an ordinary member account only. Organization membership, catchment, sector, sensitivity, assignment and escalation permissions require separate grants. |
| SA-IDN-04 | P0 | Link/unlink external accounts only after reauthentication and proof of control; matching email addresses never silently merge accounts or promote privileges. |
| SA-IDN-05 | P0 | Use standard authorization-code flows and appropriate PKCE/state/nonce validation. Keep provider secrets on the gateway; never collect provider passwords or authenticate inside the Blazor WebView. |
| SA-IDN-06 | P0 | Use server-side web cookies with anti-forgery protection and secure native token storage. Issue SwiftAid-scoped sessions/tokens; Facebook/Google tokens are not SwiftAid API authority. |
| SA-IDN-07 | P0 | SSO connections are organization-admin configured with allowlisted issuer, client, redirect and claim mapping. User input cannot trigger arbitrary issuer discovery or choose a trusted tenant. |
| SA-IDN-08 | P0 | Support OIDC organizational SSO as the first concrete federation contract. Direct SAML is not silently promised; SAML-only organizations need a verified OIDC broker or a separately approved adapter. |
| SA-IDN-09 | P0 | Validate issuer, audience, signature, lifetime, flow binding and tenant association using maintained libraries. Email-domain hints alone do not grant organization membership or case access. |
| SA-IDN-10 | P0 | SSO-only organizations may prohibit social login for their workspace. Social login may still access that account's personal public subscriptions without bypassing workspace policy. |
| SA-IDN-11 | P0 | Revocation, organization suspension and role removal must be enforced on subsequent privileged requests, memory lookups and cache hits; token refresh cannot resurrect revoked membership. |
| SA-IDN-12 | P0 | Distinguish provider sign-out, SwiftAid sign-out and offline-grant expiry. Preserve guest drafts separately during account changes and deny access to another account's cached workspace. |
| SA-IDN-13 | P0 | Request minimal identity scopes. Login permissions are not Facebook posting, WhatsApp messaging, contact import or marketing consent. |
| SA-IDN-14 | P0 | Verify Facebook, Google and at least one OIDC SSO flow separately, including denied consent, missing email, replay, provider outage and native callback handling. Passing mocks is not real federation evidence. |

**Product behavior:** show “Continue without an account,” “Continue with Facebook,” “Continue with Google,” and “Organization SSO.” Ask for login only when an action requires it. Do not require access to Facebook contacts, a Google calendar, or social posting to submit a report or sign in. SSO must be implemented and tested against a configured provider, not merely represented by a button. OIDC is the recommended initial SSO protocol; SAML-only support is a broker/integration decision, not an already delivered capability.

## 4. Product information model and terminology

### 4.1 Core concepts

| Concept | Product meaning |
|---|---|
| Concern | An observed or reported health issue, even when its cause is unknown |
| Local draft | Editable, device-local information not accepted by the server |
| Case | A durable human or animal support record with ownership and history |
| Human report | Observations about one pseudonymous person; not a household count automatically expanded into individuals |
| Animal report | Observations about an individual animal, herd, flock, or another explicit group |
| Report revision | An immutable version of submitted report facts |
| Case event | A versioned action or decision in the authoritative case history |
| Current projection | A rebuildable view of accepted events for reading, sync, and dashboards |
| Assignment | Accountability and permitted access for a worker; not a diagnosis |
| Assistance request | A need with an owner, progress, delivery, and receipt evidence |
| Follow-up task | Owned future work with due time, outcome, and history |
| Escalation | A documented request for attention/action that requires acknowledgement and disposition |
| Transcript | A fallible text derivative of an original recording |
| AI proposal | Version-bound suggested fields, questions, summary, or explanation awaiting appropriate review |
| Surveillance signal | A possible pattern for review, not a confirmed outbreak |
| Knowledge version | A specific source edition/content hash with applicability, rights and review metadata |
| Community session | An officer-led education activity with a schedule and public-safe invitation |
| Public projection | Deliberately approved publishable fields, not a filtered copy of the private case model |

### 4.2 Required distinctions

**SA-DAT-01 — P0.** The UI and data contracts MUST distinguish unknown, not recorded, not applicable, explicitly absent, and numeric zero. They MUST NOT substitute false or zero for missing evidence.

**SA-DAT-02 — P0.** Case event sequence, clinical report revision, Datasync transport version, transcript revision, signal version, and knowledge version MUST remain distinct concepts.

**SA-DAT-03 — P0.** The original human narrative, recorded observations, AI interpretation, human-confirmed facts, and official classification MUST retain separate provenance.

**SA-DAT-04 — P0.** Onset/event time, capture time, server-recorded time, and synchronization time MUST remain separate. An upload batch MUST NOT become a new onset cluster merely because reports arrived together.

**SA-DAT-05 — P0.** A location MUST record whether it was device-derived, user-confirmed, approximate, or unknown, with precision where available. Lack of GPS MUST NOT prevent submission.

**SA-DAT-06 — P0.** Human case counts, animal report counts, animals at risk, animals affected, and animals dead MUST be separately labelled. Their denominators MUST NOT be interchanged.

**SA-DAT-07 — P0.** Disease suspicion, laboratory evidence, administrative status, outcome, and surveillance signal status MUST NOT be inferred from one another.

**SA-DAT-08 — P0.** Source references, authoritative records and derived results MUST remain linked by stable identifiers rather than copied into unversioned prose that cannot be audited.

### 4.3 Human and animal report content

Shared context includes case ID, workspace, sector, reporting worker, observations, onset/event precision, location, source narrative/recording references, evidence, owner, sensitivity, current workflow, and processing/consent context where applicable.

Human-specific content includes a pseudonymous subject reference; age or age band when known; reported observations; relevant reported exposure; and protected contact references when needed. HIV status is never a generic intake requirement.

Animal-specific content includes subject scope (`Individual`, `Herd`, `Flock`, `OtherGroup`); species or unknown; pseudonymous holding/owner reference; separately nullable at-risk/affected/dead counts; observation period; and reported contact, exposure, or movement observations. Count validation applies only to compatible known populations and time windows.

**Basis:** [I §§6.1–6.3, 11.3, 12, 15].

## 5. Case lifecycle and state semantics

### 5.1 The accountable journey

The operational journey is: local capture → report acceptance and initial ownership → review/prioritization → assignment or reassignment → assistance and monitoring → follow-up/escalation where needed → authorized resolution → reopening when new work is justified.

These activities are not a rigid medical pathway. A case can have multiple open assistance requests and follow-ups. Escalation may coexist with any active operational state. Surveillance examines evidence without moving the case through a clinical state automatically.

### 5.2 Independent state dimensions

| Dimension | State set from the implementation plan | Product rule |
|---|---|---|
| Local draft | `Editing`, `ReadyToSubmit`, `Queued`, `NeedsAttention` | None means server acceptance |
| Case workflow | `Reported`, `UnderReview`, `Active`, `AwaitingFollowUp`, `Resolved` | Operational progress only |
| Clinical classification | `Unclassified`, `Suspected`, `Probable`, `Confirmed`, `RuledOut` | Explicit authorized evidence-based decision only |
| Laboratory | `NotRecorded`, `Requested`, `Collected`, `Pending`, `ResultRecorded` | A label is not a laboratory result |
| Assistance | `Requested`, `Accepted`, `InProgress`, `Provided`, `ReceiptConfirmed`, `Unfulfilled`, `Cancelled` | Provision and confirmed receipt are different |
| Follow-up | `Scheduled`, `Acknowledged`, `Completed`, `Missed`, `Rescheduled`, `Cancelled` | Completion requires an outcome/observation |
| Escalation | `Raised`, `Acknowledged`, `ActionRecorded`, `Closed` | Actual escalation is distinct from an AI suggestion |
| Signal | `Candidate`, `UnderReview`, `MoreEvidenceRequested`, `Dismissed`, `Escalated` | No state means automatic outbreak confirmation |
| Public content | `Draft`, `InReview`, `Published`, `Withdrawn` | Publication is a protected operation |
| Session | `Draft`, `Reviewed`, `Scheduled`, `Completed`, `Cancelled` | Session and invitation delivery states are separate |

Outcome is recorded separately. The source plan does not fix a universal human/animal outcome vocabulary; the implementation must use a versioned, reviewed catalogue rather than invent clinical outcome policy. An explicit unknown outcome remains valid.

### 5.3 Transition requirements

**SA-LIF-01 — P0.** First acceptance MUST atomically record `CaseReported` and initial `CaseAssigned`. Under the plan's two-event convention, aggregate version becomes 2 and report revision becomes 1; one user submission must not appear as two cases.

**SA-LIF-02 — P0.** Workflow transitions MUST be actor-authorized and auditable. Review, assignment, escalation and closure MUST NOT automatically set a clinical classification.

**SA-LIF-03 — P0.** Corrections MUST create a new report revision with the original preserved, the actor, a reason, and a relationship to the changed facts.

**SA-LIF-04 — P0.** A stale submission MUST retain its local proposal and request reconciliation rather than overwrite a newer accepted version.

**SA-LIF-05 — P0.** Resolution MUST record an authorized decision, outcome if known, and explicit disposition of unfinished work. Closure does not imply recovery, absence of disease, laboratory confirmation, or fulfilment of all assistance.

**SA-LIF-06 — P0.** Reopening MUST preserve prior closure, record a reason, restore accountable ownership, and expose the new next action.

**SA-LIF-07 — P0.** For a resolved case, the default interaction is a history view. New operational work requires reopening; a permitted factual correction may be appended without claiming the case has been reopened. This is **PC-07** and must be reflected in transition tests.

**Acceptance:** register, assign, assist, follow up, escalate, resolve and reopen a human case and an animal case; replaying the history must reproduce the accepted operational state. [I P1.1, P2.1, P5.3].

**Basis:** [I §§6.2–6.5; PC-07].

## 6. Case reporting and registration

### 6.1 Entry points and low-input flow

Any visitor starts from **Report concern** without a login wall. A verified worker can alternatively choose a permitted organization workspace or follow-up action. The first selection is **Human** or **Animal**. Input may be a short voice recording, typed narration, or guided observation chips. The user does not need to know a disease name.

**PC-02 updated intake contract:** a client-generated identifier; human/animal subject scope; at least one observation or narrative/original-audio reference; capture date/time and geographic fields from §6.6; processing confirmation; and reporter confirmation. A guest supplies no organization authority. For staff reporting, the trusted actor supplies a permitted workspace; occurrence geography remains separately reported. Onset, age, detailed species/counts and exact coordinates may be unknown. This is a product intake contract, not a national statutory form.

The mobile experience has three logical stages: **capture → review known/unknown information → save or submit**. It may use several compact subviews, but must not force a long disease-specific questionnaire for every concern.

### 6.2 Functional requirements

| ID | Priority | Requirement |
|---|---|---|
| SA-REP-01 | P0 | Create/edit human and animal guest drafts offline on a fresh Android installation. Staff offline work additionally requires authorized workspace bootstrap. Persist meaningful changes and recover after restart. |
| SA-REP-02 | P0 | Allow unclassified reporting and symptom/event-first capture. Never require a diagnosis to proceed. |
| SA-REP-03 | P0 | Reuse trusted worker, organization, country, permitted region, and case context; show editable suggestions where confirmation is required. |
| SA-REP-04 | P0 | Offer voice, typed narration, and guided observation input. Microphone denial or transcription failure must not block the text route. |
| SA-REP-05 | P0 | Preserve user statements and explicit uncertainty. AI-extracted facts remain proposals until confirmed. |
| SA-REP-06 | P0 | Allow unknown onset, age, location and counts. Capture location source/precision; manual locality selection must work without GPS. |
| SA-REP-07 | P0 | Distinguish animal individual/group reporting and keep species, report count, affected count, mortality count and at-risk count separate. |
| SA-REP-08 | P0 | A narrative describing multiple people must not silently produce multiple patient cases. Ask how the worker wishes to register the individuals and retain the original observation. |
| SA-REP-09 | P0 | Resolve relative dates only as a reviewable interpretation of capture time and declared local context. Never use later upload time to reinterpret “yesterday.” |
| SA-REP-10 | P0 | Show a compact confirmation with critical facts, proposed values, and unknowns. Do not pre-confirm consent, official diagnosis, sensitive disclosure, or publication. |
| SA-REP-11 | P0 | Keep an immutable queued submission payload for retry. Display “Saved on this device” or “Queued for submission” until the server accepts it. |
| SA-REP-12 | P0 | Atomically establish initial ownership when a permitted reporting worker's case is accepted, then return its authoritative receipt/version. |
| SA-REP-13 | P0 | Accept a report with a pending original-audio attachment reference without pretending the audio is already uploaded or transcribed. |
| SA-REP-14 | P1 | Provide additional photo/document evidence only after attachment-type validation, authorization, and privacy requirements are implemented. Image capture must not imply image-based diagnosis. |

### 6.3 Confirmation rules

The review view emphasizes subject type, species/group where applicable, onset precision, affected/dead counts, location precision, negation, and sensitive processing context. For each extracted field, the user can inspect the supporting text or audio/transcript reference, correct it, accept it, or leave it unknown. The AI may reproduce a worker-stated condition as an attributed statement, but must not propose a diagnosis or confirmation.

For an offline recording without cloud inference, save the minimal worker-confirmed report and mark extraction as unavailable/pending. A later extraction produces a proposal; it does not rewrite the accepted report. A worker may subsequently confirm a correction through the normal revision workflow.

### 6.4 Failure and recovery

A rejected submission retains the local content with a safe explanation and repair action. A timeout produces a retry of the same submission identity, not a new case. A scope denial requires reassessment of authority, not automatic submission under another user's account. A duplicate suspicion may be flagged for review, but no case is silently merged on fuzzy name, location, or narrative similarity.

**Acceptance:** a worker can save a human or animal concern in airplane mode, force-stop, reopen, reconnect, and obtain exactly one accepted case while retaining original evidence and unknown fields. [I P2.2, P3.1, P3.2, P8.2, P11.1; PC-02].

### 6.5 Account-free reporting, private intake and coverage

UI wording may say “Report a case.” Internally distinguish **intake received** from an **organization-accepted managed case**. The first preserves what a guest reported; the second requires an authorized service organization and accountable worker. This prevents unauthenticated submissions becoming trusted diagnoses or acquiring staff rights.

Intake lifecycle: `LocalDraft → Queued → ReceivedUnverified → NeedsRouting / AwaitingReview → AcceptedIntoCase / LinkedAsEvidence / Declined`. `NoServiceCoverage` is a routing outcome, not a fabricated resolved health outcome. Review/routing changes append events; do not delete a report merely because it is incomplete. A duplicate link is human-authorized and never reveals another person's dossier to the reporter.

| ID | Priority | Requirement |
|---|---|---|
| SA-PUB-01 | P0 | Allow any visitor to report a human or animal concern without registration, login, organization membership, email, phone number or social-provider consent. Account-free submission is a core product path. |
| SA-PUB-02 | P0 | Accept guest reports through a dedicated intake API. Anonymous intake cannot invoke the privileged managed-case command, assignment, escalation, resolution or publication APIs. |
| SA-PUB-03 | P0 | Persist a guest submission privately as an immutable Unverified intake report with a receipt and explicit routing/review state. Report acceptance is not clinical verification, staff assignment or a guarantee of response. |
| SA-PUB-04 | P0 | Route only to explicitly configured, eligible organization catchments. Ambiguous or uncovered places retain NeedsRouting or NoServiceCoverage; never expose the report to all nearby organizations. |
| SA-PUB-05 | P0 | Every received intake has a monitored intake-queue owner or an explicit uncovered state. Only authorized acceptance into managed casework creates a staff owner; the guest is never impersonated as that worker. |
| SA-PUB-06 | P0 | Promotion creates a managed case atomically with initial staff assignment and a link to the unchanged guest evidence. Keep the distinction between reported facts, review acceptance and official classification. |
| SA-PUB-07 | P0 | Issue high-entropy, submission-specific receipt proof for limited tracking without an account. A receipt identifier alone reveals nothing, and receipt proof never grants access to staff notes, other reporters, clinical records or assignments. |
| SA-PUB-08 | P0 | Make retries idempotent per intake capability and submission ID. Lost responses or repeated uploads must not multiply reports, managed cases or analysis jobs. |
| SA-PUB-09 | P0 | Use quotas, payload/media bounds, abuse controls and safe validation on anonymous endpoints. Provide accessible recovery for challenges; a temporary abuse limit is not a demand to create an account. |
| SA-PUB-10 | P0 | Do not expose unlimited guest AI calls. Apply abuse and budget checks before processing; invalid or quarantined content must not poison approved memory or trigger repeated costly jobs. |
| SA-PUB-11 | P0 | Guest attachments use submission-bound upload capabilities with size/type/hash checks. All originals remain private; upload authority cannot be used to download another report or alter accepted evidence. |
| SA-PUB-12 | P0 | Display privacy/processing information, require appropriate recording/processing confirmation, and minimize identifiers about third parties. Optional contact information must remain optional and protected. |
| SA-PUB-13 | P0 | A fresh Android installation can capture guest drafts and original audio offline without prior sign-in. It cannot read organization data or claim server acceptance while disconnected. |
| SA-PUB-14 | P0 | Keep guest outbox, receipts and original recordings isolated from staff projections. Sign-in or workspace reset must not silently delete, attach, or submit guest work under another identity. |
| SA-PUB-15 | P0 | Link a guest receipt to a signed-in account only with explicit confirmation and possession of receipt proof. Reporter tracking does not confer membership or access to the managed case dossier. |
| SA-PUB-16 | P0 | Offer no public case directory, private-case map, searchable receipt endpoint or automatic publication. The global ability to submit does not make the submitted data public. |

**Receipt view:** show receipt ID, received/pending status, the reporter's own minimal submission metadata, and a generic routing result such as “Received for review” or “No participating service is configured for this area.” Do not show staff names, candidate diagnoses, comments, exact private routes, or promised attendance. Optional supplementary guest evidence appends to the intake record, not to authoritative clinical facts. Loss of receipt proof does not justify lookup by someone else's name/phone.

### 6.6 Global date/time and geographic capture

| ID | Priority | Requirement |
|---|---|---|
| SA-GEO-01 | P0 | Accept occurrence countries globally using a versioned ISO 3166-1 alpha-2 catalogue, not a ZW/SZ-only validator. Zimbabwe and Eswatini remain initial service workspaces, not the boundary of public intake. |
| SA-GEO-02 | P0 | Every intake schema contains capture date/time, server receipt date/time, reported occurrence/onset with precision, location, area, region and country. Unknown values must be explicit, never invented. |
| SA-GEO-03 | P0 | Ask the reporter to confirm occurrence country on submission. Permit region/area/locality to be explicitly unknown or not applicable where genuine; a complete hierarchy or precise GPS cannot be mandatory worldwide. |
| SA-GEO-04 | P0 | Store coordinates as optional WGS84 latitude/longitude with accuracy and provenance. Manual locality/area entry must work without location permission; reject malformed coordinates and flag contradictions without replacing evidence. |
| SA-GEO-05 | P0 | Store known instants in UTC with original offset/time-zone and precision when available. Date-only/approximate/unknown event times remain such; upload time and reporter device location cannot substitute for occurrence facts. |
| SA-GEO-06 | P0 | Administrative names use country-aware labels and optional normalized identifiers; retain reported text and lookup version. Do not assume every country uses provinces/districts/villages or that duplicate place names are identical. |
| SA-GEO-07 | P0 | Separate occurrence geography, reporter location, service coverage, organization jurisdiction and data-processing region. No geographic match alone authorizes cross-organization or cross-border disclosure. |
| SA-GEO-08 | Gate | Global intake is a technical product scope, not a claim of worldwide emergency response, legal approval or available staff. Publish coverage and configured processing notices; real rollout requires jurisdiction-specific review without disguising unsupported areas as covered. |

| Field group | Required stored representation | Input behavior |
|---|---|---|
| Capture and receipt | `CapturedAtUtc?`, original offset/zone, capture precision; server `ReceivedAtUtc` | Default from device with visible confirmation; preserve unreliable/unknown clock indication |
| Occurrence/onset | `OccurredAtUtc?` or local date/range, offset/zone and precision | Reporter supplied; may be unknown; never default to upload time |
| Location | Coordinates nullable, accuracy, locality/description and source | GPS optional; manual entry allowed; a map pin is not proof |
| Area | Reported name, optional normalized ID and `Known/Unknown/NotApplicable` | Country-aware label; preserve original text |
| Region | Same representation, parent country relationship when normalized | Do not enforce Zimbabwe/Eswatini administrative names globally |
| Country | Reporter-confirmed occurrence country code and catalogue version | Global country selector; not derived from provider profile/IP as fact |
| Routing | `CoverageState`, candidate authorized catchment, decision/provenance | Server-derived after validation; never trusted from client payload |

All fields exist in the schema, but unsupported precision is not manufactured. Optional geocoding is a suggestion with source/version and confirmation; use a verified/licensed provider only after privacy review. A missing geocoder must not prevent intake.

## 7. Case tracking, history, and corrections

### 7.1 Case workspace

The workspace must answer: **What was reported? Who owns it? What has happened? What is still needed? What is due next? What needs review? How fresh is this view?**

The default summary shows pseudonymous case identifier, human/animal context, reported condition or unclassified label, workflow, accountable owner, latest observation, open assistance, next follow-up, escalation indicator, version, and synchronization status. It does not place a disease label in a public or lock-screen surface.

| ID | Priority | Requirement |
|---|---|---|
| SA-HIS-01 | P0 | Display a chronological, filterable case timeline with author, action, recorded time, event/observation time when known, reason where applicable, and version references. |
| SA-HIS-02 | P0 | Make submitted facts read-only in place. “Correct report” creates a new revision and explains what is being corrected. |
| SA-HIS-03 | P0 | Preserve prior report revisions and allow authorized users to compare changed fields with provenance. |
| SA-HIS-04 | P0 | Distinguish authoritative events, pending local proposals, AI analysis, transcript derivatives, and technical media activity visually and semantically. |
| SA-HIS-05 | P0 | Store AI analysis separately. Merely generating a summary must not create a clinical correction or increment the case stream in a self-triggering loop. |
| SA-HIS-06 | P0 | Record comments and comment corrections append-only; never silently replace another person's statement. |
| SA-HIS-07 | P0 | Retain original recorded and capture times even when updates arrive late. Sorting choices must not disguise late-arriving evidence as newly observed illness. |
| SA-HIS-08 | P0 | When privacy/retention procedures remove protected content, show an authorized audit indication instead of retaining identifiable payloads forever or implying data never existed. |

**PC-03 — Timeline presentation.** A single user command that appends several internal events may be displayed as one expandable activity group, but individual events and versions remain inspectable by authorized reviewers. Local pending activity must be labelled separately from server history.

**Acceptance:** a correction changes the current projection, leaves the original revision accessible within scope, updates the timeline, and makes earlier AI analysis stale where its input changed. [I P1.1, P2.1, P5.3, P8.3].

**Basis:** [I §§6, 14, 16; PC-03].

## 8. Assignment and coordination

### 8.1 Ownership model

Every accepted active case has one accountable owner. Collaborators are additional scoped participants; they do not erase or ambiguously share primary accountability. Officers assign within current authorization and appropriate sector. A cross-sector investigation does not automatically grant both teams full case access.

| ID | Priority | Requirement |
|---|---|---|
| SA-ASG-01 | P0 | Establish one initial owner on case acceptance and show that owner in worker/officer workspaces. |
| SA-ASG-02 | P0 | Permit an authorized officer to reassign with a reason and an eligible assignee; validate authority at acceptance time. |
| SA-ASG-03 | P0 | Preserve old/new assignment history, transfer time and acknowledgement. Show a pending acknowledgement explicitly. |
| SA-ASG-04 | P0 | Allow explicit collaborator grants with bounded case/sector/action scope; do not make all workers in a region collaborators. |
| SA-ASG-05 | P0 | Reassignment must update authorized read/sync/media scope. A newly assigned older case must appear even if its earlier update predates the client's sync checkpoint. |
| SA-ASG-06 | P0 | Revoked assignment must remove future server access and trigger safe client reconciliation on reconnection. |
| SA-ASG-07 | P0 | Transfer of case ownership must not silently mark assistance or follow-ups completed. Existing action owners remain visible until explicitly changed. |
| SA-ASG-08 | P1 | Suggest eligible assignees based on permitted workload/context, but require officer confirmation and retain the reason. No unreviewed autonomous assignment. |

**PC-01 clarification:** reassignment requests may be recorded by workers, but authoritative reassignment and collaborator grants are officer-controlled in the demonstration. Cross-institution transfers need separately established sharing arrangements and are not implied by a region selector.

**Acceptance:** reassignment removes a former worker's online access, adds the case for the new assignee, preserves transfer history, and leaves existing open actions accurately owned and unresolved. [I P1.2, P3.3, P5.1].

## 9. Assistance, referrals, and support fulfilment

### 9.1 Supported operational record

An assistance request captures the need, requesting actor, responsible owner, requested time, optional selected due time, current state, recorded activity, delivery evidence, receipt confirmation, and any unresolved reason. Referral is a supported assistance context, not an automatic guarantee of care.

A directory result must have a verified or explicitly synthetic origin. In the demonstration, a fictitious facility is labelled as such. No real contact, service availability, telephone number, medicine recommendation, or transport guarantee may be invented.

| ID | Priority | Requirement |
|---|---|---|
| SA-AST-01 | P0 | Record an assistance need independently from the disease classification and assign a responsible person. |
| SA-AST-02 | P0 | Support `Requested`, `Accepted`, `InProgress`, `Provided`, `ReceiptConfirmed`, `Unfulfilled`, and `Cancelled` without conflating them. |
| SA-AST-03 | P0 | Record who accepted responsibility, who provided assistance, and the provenance of receipt confirmation; these may be different people. |
| SA-AST-04 | P0 | Keep an unmet need visible when support is delayed, declined, unavailable, or unconfirmed. |
| SA-AST-05 | P0 | Make cancellation/unfulfilled outcomes explicit and reasoned. No silent removal of the request from the case record. |
| SA-AST-06 | P0 | Distinguish a referral requested, a referral arranged, and evidence that the referred service was received. A shared link or message is not receipt of care. |
| SA-AST-07 | P0 | Show only verified applicable directory entries or a clear “Referral contact not configured” state. |
| SA-AST-08 | P0 | Ensure summaries and dashboards count provided-but-unconfirmed assistance as a remaining receipt question, not verified fulfilment. |
| SA-AST-09 | P2 | Add inventory, supplier, financial support or partner fulfilment integrations only with a separate product and authorization decision. |

**PC-06 — Receipt evidence.** The prototype may record a worker's documented confirmation from the recipient; it need not create a patient/farmer account. It must label who reported receipt and how it was established, rather than imply a digital recipient signature.

**Acceptance:** the same request can be accepted, provided and later receipt-confirmed; the intermediate gap remains visible to the owner, officer and AI continuity summary. [I P5.1, P8.3].

## 10. Monitoring and operational prioritization

### 10.1 Monitoring view

Monitoring combines reported observations, outstanding assistance, due tasks, unacknowledged escalations and unresolved review requests. It is not continuous physiological monitoring. A last-observed time and a last-synchronized time must not be conflated.

| ID | Priority | Requirement |
|---|---|---|
| SA-MON-01 | P0 | Allow authorized staff to append an observation with source, occurrence/capture precision and optional original voice. |
| SA-MON-02 | P0 | Display the latest accepted observation and expose earlier observations through history. |
| SA-MON-03 | P0 | Derive overdue and pending action states deterministically from recorded schedules and outcomes. AI is not the authority for completion. |
| SA-MON-04 | P0 | Provide separate views of overdue follow-ups, unreceived assistance, unacknowledged escalations, and cases without a recorded next action. |
| SA-MON-05 | P0 | Distinguish administrative review priority from clinical severity. Unknown priority/severity must not be presented as low risk. |
| SA-MON-06 | P0 | Keep manual action and escalation available when AI, the queue, or a knowledge source is unavailable. |
| SA-MON-07 | P0 | Show stale evidence and last refresh time; do not make a cached summary appear current after newer known facts exist. |
| SA-MON-08 | Gate | Clinical escalation engines and symptom-derived prioritization are disabled in the competition configuration. Staff may raise and record their own escalations; the agent may flag a missed, already-recorded administrative deadline, not clinical urgency. |

**Acceptance:** disconnect the AI service and verify that the owner can still inspect outstanding work, record an observation, schedule a follow-up and raise an escalation. [I P5.2, P6.3, P8.3].

## 11. Follow-up management

A follow-up belongs to a case, has a purpose and accountable assignee, and records a due instant, acknowledgement, outcome and rescheduling history. A future date is an operational commitment, not an AI-generated clinical recommendation.

| ID | Priority | Requirement |
|---|---|---|
| SA-FUP-01 | P0 | Schedule follow-up work with case, purpose, eligible assignee, due time, and known case context reused. |
| SA-FUP-02 | P0 | Support acknowledgement, completion, missed/unreachable outcome, rescheduling and cancellation without deleting the earlier schedule. |
| SA-FUP-03 | P0 | Completing a follow-up requires an observation or documented outcome, not only checking a box. |
| SA-FUP-04 | P0 | Failure to contact a person or reach an animal holding must not imply care completed, recovery, or absence of need. |
| SA-FUP-05 | P0 | Rescheduling records the old due time, new due time, actor and reason. |
| SA-FUP-06 | P0 | Show due/overdue work in the assigned worker's queue and the officer's authorized regional view. |
| SA-FUP-07 | P0 | Avoid re-entry of unchanged subject/species/demographic facts during routine follow-up. |
| SA-FUP-08 | P0 | Offer at most three factual/administrative clarification questions, such as a contradictory reported date or who received a delivery. Do not select clinical examinations, exposure investigations, treatment or medical monitoring questions. |
| SA-FUP-09 | P0 | Cancel or suppress no-longer-relevant reminders after a completed, rescheduled or cancelled task. |
| SA-FUP-10 | Gate | The agent MUST NOT generate disease-specific follow-up schedules. Staff choose due times; existing human-set task dates may be reused for administrative reminders. Clinical scheduling automation is outside the entry. |

An unreachable contact may be recorded as an outcome of an attempt while the task remains missed or is explicitly rescheduled. It is not completion of the intended clinical/veterinary follow-up. This preserves the source plan's separation between activity and fulfilment.

**Acceptance:** schedule, acknowledge, miss, reschedule and complete a task; verify preserved due-date history, no duplicate reminders, and an outcome linked to the correct case version/context. [I P5.2, P6.3, P11.1].

## 12. Escalation, resolution, and reopening

### 12.1 Escalation

| ID | Priority | Requirement |
|---|---|---|
| SA-ESC-01 | P0 | Permit a worker to raise a concern at any active stage with reason, evidence references and an authorized recipient/route. |
| SA-ESC-02 | P0 | Distinguish an AI recommendation to escalate from an actual staff-issued escalation. |
| SA-ESC-03 | P0 | Track `Raised`, `Acknowledged`, `ActionRecorded`, and `Closed`; show unacknowledged work prominently. |
| SA-ESC-04 | P0 | Record the recipient, acknowledger, action, resolution and timestamps. An unread notification cannot satisfy acknowledgement. |
| SA-ESC-05 | P0 | Keep the case owner accountable while escalation is pending; escalation does not automatically transfer ownership. |
| SA-ESC-06 | P0 | Make offline escalation status honest: it is locally queued, not received by the officer. Show configured real-world escalation guidance where available; do not wait for AI. |
| SA-ESC-07 | Gate | Real emergency routes, time thresholds and recipient contacts must be configured from verified local procedures. Never invent a hotline or response-time guarantee. |

### 12.2 Resolution and reopening

**SA-RES-01 — P0.** Only an actor with resolution capability may close a case. The record must include a reason, known outcome, open-action disposition, and the relevant evidence/version.

**SA-RES-02 — P0.** Closure must not overwrite clinical classification, lab evidence or original observations. A closed case may retain an unknown clinical outcome.

**SA-RES-03 — P0.** Reopening must create a new event and a visible next action, not delete the previous resolution.

**SA-RES-04 — P0.** Outstanding escalation, assistance and follow-up work must be explicitly completed, cancelled with reason, transferred, or retained under an authorized disposition before closure is represented as settled.

**SA-RES-05 — P0.** A worker lacking closure permission may record a resolution recommendation/outcome for officer review, but the interface must not imply that recommendation has already closed the case. This is part of PC-01.

**Acceptance:** raise an escalation without AI, acknowledge it with a second authorized identity, document action, close it, then resolve and later reopen the case without losing any prior state. [I P5.2, P5.3].

## 13. Communication, original voice, and transcripts

### 13.1 Voice is primary evidence

Every original voice note must remain playable from the relevant case discussion. Transcription supplements the recording; it is not a substitute. The application must not replace a worker's original voice with synthesized speech.

| ID | Priority | Requirement |
|---|---|---|
| SA-COM-01 | P0 | Support text and original-voice case comments with author, capture/recorded time, case association and access scope. |
| SA-COM-02 | P0 | Provide inline play/pause, progress, duration and a visible download/upload state. The original is reachable in one tap from its ready comment. |
| SA-COM-03 | P0 | Keep transcripts collapsed by default so users may listen instead of reading. Distinguish machine and human-corrected text. |
| SA-COM-04 | P0 | Preserve the original recorded byte stream and content hash. Any ASR-compatible conversion is a separate derivative. |
| SA-COM-05 | P0 | Support recording and authorized local playback offline; cloud transcription must be explicitly unavailable or pending offline. |
| SA-COM-06 | P0 | An interrupted recording must be recoverable or explicitly failed; the UI must not claim a complete recording that does not exist. |
| SA-COM-07 | P0 | Permit multiple notes within a case. Apply the plan's proposed demo bounds of 120 seconds and 10 MiB per original note, with clear feedback before transfer. |
| SA-COM-08 | P0 | Upload media separately from case records; show pending media after comment acceptance and resume or transparently retry according to tested capability. |
| SA-COM-09 | P0 | Restrict audio, transcripts, derivatives and cached copies using the case's current authorization. Never expose a permanent public recording URL. |
| SA-COM-10 | P0 | Keep original voice usable when ASR fails, language support is unverified, or the AI budget is exhausted. |
| SA-COM-11 | P0 | Correcting a transcript must append a new revision with actor/reason while preserving the original audio and prior machine output. |
| SA-COM-12 | P1 | Add seeking and playback speed only where the selected device/player path has been verified. Basic playback must not depend on these extras. |
| SA-COM-13 | Gate | Record/transcribe under applicable consent or authorized processing policy. Do not assume consent from a pre-ticked control or from case creation alone. |

### 13.2 Transcript truthfulness

Store the audio reference/hash, provider and model identity where applicable, requested/detected language where actually known, review state, and the available segment references. Display confidence only when a provider genuinely supplies it and its meaning is documented; do not invent numerical certainty.

Numbers, dates, negation, species, code-switching and medically significant words need visible confirmation. A corrected transcript does not by itself change accepted case facts. Those changes require a separate authorized report-correction command.

### 13.3 Attachment and playback states

The plan's detailed attachment lifecycle is retained: `LocalOnly`, `AwaitingCaseAcceptance`, `UploadAuthorized`, `Uploading`, `ServerVerified`, `Ready`, then optional `ProcessingTranscript` and `TranscriptAvailable` or `TranscriptionFailed`.

For users, present simple independent labels: **On this device**, **Waiting to upload**, **Uploading**, **Audio available**, **Transcript processing**, **Transcript unavailable**. A failed transcript must not downgrade ready audio to missing. A comment whose metadata arrived before the media must say audio is not yet available on this device, rather than show an active play button with no content.

**Acceptance:** record a note, restart offline, play it, upload with interruption, play the returned original, compare hashes, fail transcription, and still play the original. Revoke access and verify unauthorized playback is denied. [I P4.1–P4.3].

**Basis:** [I §9].

## 14. Offline behavior, synchronization, and conflict recovery

### 14.1 Offline contract

Guest capture needs no account or online bootstrap. **Staff workspace access** requires initial online sign-in and approved membership to provision identity, offline access grant and permitted data. Show **Ready for offline guest reporting** separately from **Workspace ready offline**. A guest installation cannot acquire staff authority by choosing a role.

The source plan separates two paths: Datasync downloads authorized flat read models, while a durable domain-command outbox sends immutable actions through the ASP.NET Core command API. Attachments have a separate transfer lifecycle. This is not unrestricted database-to-database synchronization.

| Capability | Offline behavior |
|---|---|
| Create/edit local report | Available within the provisioned authorized context |
| Save original voice | Available when microphone/storage permission and capacity permit |
| Read assigned cached cases | Available only within valid offline grant and cached scope |
| Add permitted observation/comment/follow-up work | Queue locally; never imply server acceptance |
| Play original voice | Available only when the authorized original is cached locally |
| Read downloaded education | Available with version, review state and last-update warning |
| Run cloud AI/transcription | Not available offline; defer or continue without it |
| Download a new assignment or uncached audio | Requires reconnection |
| Apply officer publication/assignment decisions from web | Requires authorized server interaction |
| View newly changed access rights or withdrawn guidance | Known only after reconnecting; expiry locks apply locally |
| Deliver an escalation or message | Not guaranteed until the appropriate server/provider receipt exists |

### 14.2 Requirements

| ID | Priority | Requirement |
|---|---|---|
| SA-OFF-01 | P0 | Persist drafts, original media and immutable queued commands in device-local storage that survives restart. |
| SA-OFF-02 | P0 | Distinguish draft, queued, server-accepted, media-pending, synchronized, and needs-review states on the affected item. |
| SA-OFF-03 | P0 | Trigger synchronization on resume, manual request, and debounced reconnection; do not promise continuous Android background execution. |
| SA-OFF-04 | P0 | Refresh identity/scope before normal sync and protected server-origin media playback; network connectivity alone is not proof of server reachability. |
| SA-OFF-05 | P0 | Use stable command identity for retries. A timeout after commit must not produce a duplicate case, event, task, assistance action or reminder. |
| SA-OFF-06 | P0 | Preserve per-case command ordering and dependency handling. A blocked case must not block unrelated cases. |
| SA-OFF-07 | P0 | Show conflicts with retained local proposal and latest authorized server state. Reconciliation creates a new command; never silently apply client-wins to clinical history. |
| SA-OFF-08 | P0 | Apply scope reconciliation for both revoked records and old newly assigned records. A timestamp-only delta is insufficient as an access-change product behavior. |
| SA-OFF-09 | P0 | Lock cached case content when the offline grant expires. The source plan's demo default is 12 hours, configurable and not a legally mandated retention/access duration. |
| SA-OFF-10 | P0 | Preserve protected unsent work for reconciliation when access expires or is revoked. Do not upload denied work, impersonate another user, or silently delete it. |
| SA-OFF-11 | P0 | Provide a safe sync/privacy view containing last confirmation, pending actions/media, failed items, conflicts, access expiry and recovery actions. |
| SA-OFF-12 | P0 | Safe sign-out/account switching must prevent one user reading another user's data and explicitly handle unsent work before destructive clearing. |
| SA-OFF-13 | P0 | Database/cache rebuild must not destroy isolated pending drafts or original local recordings. |
| SA-OFF-14 | P0 | After successful source refresh, stop recommending withdrawn education while retaining relevant historical citation references. |

### 14.3 Conflict interaction

The conflict view shows the retained proposed action, its base version, the newer authorized facts, and whether the user's permission changed. The worker may review, amend into a new authorized proposal, cancel that local proposal with an audit reason where appropriate, or leave it unresolved. There is no “overwrite server” shortcut.

The case timeline must not display a queued command as an accepted event. A server receipt can exist while the device has not yet pulled the updated projection; show **Accepted; refreshing** rather than retrying as a new submission.

### 14.4 Security limit

Disconnected devices cannot receive instantaneous revocation or withdrawal information. Expiry and reconnect purging reduce exposure but are not a guarantee against a compromised device. Native SQLite/app-private storage must not be described as database encryption without a tested encryption implementation. The demonstration uses synthetic data; real deployment is gated in §23.

**Acceptance:** prove restart recovery, timeout-after-commit retry, per-case conflict isolation, old-new-assignment refresh, revoked-case/audio denial, expired grant lock, and preservation of unsent work. [I P0.2, P3.1–P3.3, P4.2, P12.1].

**Basis:** [I §§7.3, 8, 9, 21].

### 14.5 Guest mode and offline boundaries

Guest mode is a separate local namespace containing only that device's drafts, original recordings and receipt capabilities. It uses a dedicated intake submission queue, not authenticated Datasync. The existing staff grant expiry policy applies to staff caches, not to the ability to compose a new guest report.

A first-run offline Android app may create a guest draft and play its local recording. A first-time web visit still needs a network to load the public site; the native Android offline promise does not imply an installed web PWA. Internet access is required to receive a server receipt or upload audio. A public web form can submit online without registration.

At sign-in, keep guest drafts separate. Ask explicitly before linking a receipt to an account; do not reinterpret the guest as a staff author. Sign-out locks staff material while leaving the guest mode available. Clearly distinguish public source material from permission-controlled cached staff records.

## 15. AI assistance and human control

### 15.1 Competition capability allowlist

The application uses Microsoft AI/MCP tooling with verified Meta models. These are logical capabilities, not separately deployed agents. Scope is enforced in code, not merely a system prompt or reviewer disclaimer.

| Capability | Input | Output | Event boundary |
|---|---|---|---|
| `report.extract` | Original authorized narration/transcript and report schema | Only explicitly reported facts, source spans and unknowns | No inferred diagnosis, symptom-severity ranking or new care advice |
| `report.audit` | Report fields, their source and accepted revisions | Date/count contradictions, inconsistent units and possible duplicate evidence for review | No automatic merge/correction; no physiological plausibility diagnosis |
| `report.clarify` | Missing/contradictory administrative/reporting facts | Up to three factual clarification questions | No examination, testing, treatment, exposure-investigation or urgency recommendation |
| `case.summarize` | Permitted history, existing human-set obligations and receipts | Factual current work, owners, unmet needs and stale evidence | No prognosis, clinical risk or invented completion |
| `ops.plan-continuity` | Bounded work queue and existing operational obligations | At most three evidence-backed administrative actions for approval | Dates/recipients/scopes constrained; no clinical priority inferred from case content |
| `session.prepare-invitation` | Officer-selected topic/title, approved material IDs and schedule | Nonmedical logistical invitation only | No AI-generated health teaching content or diagnosis-targeted audience |
| `education.retrieve-materials` | User-selected topic, country, language and audience | Existing card titles, source links and human-authored material | Deterministic retrieval, not a generative education chatbot |
| `signal.investigate` | None by default | Unavailable until narrowly clarified in writing | Optional P1/Gate; not part of core video or score claims while disabled |

`report.review-priority` and `education.explain` are retired for the event. An old client calling them receives `CAPABILITY_DISABLED_FOR_EVENT`; the implementation must not quietly alias them to a new name with the same prohibited behavior. `followup.suggest-questions` is migrated to the narrower `report.clarify` contract, with tests that reject clinical investigation questions. Factual transcription of a worker's original words is labelled attributed evidence, never repurposed as SwiftAid advice.

### 15.2 Requirements

| ID | Priority | Requirement |
|---|---|---|
| SA-AI-01 | P0 | Every output must be labelled as AI-generated where shown and tied to capability, input snapshot, evidence/source versions, and model/prompt configuration. |
| SA-AI-02 | P0 | Accepting proposed factual changes must invoke the normal human-authorized command/correction path. No provider response can directly mutate clinical facts. |
| SA-AI-03 | P0 | Reject or retain as noncurrent any result based on superseded input; display staleness and allow regeneration where authorized. |
| SA-AI-04 | P0 | Preserve negation, unknowns, quantities, date ambiguity and human/animal distinctions. Unsupported facts must be omitted or marked unknown. |
| SA-AI-05 | P0 | Keep absence of evidence distinct from evidence of absence. A failed analysis cannot produce “no risk” or “no outbreak.” |
| SA-AI-06 | P0 | Use only allowlisted typed tools, with trusted user/job scope and bounded inputs/results. Model-selected arguments cannot expand access. |
| SA-AI-07 | P0 | Treat narrative, transcript, source text and tool responses as untrusted data; instructions embedded in them cannot override policy or execute tools. |
| SA-AI-08 | P0 | Do not expose arbitrary SQL, shell, file, web-fetch, classification, public-posting or credential tools to the model. |
| SA-AI-09 | P0 | A factual operational summary must distinguish requested, provided and receipt-confirmed assistance, and due/missed/completed follow-ups. |
| SA-AI-10 | P0 | Use evidence IDs and supporting excerpts for factual case/operational assertions; static education displays publisher/source references without AI-authored medical explanations. |
| SA-AI-11 | P0 | Unsupported factual claims and contradictory records produce explicit unknowns or a review request. Requests for diagnosis, clinical risk, treatment or medical education explanations are refused by the event capability boundary, not answered using RAG. |
| SA-AI-12 | P0 | Provider failure, unsupported language and budget exhaustion must produce visible fallback states while preserving case work and original audio. |
| SA-AI-13 | P0 | Enforce the plan's configured limits: at most three reasoning/tool iterations, four tool calls, one schema-repair retry, and bounded request duration/output. |
| SA-AI-14 | Gate | Enable Meta ASR only after the participant account, specific Meta model endpoint, media format, language behavior and credit eligibility are verified. No fixed price or universal language claim is made by this spec. Non-Meta transcription endpoints are prohibited. |
| SA-AI-15 | P0 | Measure field accuracy, missingness, unsupported claims, citation validity, latency and usage on fixed examples; report failures and sample sizes. |

### 15.3 Human review experience

The review card presents reported evidence, proposed factual fields or administrative action IDs, unknowns and input versions. The user can correct, accept selected facts, dismiss or regenerate. Fact acceptance uses normal case-correction commands. A plan approval is separate: it names the approved action set, permitted recipients, source versions and expiry.

A disclaimer or human review does not make AI medical advice part of this entry. Clinical questions receive a clear out-of-scope response without generated health recommendations. Existing officer decisions and source documents remain readable through ordinary permitted record/reference views.

### 15.4 AI language and cost boundaries

English is the initial route to test. Shona and siSwati are target languages, not promised capabilities. Preserve original-language audio even when transcription or translated education is unavailable. An unreviewed translation must never carry a clinical-approval label.

**Retained internal budget proposal:** $85 for Meta text/reasoning and agent workflows, $25 for eligible Meta voice transcription, $20 for Meta-model evaluation, and $20 unallocated reserve, totaling **$150**. These are internal usage caps, not verified price estimates, guaranteed coverage, or an entitlement to GPU hosting. Provider-reported credit balance/charges take precedence over local estimates. When prices are unknown, enforce request/audio duration limits, count actual usage, and conservatively pause optional calls before exceeding the credited balance. Hosting, object storage, network egress, partner services and independent self-hosted GPU costs are outside the assumed Meta Model API credit.

**Acceptance:** execute a real permitted Meta capability; test hostile input, stale facts, missing sources, unreceived assistance, denied scopes, failed language handling and exhausted budget. [I P0.3, P8.1–P8.4].

**Basis:** [I §§5.3, 14, 18, 21.1].


### 15.5 Meta-only model and transcription policy — 26 October 2026 decision

These requirements supplement `SA-AI-01`–`SA-AI-15` without renumbering them.

**SA-META-01 — P0.** All enabled generative operations, factual extraction, nonmedical language transformation and speech transcription MUST use verified Meta models exclusively. Medical/veterinary advice generation is excluded regardless of the model. .NET, Python, Microsoft AI/MCP, retrieval and deterministic processing remain infrastructure, not competing model providers.
- **SA-META-02 — P0.** The default inference route MUST be a verified participant-accessible **Meta Model API** endpoint. Do not invent the model ID, voice API capability, multilingual coverage, or price. API availability is separately gated for text, tools and audio.
**SA-META-03 — P1/Gate.** An eligible Meta open-weight route MAY run on project-controlled hardware or partner inference after license, runtime, budget, capability and real execution checks. The separate bonus requires Muse Glimmer or Muse Spark Open under the supplied guide/slide; provide actual evidence. Remote inference is not mobile offline AI and Meta API credit is not assumed to pay for it.
- **SA-META-04 — P0.** Fallback between self-hosted Meta open weights and hosted Meta API MUST be capability-aware, model-compatible, permission-aware and explicitly controlled. Only retry an idempotent model-only task using the same versioned minimal evidence snapshot; record which route generated the output. Never route private case content to a different endpoint if its approved processing policy forbids it.
**SA-META-05 — P0.** When no permitted Meta route is available, show AI/transcription unavailable. Preserve original audio, reporting and the ordinary support workflow; never use non-Meta model fallback or turn an outage into a clinical conclusion.
- **SA-META-06 — P0.** Set aggregate Meta Model API credit ceiling to **$150**, with planning envelopes **$85 reasoning**, **$25 transcription**, **$20 evaluation** and **$20 reserve**. Separate tracked allotments by request purpose, maintain an estimated-vs-reported usage ledger and block new optional requests when reserved budgets are exhausted. The reserve requires an explicit recorded transfer before consumption.
- **SA-META-07 — P0.** No provider API key, patient identifier, raw private audio, or case narrative is sent to an unapproved inference host. The gateway enforces minimum-necessary payloads, auth scope, rate limits and short timeouts. Provider retention and processing terms require review before real patient or farmer data can be sent.
- **SA-META-08 — P0.** Evaluate Meta text extraction and Meta transcription independently. Original recorded voice stays stored and playable even if no eligible Meta ASR endpoint exists, language evaluation fails, or remaining credits are insufficient.

**Meta-only acceptance extensions** (add to AT-14, AT-22, AT-23, AT-34, AT-36): a fake non-Meta provider is rejected at configuration and dispatch; Meta text call identifies model/provider and usage; Meta voice is either actually evidenced or disabled with original-audio playback; exhausted credits block model calls but not ordinary case work; self-host failure only switches to a permitted Meta API route; access-denied cases never enter a failover payload. These are requirements to test, not claims of completion.

### 15.6 Flagship: administrative continuity, with verified completion

**Recommended demonstration:** an officer's existing follow-up deadline has passed; a support request is marked Provided but has no receipt confirmation; an assignment has no acknowledgement. Meta inspects this permitted evidence, identifies the administrative gaps, and proposes a bounded action set. The authorized officer approves it. The deterministic executor creates a review task, records an assignment-review request and/or queues a generic internal reminder. A final read verifies actual task/receipt IDs. It does **not** mark assistance received or a patient recovered.

This is intentionally more than a summary chatbot. The agent can independently retrieve a bounded evidence set, assemble a plan and check effects, while protected writes remain approval-controlled. Autonomy is not clinical authority and is not a guarantee of bonus selection. [C01 §9; C02]

| ID | Priority | Requirement |
|---|---|---|
| SA-OPS-01 | P0 | Ground every plan in current permitted task/assignment/assistance evidence and cite stable IDs/versions; absence of a receipt means missing documentation, not fraud or clinical failure. |
| SA-OPS-02 | P0 | Allow at most three administrative actions per plan. P0 action kinds are CreateAdministrativeReviewTask, RecordAssignmentRequest and EnqueueInternalReminder for an existing accepted obligation. |
| SA-OPS-03 | P0 | Require explicit approval of the exact plan version/action set before writes. Approval carries actor, permitted scope, expiry and input versions; it cannot authorize future arbitrary model decisions. |
| SA-OPS-04 | P0 | Recheck access, action validity, cancellation/completion, recipient eligibility, plan expiry and input freshness immediately before every action. Changed inputs require review; no silent replay of stale authority. |
| SA-OPS-05 | P0 | Execute through deterministic application services and idempotent receipts, not general model write tools. Retries produce one effect per approved action. |
| SA-OPS-06 | P0 | Verify completion by re-reading accepted receipts/state; a plan, API timeout, sent notification or model statement cannot prove assistance received or a clinical action completed. |
| SA-OPS-07 | P0 | Track each action and the run as pending/approved/executing/partial/completed/no-action/needs-attention/expired/cancelled with truthful outcomes. No automatic compensation that erases immutable history. |
| SA-OPS-08 | P0 | Use staff-supplied schedules and existing deadlines. Do not derive medical urgency, follow-up interval or task priority from symptoms, condition labels or disease prevalence. |
| SA-OPS-09 | P0 | Show a compact audit of evidence IDs, selected action, approval, receipts, model/route and verification result; never expose hidden chain-of-thought or private prompts as the trace. |
| SA-OPS-10 | P0 | On denial, missing information, model/broker outage, no eligible recipient or zero justified actions, show no-op/needs-review without inventing an action or bypassing permission. |
| SA-OPS-11 | P0 | Evaluate factual grounding, proposed-action validity, approvals, duplicate resistance, stale-plan rejection and verified completion. Report actual baseline and end-to-end results on synthetic scenarios rather than predicted benefits. |
| SA-OPS-12 | P0 | Administrative plans cannot diagnose, select care, change clinical classification, choose medical tests, close cases, reassign ownership directly, verify assistance receipt, or post/send externally. Normal authorized human workflows remain available. |

An administrative review task is not a scheduled patient/animal visit. It is work for staff to inspect an existing record gap. Its due time is supplied/confirmed by the approving officer, never model-generated clinical timing. Actual protected case decisions remain separate ordinary human commands.

### 15.7 Qdrant AI memory and cache

**Purpose:** recall authorized previously recorded context and avoid repeating eligible inference, not create an autonomous patient profile or a second case database. Memory retrieval supplies evidence; caching reuses a prior eligible computation. Neither is a source of authority for assignments, clinical facts or approvals.

| ID | Priority | Requirement |
|---|---|---|
| SA-MEM-01 | P0 | Use Qdrant for scoped AI memory retrieval and reusable AI-result cache indexing. PostgreSQL remains authoritative for case events, memory provenance, consent, versions, approvals, schedules and invalidation state. |
| SA-MEM-02 | P0 | Separate case-context memory, approved organization operating context, opt-in account preferences and public nonclinical reference metadata. Never create one shared global patient memory. |
| SA-MEM-03 | P0 | Every memory item carries scope, source IDs/versions, trust/review state, purpose, creation/expiry and embedding/encoding version when relevant. A model summary is a derivative, not a new clinical fact. |
| SA-MEM-04 | P0 | Enforce tenant, permitted case, sector, sensitivity, current grants and active version before retrieval and reauthorize each result before it reaches a prompt. Missing scope means deny, not global search. |
| SA-MEM-05 | P0 | Anonymous narration and external documents remain untrusted evidence. They cannot change agent policy, trusted organizational procedures, account preferences or another report's memory. |
| SA-MEM-06 | P0 | Minimize Qdrant payloads; exclude raw audio, identity secrets, guest receipt proof, provider tokens, passwords and direct contact details. Treat vectors as sensitive derivatives, not anonymized data. |
| SA-MEM-07 | P0 | Source correction, grant change, source withdrawal, deletion and expiry immediately invalidate use through authoritative checks and enqueue index cleanup. Eventual vector deletion is not the authorization boundary. |
| SA-MEM-08 | P0 | Implement application-level expiry checks and deletion/reindex jobs; do not assume automatic Qdrant TTL or eviction behavior. Test expired items remain unusable even when cleanup is delayed. |
| SA-MEM-09 | P0 | Cache factual case output only with an exact canonical key binding account/scope, case/input versions, capability, model/prompt/schema, source and policy versions. Similar reports from different people never share a case answer. |
| SA-MEM-10 | P0 | Semantic cache reuse is limited to allowlisted non-sensitive administrative/logistical content with matching context versions. Never reuse approvals, clinical conclusions, care plans, assignment decisions, permissions or action receipts. |
| SA-MEM-11 | P0 | A cache hit must undergo the same capability, authorization, freshness and output checks as new inference. Cache data cannot bypass a disabled capability or authorize execution. |
| SA-MEM-12 | P0 | Cache lookup, miss and fallback are observable without logging private text. If Qdrant fails, continue core casework and either use current authoritative context for a budgeted Meta call or show an explicit unavailable state. |
| SA-MEM-13 | Gate | Dense embeddings and model-based rerankers must obey Meta-only inference policy and be verified. Do not assume a Meta embedding endpoint exists or silently enable a default non-Meta FastEmbed/cloud encoder. |
| SA-MEM-14 | P0 | When no approved dense encoder is verified, support explicitly labelled exact/ID/payload memory retrieval and exact caching in Qdrant where proven by the pinned build; do not call it semantic recall or fabricate vectors. |
| SA-MEM-15 | P0 | Guest processing has no cross-report personal memory; personal preferences require login and opt-in. Retrieval and response cache cannot expose rejected, expired or another organization's reports. |
| SA-MEM-16 | P0 | Run Qdrant privately with service authentication, access scoping, protected transport, bounds and tested recovery. Clients and public MCP endpoints receive no Qdrant credentials or arbitrary query access. |

**Memory classes:** transient request context (not long-term storage); case-scoped factual derivatives; human-approved nonclinical organization operating procedures; signed-in opt-in UI/language preferences; public nonclinical source/session metadata. Guest reports remain isolated unverified evidence and never seed organization-wide trusted memory. Approved medical source material stays fixed reference content, not an advice-generating memory tool.

**Cache policy:** exact-match, same-account/case/input-snapshot caching is the baseline for factual extraction and summaries. Semantic reuse may be enabled only for tested, nonsensitive logistics. A changed number, negation, source version, permission, model, prompt or time-dependent event must invalidate reuse where it affects meaning. Approved plans and execution results always use the normal current-state checks and deterministic receipt store, not cache replay.

**Encoding constraint:** Qdrant is storage/retrieval infrastructure, not an embedding model. The chosen encoder must be separately evidenced; no Meta embedding API or included embedding credit is assumed. The exact/payload path is useful while that gate is unresolved but must be labelled as nonsemantic. See implementation §14.8 and [technical sources](docs/TECHNICAL-SOURCES.md).

### 15.8 Multi-turn reasoning loops and resumable agent investigations — product addition

**Scope:** A multi-turn reasoning loop is a controlled sequence of separate, contextual **model turns and tool-result observations**, potentially interrupted by worker input, human review, or a later case revision. It is more than repeated RAG retrieval or a single model completion. The application stores **structured state and evidence references, never purported private chain-of-thought text**. It does not authorize autonomous clinical judgments. One reasoning cycle stays within the existing **maximum three model/tool iterations and four tool calls**; longer tasks continue only through a **new, explicitly authorized cycle** with a fresh budget and scope check. These are application-side controls, not provider guarantees.

| ID | Priority | Product requirement |
|---|---|---|
| SA-LOOP-01 | P0 | Define an explicit `AgentInvestigation` aggregate identified by run ID, objective, actor/tenant/case scope, allowed capabilities, case and source versions, status, current cycle/turn counters and budget reservation. Distinguish it from case-event authority. |
| SA-LOOP-02 | P0 | Implement a finite state machine `Created → GatheringEvidence → Reasoning → NeedsInput/AwaitingApproval → Resuming → Verifying → Completed`, with terminal `NoAction`, `Cancelled`, `Expired`, `Failed`, and recoverable `Stale` states. Persist only allowed state transitions. |
| SA-LOOP-03 | P0 | Each turn uses a versioned `TurnInput` containing the latest user statement or authorized tool result, a concise factual summary of prior observations, unresolved questions, and provenance. Never persist or expose hidden chain-of-thought reasoning. |
| SA-LOOP-04 | P0 | Use strictly typed read-only MCP tools with per-turn authorization and evidence IDs. Limit reasoning to administrative continuity, contradiction checking, record clarification, and assistance verification; clinical diagnosis/triage/advice remain prohibited in every turn. |
| SA-LOOP-05 | P0 | When new worker input or corrected case facts arrive, validate current identity, authorization, scope epoch, consent, source version, and case version before resuming. If mismatched, mark stale and restart from current permitted evidence rather than silently rebase an approved action. |
| SA-LOOP-06 | P0 | An investigation may ask up to three specific factual/administrative questions per turn, preserve unknowns, and pause for an authorized human reply. It must not invent answers, clinical follow-up intervals, or a medical examination request. |
| SA-LOOP-07 | P0 | Approval is separate from reasoning; bind exact plan hash, action IDs, current case/input versions, actor and expiry. Each approved action is reauthorized and verified using receipts; no model turn can execute a protected action without valid approval. |
| SA-LOOP-08 | P0 | Persist an opaque checkpoint after each accepted turn and tool result; durable resume after API/worker restart or network interruption must not repeat accepted commands, duplicate reminders or lose original user/audio evidence. |
| SA-LOOP-09 | P0 | Require a per-cycle maximum of three reasoning iterations, four tool calls, one schema-repair retry and configured wall-clock/token/credit caps. Additional cycles require current authorization and a new reserved budget; enforce global cumulative limits and explicit stop reasons. |
| SA-LOOP-10 | P0 | Qdrant may provide scope-filtered evidence IDs and exact cached factual results, but PostgreSQL remains authoritative for investigation state and consent/authorization. No unverified semantic encoder, stale cached authority, cross-user memory, or guest-personal long-term memory. |
| SA-LOOP-11 | P0 | Correlate asynchronous responses by run ID, turn ID, input snapshot hash, source versions and fencing token; reject or mark stale delayed/duplicate worker and provider responses. Never allow an old response to replace a newer reviewed result. |
| SA-LOOP-12 | P0 | Support `NeedsInput`, `NoAction`, `Partial`, `BudgetExhausted`, `CapabilityUnavailable`, `Cancelled` and `Stale` as honest UI states. A completed reasoning cycle is not a completed assistance task or verified clinical outcome. |
| SA-LOOP-13 | P0 | Guest reporters can respond only via scoped submission/receipt proof for their own intake clarification, without accessing organizational agent tools or case evidence. Guest inputs cannot poison organization-wide memory. |
| SA-LOOP-14 | P0 | Audit each turn's model/route/version, input and evidence references, tool names/results status, approval and action receipts, policy/budget consumption and current status without logging private raw narrative, patient identifiers, original audio or hidden reasoning text. |
| SA-LOOP-15 | P0 | Evaluate multi-turn loops on fixed synthetic scenarios: clarification after unknown facts, interruption/resume, stale corrections, role revocation, duplicate tool responses, partial action results, exhaustion, Qdrant outage and cross-tenant isolation. No claim of clinical or epidemiological correctness follows. |

**Illustrative nonclinical journey:** The worker reports an animal group concern → Meta extraction preserves the original narrative → the agent notices the *administrative* affected-animal count is missing → asks the worker one factual question → the worker answers later → the agent refreshes case-version and permission checks → it proposes an assignment-review request → an authorized officer approves the exact plan → the gateway issues the idempotent command → the agent verifies its receipt and reports what remains pending. No disease is diagnosed and the assistant does not prescribe intervention.

**Acceptance:** AT-73–AT-76, with explicit evidence of real separate turns, persisted checkpoints, correct stop conditions, no approval bypass, and no unauthorized Qdrant recall. The UI may show a concise *evidence and decision summary*, not hidden model reasoning.

## 16. Surveillance and One Health investigation

### 16.1 Competition gate and retained operational views

Descriptive, access-filtered counts of staff-reported records and ordinary case searches may remain. Disease/outbreak prediction, inferred transmission, clinical risk scoring and AI-guided epidemiological investigation are **not enabled in the default competition build**. The supplied anti-project wording is broad, and no source establishes an approved surveillance exception. [C01 §4]

`CrossSectorInferenceEnabled=false` by default. `signal.investigate` and optional detectors require an explicit product decision and applicable privacy/source/evaluation validation; the optional event profile additionally requires its written organizer response. An unanswered question is not approval. These requirements retain future design detail and gate tests, but a disabled feature is not a completed detector or an included demo capability. The core entry demonstrates operational continuity without it.

### 16.2 Requirements

| ID | Priority | Requirement |
|---|---|---|
| SA-SUR-01 | P1/Gate | Analyse only job-authorized, minimized snapshots and clearly distinguish event/onset time from reporting/upload time. |
| SA-SUR-02 | P1/Gate | Use deterministic configured detection rules; attach algorithm/config version and input versions to each candidate. |
| SA-SUR-03 | P1/Gate | Deduplicate case identity/replayed commands and treat animal groups/counts separately from report counts. |
| SA-SUR-04 | P1/Gate | Display observed counts, time window, geography/precision, contributing evidence, missing data, counterevidence and review state. |
| SA-SUR-05 | P1/Gate | Use “Possible pattern for review” instead of “Outbreak confirmed” or an unsupported disease probability. |
| SA-SUR-06 | P1/Gate | Support human review, request for more evidence, dismissal and escalation; store actor, reason and evidence versions. |
| SA-SUR-07 | P1/Gate | Corrections, invalid records and changed permissions must recompute or supersede affected analysis. Old analysis remains auditable but not current. |
| SA-SUR-08 | P1/Gate | A cross-sector reviewer must not acquire underlying private case access merely by opening a signal. |
| SA-SUR-09 | P1/Gate | Exclude HIV from the generic acute detectors/public outbreak maps. Do not apply the acute gastrointestinal demonstration algorithm to TB continuity work. |
| SA-SUR-10 | P1/Gate | Retain negative controls: unrelated reports, different time windows, delayed batches, missing onset and misleading herd totals must not become fabricated outbreaks. |
| SA-SUR-11 | P1/Gate | Reject synthetic-demo detection settings in production; approved local protocols, validation and suitable baselines are prerequisites for operational alerts. |
| SA-SUR-12 | P0 | Only staff-authored, authorized, non-identifying material may enter a public advisory publication workflow. No AI health-signal decision publishes or generates medical advice; the optional signal path remains separately gated. |

### 16.3 Retained optional synthetic fixture scope — not enabled by default

The source plan defines only three positive demonstration patterns: an acute gastrointestinal human cluster, an animal-event/mortality cluster, and a cautious cross-sector spatial/temporal overlap. Other conditions retain case-management and knowledge functionality without a claimed validated detector.

For reproducibility, preserve the existing fixture configuration: three distinct human cases within 72 hours and 5,000 metres; three distinct animal reports within 72 hours and 5,000 metres; and an overlap scenario with at least one human case and one animal report within 168 hours and 5,000 metres. **These are synthetic software-test parameters, not medical or veterinary thresholds.** Their appearance in a configuration must be labelled `synthetic-demo-only`.

### 16.4 Evidence presentation

Human cases and animal reports appear in separate evidence sections with their respective denominators. The review may identify a missing exposure question, but must not invent a shared water source, farm contact, movement route or laboratory result. Evidence links reveal only permitted data. Unknown geography may limit a spatial analysis; it is not silently assigned to the worker's current GPS.

Maps are optional. A list/table with evidence and time/area information is sufficient for P0. Any map uses authorized aggregation and clearly synthetic demo locations; private precise locations must not be leaked to a third-party map/analytics service.

**Conditional acceptance:** only after explicit product approval and applicable privacy/source/evaluation validation, test the positive/negative/correction patterns and restricted cross-sector review. An event submission additionally requires its recorded organizer clarification. With the gate closed, acceptance is rejection of the endpoint, no job dispatch, no signal UI claims, and a complete operational demonstration without it. [I P9.1–P9.3].

**Basis:** [I §§12.4, 15, 16.2].

## 17. Health knowledge, education, and source governance

### 17.1 Three separate knowledge classes

1. **Health/veterinary reference and education:** approved or demo-labelled source material, versioned and cited.
2. **Local operational directory/procedures:** verified referral options, contacts and role-specific procedures; demonstration entries remain fictitious and labelled.
3. **Case evidence:** private observations, reports and discussions. A case comment never becomes general medical guidance merely because it was written by a worker.

### 17.2 Source set inherited from the plan

| Publisher/source family | Initial material | SwiftAid use | Boundary |
|---|---|---|---|
| WHO | Nine identified human-condition fact sheets | Human education and reference retrieval | Not proof of a complete country-approved clinical protocol |
| GTFCC | Cholera surveillance resources, job aids and forms/line-list templates | Reporting schema/reference foundation | Templates are not populated patient datasets |
| WOAH | Nine animal-disease pages, disease catalogue, WAHIS context | Veterinary/One Health reference and controlled catalogue growth | No assumed unrestricted dataset/API or local prevalence |
| FAO | Identified animal-health situation material | Context and source-linked animal/One Health education | Not local case truth or linked patient/farm records |
| Zimbabwe health/veterinary authorities | Applicable material when obtained and checked | Local protocols, referral routes and operational applicability | A complete current approved pack was not established in the plan |
| Eswatini health/veterinary authorities | Applicable material when obtained and checked | Same, for the Eswatini workspace | Same verification boundary |

Full resource IDs/links are reproduced from the plan in Appendix A. Link availability, permission to reuse, clinical review and local applicability are separate checks.

### 17.3 Requirements

| ID | Priority | Requirement |
|---|---|---|
| SA-KNW-01 | P0 | Ingest only an explicit approved-source manifest; models and case narratives cannot supply arbitrary fetch URLs. |
| SA-KNW-02 | P0 | Record publisher, title, URL, publication date when known, retrieval date, content hash/version, language, sector, audience, country applicability, rights and review status. |
| SA-KNW-03 | P0 | Keep source identity, reuse permission, clinical review, translation review, publication and withdrawal as separate properties. |
| SA-KNW-04 | P0 | Filter by authorization/applicability/review state before retrieval ranking. Country/global relevance must be visible; unresolved local applicability must remain explicit. |
| SA-KNW-05 | P0 | Preserve heading/paragraph/page and source edition for displayed reference material. Operational AI cites factual evidence/procedure metadata; it does not generate medical explanations from retrieved passages. |
| SA-KNW-06 | P0 | Start with a small useful source-linked pack rather than making a complete medical corpus a delivery dependency. |
| SA-KNW-07 | P0 | Keep full fetched documents out of public distribution unless reuse rights are resolved. Attribution alone must not be treated as permission. |
| SA-KNW-08 | P0 | Human-authored demonstration summaries are `DemoOnly` unless properly reviewed. Do not use a model to author, simplify or translate medical/veterinary guidance for this entry; show static reference content with its source and status. |
| SA-KNW-09 | P0 | Deliver versioned offline education with language, source, review state, last successful update and withdrawal handling. |
| SA-KNW-10 | P0 | After a known withdrawal, remove the item from current recommendations. Historical case references may remain, clearly indicating the obsolete/withdrawn version. |
| SA-KNW-11 | Gate | Shona/siSwati health-education materials require human/domain-reviewed translations and source rights. The competition route does not generate health advice or translated medical explanations on demand. |
| SA-KNW-12 | P0 | Missing/conflicting reference content yields unavailable or a request for human content review. Do not generate substitute clinical instructions. |
| SA-KNW-13 | Gate | Real operational contact/procedure information requires institutional review. The competition uses a static reference library and synthetic contacts, never advice-generating retrieval; it does not depend on real patient data. |

### 17.4 Education card behavior

A card contains title, topic/condition tags, audience, sector, language, concise content, source references/versions, publication/review labels and offline availability. It may link to approved source media, but should prefer small text-first downloads. Card content can support a session without carrying any patient-specific context.

A source update creates a new human-review version; it is not silently pushed as medical policy or rewritten by AI. Health material is displayed as fixed source-linked content, not the answer of a generative medical assistant. Public users see only approved public projections or clearly demo-labelled public content in the demonstration environment. Restricted clinical checklists do not become public merely because their source website is public.

**Acceptance:** retrieve an existing source-linked reference card without generated health prose; deny disallowed sector/sensitive content; show no-source behavior; cache a pack offline; withdraw a version; verify it stops appearing as a current recommendation after refresh. [I P7.2, P7.3, P8.3].

**Basis:** [I §§13, 14, 18.3, 24].

## 18. Community education sessions, Facebook, and WhatsApp

### 18.1 Product promise

Officers can schedule education sessions, select source-linked materials, publish a generic invitation, export a calendar entry, and share/open an external session destination. SwiftAid manages the schedule and its own content; it does not promise to host a video meeting or provision social-platform groups/events automatically.

### 18.2 Session record

Required product fields are title, public summary, topic/condition tags, non-sensitive audience description, country/region, sector, presenter, language, start instant and IANA time zone, duration, approved/demo-eligible material versions, visibility, external destination type/link where applicable, reminder configuration, and status. Capacity is optional. Attendee lists and contact details, if collected, are private and separate from the public invitation.

Lifecycle: `Draft → Reviewed → Scheduled → Completed`, or `Cancelled`. Updates and cancellations are versioned. **PC-08:** one authorized officer may perform draft and publication actions for the demonstration where policy permits; a recorded review is still required, and the UI must not imply independent clinical review that did not occur. Production separation-of-duties policy remains an institutional decision.

| ID | Priority | Requirement |
|---|---|---|
| SA-SES-01 | P0 | Create, review, schedule, edit, cancel and complete a session without any social-platform API dependency. |
| SA-SES-02 | P0 | Select source-linked public-safe materials and a language/review state appropriate to the session. |
| SA-SES-03 | P0 | Display local time zone and preserve a correct stored instant. A changed date/time must produce an updated version. |
| SA-SES-04 | P0 | Generate a public calendar file with stable UID and incremented sequence for updates; omit private attendee/contact data. |
| SA-SES-05 | P0 | Offer copy invitation, native/manual sharing, and open destination for approved generic content. |
| SA-SES-06 | P0 | Facebook/WhatsApp destination links may be entered after the officer creates/configures the destination externally. Do not claim automatic group/event/live creation. |
| SA-SES-07 | P0 | Exclude patient names, case IDs, private original audio, clinical details and diagnosis-derived audience membership from invitations. |
| SA-SES-08 | P0 | A disease may be a public education topic, but attendance/audience targeting must not disclose that named participants have that disease. |
| SA-SES-09 | P0 | Store reminders durably and recheck session version, cancellation, public approval and consent/opt-out at dispatch. |
| SA-SES-10 | P0 | Distinguish prepared, manually shared, provider-submitted, confirmed delivered, unknown, failed and cancelled outcomes. Opening a share sheet is not delivery. |
| SA-SES-11 | P1/Gate | Automated Facebook/WhatsApp adapters require verified account permissions, applicable platform rules, test provisioning, explicit send authorization and authenticated receipt handling. |
| SA-SES-12 | P0 | A timeout after an external send may mean unknown outcome; do not blindly resend and claim exactly-once delivery. |
| SA-SES-13 | P0 | Cancellation suppresses future reminders; revised sessions require approved update content. |
| SA-SES-14 | P0 | Automated tests use fake/sandbox recipients and never message real people. The demonstration must distinguish manual, sandbox and actual provider paths. |

### 18.3 Public experience

Public visitors can view published session title, presenter, topic, language, schedule, general locality, approved description/materials and destination link. They can add the session to a calendar or open the destination. A public visitor does not need staff case access to use these functions.

A link click may be measured as a link action, not verified attendance. Manual attendance counts or provider receipts, if used, must identify their provenance. Full registration/contact-management depth is P1 unless a specifically reviewed minimal path is implemented.

**Acceptance:** create a session; review/publish; export its calendar; prepare a generic WhatsApp/Facebook invitation; change its time; cancel it; verify reminder suppression and absence of private case fields. [I P10.1, P10.2, P6.3].

**Basis:** [I §17; PC-08].

### 18.4 Signed-in event subscriptions and organizational access

| ID | Priority | Requirement |
|---|---|---|
| SA-SUB-01 | P0 | Visitors may browse approved events/materials and open public destinations without login. Subscribing, registering an interest and receiving account-linked reminders require sign-in. |
| SA-SUB-02 | P0 | Allow an ordinary Facebook/Google/SSO-authenticated member to subscribe to a public session without joining an organization; event subscription grants no staff permissions. |
| SA-SUB-03 | P0 | Persist one subscription per account/session with idempotent subscribe, preference change and unsubscribe actions. External-channel opt-in is separate from account login and topic selection. |
| SA-SUB-04 | P0 | Recheck subscriber status, channel consent, session publication/version and cancellation when dispatching. Unsubscription suppresses future notifications and cannot be undone by a stale worker job. |
| SA-SUB-05 | P0 | Attendee/subscriber lists and contacts are private and absent from public ICS, share links and pages. Joining a health education session must not be treated as evidence of a diagnosis. |
| SA-SUB-06 | P0 | Return users to the selected session after login without losing an in-progress guest draft. Reject untrusted return URLs; offline interest is a local pending action, not a confirmed server subscription. |
| SA-SUB-07 | P0 | Organization creation/joining, invitations, assignments and escalations require a signed-in account plus the corresponding membership and action grant. Creating an organization does not grant global catchment or professional authority. |

Guest visitors may read the event page, copy a public invitation, open a public destination or export its public calendar. Clicking **Subscribe** preserves the event context and requests Facebook, Google or organization SSO; it does not force organization membership. No subscriber list is public, and subscriptions are not attendance receipts or medical records.

## 19. Human and animal condition coverage

### 19.1 Meaning of coverage

Coverage means the system can register observations, retain evidence, assign work, track support, schedule follow-ups, record authorized decisions, and retrieve available applicable knowledge. It does **not** mean automated disease confirmation, approved treatment pathways for every condition, or proof that a disease is common in both target countries.

### 19.2 Human catalogue

| Code | Display label | Required product treatment |
|---|---|---|
| `HUM_CHOLERA` | Cholera | Allow reported/suspected concerns and reviewed evidence; reference WHO/GTFCC material. |
| `HUM_TYPHOID` | Typhoid | Support observations, testing/referral records and continuity; no symptom-only confirmation. |
| `HUM_AWD_NON_CHOLERA` | AWD — non-cholera classification | Require a recorded authorized basis for non-cholera classification. Until then use cause-unconfirmed/unclassified AWD observations. |
| `HUM_MEASLES` | Measles | Capture reported observations and relevant reported immunization/investigation facts with uncertainty. |
| `HUM_HIV` | HIV | Enforce additional sensitivity, confidential support/follow-up, and exclusion from generic public acute-outbreak mapping. |
| `HUM_TB` | Tuberculosis | Support longitudinal care coordination and authorized investigation; do not reuse the acute AWD algorithm. |
| `HUM_POLIO` | Polio | Support concern/investigation workflow without inferring diagnosis from weakness alone. |
| `HUM_MALARIA` | Malaria | Support observations, test/referral records and follow-up; no automated drug/dose selection. |
| `HUM_COVID` | COVID-19 | Support reported observations, evidence, follow-up and versioned education. |
| `HUM_UNCLASSIFIED` | Unclassified human concern | Allow reporting without a known condition. |

`ACUTE_WATERY_DIARRHOEA` is a syndrome tag separate from the non-cholera condition classification. A missing cholera test is not a rule-out.

### 19.3 Starter animal catalogue

| Code | Display label | Reporting emphasis |
|---|---|---|
| `ANI_RABIES` | Rabies | Animal concern/exposure evidence and authorized human/animal follow-up where granted |
| `ANI_ANTHRAX` | Anthrax | Mortality/possible exposure report; professional review without generated hazardous examination instructions |
| `ANI_BRUCELLOSIS` | Brucellosis | Livestock/exposure observations and authorized investigation |
| `ANI_FMD` | Foot-and-mouth disease | Livestock support and veterinary response tracking |
| `ANI_ASF` | African swine fever | Pig/herd observations and mortality reporting |
| `ANI_NEWCASTLE` | Newcastle disease | Poultry/flock observations and support |
| `ANI_AVIAN_INFLUENZA` | Avian influenza | Bird/poultry reporting and permitted exposure investigation |
| `ANI_LUMPY_SKIN` | Lumpy skin disease | Cattle/herd observations and support |
| `ANI_PPR` | Peste des petits ruminants | Sheep/goat group reporting and authorized review |
| `ANI_UNCLASSIFIED` | Unclassified animal concern | Unknown cause, other species/group concerns, or topics outside the starter catalogue |

Neutral animal tags include `UNUSUAL_MORTALITY`, `RESPIRATORY`, `GASTROINTESTINAL`, `SKIN_LESIONS`, and `REPRODUCTIVE_EVENT`. Local reviewers may adjust species applicability and expand the catalogue. No fixed endemic/common-disease assertion is made.

### 19.4 Requirements

**SA-CAT-01 — P0.** Seed every requested human profile, every starter animal profile, and both unclassified routes with stable codes and source references.

**SA-CAT-02 — P0.** Keep operational catalogue breadth separate from protocol and detector availability. All requested profiles support recordkeeping; clinical advice engines and unclarified health-pattern inference remain disabled.

**SA-CAT-03 — P0.** Do not force a human/animal association for any profile or classify every listed disease as zoonotic.

**SA-CAT-04 — P0.** A catalogue label does not grant a worker authority to record an official classification; normal professional/action permissions still apply.

**SA-CAT-05 — Gate.** Local reporting requirements and professional procedures need reviewed material for real use. Their future approval MUST NOT silently enable excluded AI clinical capabilities in the competition build.

**Acceptance:** every profile is selectable in appropriate permitted context and supports the same generic operational lifecycle; unclassified reporting works; disabled detectors and unavailable guidance are visible rather than fabricated. [I P7.1, P2.2, P7.3].

**Basis:** [I §12].

## 20. Web and mobile experience specification

### 20.1 Experience principles

The application should emphasize **My work → next action → evidence → outcome**, not a wall of charts. Use human/animal icons and text, a consistent vocabulary, compact confirmation rather than repeated forms, and explicit offline/review labels.

**PC-04:** The screen catalogue below consolidates the previously discussed visual concepts into functional contracts. It describes navigation/view states, not a requirement for a separate deployable page for every row. The six core mobile and six core officer views remain the P0 implementation baseline; supporting views can be panels, tabs or dialogs.

### 20.2 Mobile screen catalogue

| ID / screen | Primary information | Primary actions | Required alternate states |
|---|---|---|---|
| M00 — Public start / sign-in / workspace | Report without an account, public library/events, provider options | Continue as guest; Facebook; Google; organization SSO; permitted workspace | First-run offline guest capture; unavailable provider; no membership; expired staff access |
| M01 — My work | Assigned cases, due/overdue work, unresolved assistance, escalations, sync badge | Report concern, open next action, open assigned case | No work assigned, offline cached view, access expiry, pending work |
| M02 — Report concern | Human/animal selection, voice/text/chips, known context, unknowns | Record, enter observations, save draft, proceed to review | Microphone/GPS denied, interrupted recording, low storage, unsupported ASR |
| M03 — Review report | Confirmable extracted/manual facts and original evidence | Accept selected facts, correct, leave unknown, submit/queue | Offline no inference, pending extraction, contradictory counts, validation error |
| M04 — Case workspace | Summary, owner, next action, assistance, follow-ups, discussion, timeline | Record observation, assistance activity, voice/text comment, escalation; permitted correction | Stale projection, denied scope, pending local actions, resolved history |
| M05 — Follow-up capture | Existing case context, purpose, due time, unresolved questions | Record outcome/voice, complete, mark attempt missed/unreachable, reschedule where allowed | No connectivity, stale case, unsupported suggestion, action conflict |
| M06 — Education library | Human/animal/One Health topics, source/version, language, downloaded status | Read source-linked card, inspect source, download allowed pack | No approved item, DemoOnly label, withdrawn/stale pack, unavailable language |
| M07 — Sessions | Published generic schedule, language, presenter and destination | View material, add calendar, open/share approved destination | Cancelled/rescheduled session, stale offline schedule, no destination |
| M08 — Sync and privacy | Last confirmation, queued commands/media, conflicts, grant expiry | Retry, inspect problem, reconcile, safe sign-out | Server unavailable, authorization blocked, storage limit, expired grant |
| M09 — Original voice / transcript | Original playback, metadata, transcript revisions | Play/pause, expand transcript, permitted correction | Audio local-only, uncached, upload failed, ASR failed, permission revoked |

M01, M02/M03, M04/M09, M05, M06/M07, and M08 correspond to the plan's six mobile modules. M00 is bootstrap, and voice/transcript is embedded in case discussions rather than replacing them with a separate chat app.

### 20.3 Officer and public web catalogue

| ID / screen | Primary information | Primary actions | Required alternate states |
|---|---|---|---|
| W01 — Regional work queue / overview | Scoped cases, unacknowledged escalations, overdue follow-ups, unmet needs, sector filters | Open case, filter authorized scope, choose next work item | Empty region, restricted sensitivity, stale counts, failed load |
| W02 — Case workspace | Current facts, ownership, assistance, tasks, escalation, original audio, immutable timeline | Assign/reassign, acknowledge, record authorized decisions, resolve/reopen | Version conflict, missing evidence, pending media, denied action |
| W03 — Operations agent runs / optional signals | Administrative evidence, plan version, approval, receipts and verification; optional signal tab hidden behind product-approval/validation gate (plus event clearance only for event mode) | Inspect/approve/cancel permitted plan; inspect action outcomes | No justified action, stale approval, denied scope, partial execution; signal gate disabled |
| W04 — Knowledge and education review | Static/human-authored source editions, rights/review/language status, education cards | Review, publish allowed material, withdraw, inspect source | Source fetch failure, missing rights, unreviewed translation, no applicable protocol |
| W05 — Community sessions | Calendar/list, editor, presenter/materials, public invitation and channel states | Draft, review, schedule, revise, cancel, export/share | Social adapter unprovisioned, ambiguous send result, no approved material |
| W06 — Operational health | Redacted queue/job/sync failures, disabled capabilities, usage and recovery state | Authorized retry, pause inference/sends, inspect safe failure | Broker/provider outage, budget limit, dead-letter backlog; no patient payloads |
| W07 — Operational analytics (P1 depth) | Authorized workload, action ageing and outcomes with explicit denominators | Filter, inspect approved breakdowns, permitted export if implemented | Incomplete window, scope changes, suppressed small groups, unavailable denominator |
| W08 — Public reporting and information | Account-free human/animal form, private receipt, approved education/events | Report; review submitted date/location context; subscribe after login; inspect own receipt proof | Pending intake, unavailable coverage, abuse limit, offline web, revoked receipt; no private-case directory |

The plan's six officer screens are W01–W06. Case-list filtering belongs to W01, the immutable history is part of W02, and optional maps/health signals belong to a gated W03 tab. W03 primarily presents nonclinical agent runs in this revision. W07 must not displace essential case functions. W08 is a separately serialized public boundary, not a privileged dashboard with controls hidden.

### 20.3a Additional product view states

M02/M03 and public W08 share the human/animal and date/location contract without assuming a signed-in reporter. M08 shows guest and staff queues separately. M07 shows public browsing plus signed-in subscription status. W01 gains an **intake review** tab available only to scoped reviewers; W06 shows redacted Qdrant/auth/abuse health. Add an account/session view for linked providers and subscriptions, and an organization settings view for approved SSO and membership configuration; they may be panels rather than extra applications.

Qdrant status distinguishes memory unavailable, exact-cache hit, semantic-cache hit (only when verified), stale memory ignored and live result. Never display an old cache hit as fresh reasoning or a guest receipt as an accepted professional case.

### 20.4 Common interface requirements

| ID | Priority | Requirement |
|---|---|---|
| SA-UX-01 | P0 | Use the same state terminology across mobile, web and API errors; never use one green success badge for both queued and accepted work. |
| SA-UX-02 | P0 | Show human/animal context and ownership clearly without exposing identifiers unnecessarily. |
| SA-UX-03 | P0 | Keep original audio playable in comments; make transcript optional/secondary. |
| SA-UX-04 | P0 | Every screen must include empty, loading, failure, permission-denied and applicable offline/stale states. |
| SA-UX-05 | P0 | Reuse known case context and display unknowns. No hidden defaults that manufacture observations, dates, counts or consent. |
| SA-UX-06 | P0 | Label controls for accessibility and do not rely on colour alone for status. Text routes remain available for noise, privacy or speech-access limitations. |
| SA-UX-07 | P0 | Show protected action confirmation for publishing, reassignment, closure, reopening and destructive local data operations. |
| SA-UX-08 | P0 | Visual mockups are illustrative only. Product rules override mockup text implying confirmed outbreaks, offline cloud AI, automatic role grants, or delivered messages without evidence. |
| SA-UX-09 | P1 | Apply the proposed readability and performance evaluation targets in §24 and record actual device/browser results. |

**Copy examples:** “Saved on this device — waiting to submit”; “Server accepted — original audio still uploading”; “Original audio available — transcript unavailable”; “AI suggestion — review before applying”; “Possible pattern for review — not a confirmed outbreak”; “Manual sharing opened — delivery not verified.”

**Acceptance:** a fresh operator can complete the main journeys on the release APK and web interface without hidden shortcuts or developer explanation of misleading states. [I P11.1, P11.2].

**Basis:** [I §§9, 16, 17, 18; PC-04].

## 21. Notifications, operational analytics, and exports

### 21.1 Notifications

**SA-NOT-01 — P0.** Internal attention items must be linked to accepted work, have a current recipient and state, and be scoped to that recipient's authorization.

**SA-NOT-02 — P0.** External/lock-screen text must be generic by default, such as “A case needs your attention.” Disease names, patient/farmer identity, precise case locations and voice excerpts must not appear in such text.

**SA-NOT-03 — P0.** Opening, reading, receiving or dismissing a notification must not complete the underlying task, acknowledge an escalation, or prove receipt of assistance.

**SA-NOT-04 — P0.** Reminder processing must re-evaluate completion, cancellation, reassignment and permission before delivery; stale reminders must be suppressed.

**SA-NOT-05 — P0.** Repeated dispatch or recovery after failure must not create repeated accepted internal notification occurrences. External delivery uncertainty follows §18, not a universal exactly-once claim.

**SA-NOT-06 — P1.** Add push/SMS or further channels only after privacy, authorization, recipient consent and actual provider behavior are verified. They are not P0 dependencies.

### 21.2 Operational measures

Counts and dashboards must state their scope, time window, last update and units. These metrics are proposed product evaluation definitions, not achieved results.

| Metric | Definition | Interpretation limit |
|---|---|---|
| Ownership coverage | Accepted active cases with one eligible accountable owner / accepted active cases in permitted scope | A good count does not prove actual field action |
| Ownership acknowledgement | Accepted assignments acknowledged / assignments requiring acknowledgement in the window | Not synonymous with case resolution |
| Due follow-up completion | Scheduled tasks completed with an outcome by their recorded due time / eligible due tasks | Rescheduled/missed/cancelled outcomes shown separately |
| Unreceived assistance | Requests recorded Provided but not ReceiptConfirmed | Provision is not receipt |
| Open unmet needs | Active assistance requests not fulfilled or explicitly disposed | No deduction merely because case summary mentions assistance |
| Escalation acknowledgement | Time from accepted escalation to recorded acknowledgement | Offline capture time and server acceptance delay shown separately |
| Case continuity | Cases with recorded next owned action / accepted active cases, with explicit exceptions | Not a clinical outcome score |
| Sync backlog | Pending/blocked local commands and pending original media | Local-only draft totals are not confirmed server case totals |
| AI usefulness | Reviewed proposals, accepted/corrected/dismissed results, unsupported assertions and latency | Acceptance by a worker is not clinical validation |
| Education activity | Published sessions, completed sessions, verified or manually recorded attendance by provenance | Link clicks and shares are not attendance |

**SA-ANA-01 — P0.** Filter authorization before aggregation, not merely before displaying detailed records.

**SA-ANA-02 — P0.** Label human cases, animal reports and animals affected separately. Never combine their totals into an unexplained “patients affected” metric.

**SA-ANA-03 — P0.** Label condition-based counts by classification, such as reported/suspected/confirmed, and use evidence-backed classification only. “All cholera cases” must not silently mix confirmed cases with unreviewed AI suggestions.

**SA-ANA-04 — P0.** Distinguish case closure from recovery and staff activity from assistance fulfilment.

**SA-ANA-05 — P1/Gate.** Public statistics require approved aggregation, small-group/location risk policy and sensitivity exclusions. Without that policy, publish education/sessions only, not raw case statistics.

**SA-ANA-06 — P1.** Private CSV/PDF/report exports may be added only with resource/sensitivity policy, explicit field selection, audit and safe destination handling. They are not P0 promises; public session ICS remains P0.

**Basis:** [I §§7, 16.3, 17.4, 18, 20.1; PC-05 for measurement definitions].

## 22. Privacy, security, and trust requirements

### 22.1 Data classes

| Class | Examples | Default handling |
|---|---|---|
| Private subject/evidence | Patient/farmer contacts, exact locations, voice originals, transcript text, personal observations | Explicit case/sensitivity scope; no public endpoints |
| Restricted operations/intelligence | Assignment details, private follow-up records, candidate signals, AI investigations | Authorized role/region/sector/action scope |
| Public approved content | Reviewed advisories, public education, generic session information | Separate published public projection |
| Technical metadata | Opaque job IDs, durations, safe failure codes, counts and capability state | Restricted operations access; no embedded private content |

### 22.2 Security requirements

| ID | Priority | Requirement |
|---|---|---|
| SA-SEC-01 | P0 | Default deny private reads/protected actions; explicitly allow bounded anonymous intake. Enforce scope on attachments, AI tools, memory, caches and subscriptions. |
| SA-SEC-02 | P0 | Use private media storage and authenticated access. A transcript inherits the same privacy restrictions as its original case/audio. |
| SA-SEC-03 | P0 | Keep provider credentials, social tokens, broker secrets and database authority off mobile clients and out of prompts. |
| SA-SEC-04 | P0 | Queue subjects/messages must contain opaque job references, not diagnoses, identifiers, transcripts, precise GPS, audio, private URLs or credentials. |
| SA-SEC-05 | P0 | Logs, traces, test artifacts and routine error messages must not expose sensitive payloads, tokens, recordings or signed private URLs. |
| SA-SEC-06 | P0 | Restrict original-history modification by application identity; permit controlled migration/retention authority separately. Hashes do not justify calling the system tamper-proof. |
| SA-SEC-07 | P0 | Separate identifying and sensitive payloads from immutable envelopes so governed correction, redaction and erasure can be supported without pretending history can never change under authorized privacy procedures. |
| SA-SEC-08 | P0 | Use synthetic personal/farm data, fictitious facilities and consenting speakers for the demo; do not use live patient records as convenient test fixtures. |
| SA-SEC-09 | P0 | Exclude restricted HIV and other sensitivity classes from unapproved counts, public audience targeting and cross-sector evidence. |
| SA-SEC-10 | P0 | Record sensitive administrative decisions, grants/revocations, publication and controlled retention actions in a restricted audit trail. |
| SA-SEC-11 | P0 | Account switching, sign-out, cache rebuilding and assignment changes must preserve privacy without silently destroying pending user work. |
| SA-SEC-12 | Gate | Real use requires reviewed legal/consent/processing arrangements, tested transport/storage encryption, source rights, retention policy and incident procedures. |
| SA-SEC-13 | P0 | Do not describe public-source availability, synthetic-demo success or API authorization tests as institutional approval, clinical validation or complete legal compliance. |

### 22.3 Retention and deletion

Retention periods remain policy decisions, not hardcoded speculative values. The default demonstration uses disposable synthetic data with deliberate reset controls. Real deletion must consider original audio, derived transcripts, cached copies, AI results, indices and backups, while preserving only permitted non-identifying audit metadata.

Recording a correction is distinct from deleting sensitive data. “Immutable” describes the normal case-history write model; it does not mean retaining every identifier and voice recording permanently. Access withdrawal and content withdrawal cannot reach an offline device instantly.

### 22.4 Scope of safety

SwiftAid is not an emergency service and must not promise that an offline alert has reached an officer. It must not recommend diagnosis, medicine/dose, carcass examination/handling, quarantine or animal movement controls from model memory. Actual urgency routes and professional actions require locally approved procedures and appropriately authorized users.

**Acceptance:** execute cross-country/region/sector/sensitivity tests across every surface, inspect safe logs and queue payloads, test revoked media access, and verify no private field appears in a public session or public serialized DTO. [I P1.2, P3.3, P8.1, P10.2, P11.2, P12.1].

**Basis:** [I §§1, 6.1, 7, 9, 10, 14, 18.3, 21].

## 23. Architecture constraints and integration contracts

This is a product specification, not a second package installation guide. The following constraints preserve the agreed implementation direction. Exact versions, code interfaces, schema files and library compatibility belong to `implementation.md` and its capability gates.

| Component | Fixed direction | Product consequence |
|---|---|---|
| Mobile | Native Android-first .NET MAUI Blazor Hybrid | Original recording/playback and reliable local storage; not a PWA replacement |
| Application authority | ASP.NET Core/.NET application | Identity, case commands, authorization, immutable history, assignments, approvals and public projections |
| Local persistence/sync | SQLite + Datasync Community Toolkit | Staff projection sync; separate staff command and guest intake queues; private attachments |
| Identity/federation | ASP.NET Core Identity + Google/Facebook adapters + organization OIDC; maintained protocol library | Accounts separate from membership; anonymous intake allowed |
| AI memory/cache | Qdrant behind the .NET gateway | Scoped derived memory and exact/eligible semantic result retrieval; not the system of record |
| Authoritative store | PostgreSQL + PostGIS | Immutable accepted evidence and explicit geographic calculations; derived views can be rebuilt |
| Worker service | Python | Bounded asynchronous analysis, knowledge ingestion and evaluations |
| Gateway queue | NATS JetStream | Durable queued work with idempotency and observable retries; never direct mobile broker access |
| AI/MCP | Microsoft AI/MCP tooling, .NET-controlled Meta integration | Constrained authorized capabilities; no model-held application authority |
| Original media | Private object storage with separate verified upload/playback | Original bytes survive; transfer state independent from case and transcript state |
| Education/social | Native scheduler/public projection/manual share first | Core product remains usable without platform API approval |

### 23.1 Requirements

**SA-TEC-01 — P0.** Keep the earlier SwiftAid application untouched. Build a fresh implementation; retain honest provenance wherever required.

**SA-TEC-02 — P0.** The .NET gateway must own authoritative case state and approvals; workers and models must not directly mutate clinical tables or public alerts.

**SA-TEC-03 — P0.** Domain writes must be authorized, version-checked and idempotent. Event/projection/receipt/outbox persistence must be atomic.

**SA-TEC-04 — P0.** Datasync must synchronize authorized projections rather than expose direct mutable clinical history. SwiftAid must not introduce a competing generic replication engine.

**SA-TEC-05 — P0.** Mobile must not connect directly to JetStream. Broker unavailability must delay background work, not invalidate an otherwise accepted case command.

**SA-TEC-06 — P0.** Worker job completion must be durable and idempotent before acknowledgement, and stale/duplicate results must not change accepted facts.

**SA-TEC-07 — P0.** Schedules and job results must remain durable in the authoritative application store rather than depend exclusively on in-memory timers or queue retention.

**SA-TEC-08 — P0.** Require real Android Release, Datasync, provider, original-audio and upload capability verification before advertising the corresponding behavior. Use documented fallbacks, not fictitious integrations.

**SA-TEC-09 — P0.** Preserve a single .NET AI control plane and one Python worker service for the demonstration. All model inference and transcription use Meta models exclusively as specified in SA-META-01–08. Do not duplicate provider credentials/orchestration stacks without a recorded architectural decision.

**SA-TEC-10 — P0.** Operations must support safe retry, inference/channel pause, capability disablement, source withdrawal, access revocation and backup recovery without exposing private records to general operators.

### 23.2 Capability availability

The user-facing result of a capability probe is not just a developer log. Unsupported speech languages show a fallback; disabled auto-messaging shows manual sharing; no approved local protocol shows unconfigured policy; no verified contact shows no contact. A blocked core Android/Datasync path is a release blocker until the accepted fallback is demonstrated.

### 23.3 Professional rollout gate

Before real human/animal case use, the product requires institutional authorization, clinical/veterinary review, validated language claims, source/reuse rights, consent/processing policy, country-specific legal review, threat assessment, tested encryption and retention, appropriate device controls, backup/restore evidence and incident response. This specification does not certify any of them as complete.

**Basis:** [I §§3–11, 18.3, 21, 23].

## 24. Quality, accessibility, and service behavior

### 24.1 Hard engineering gates inherited from the plan

| ID | Priority | Requirement |
|---|---|---|
| SA-QLT-01 | P0 | A saved offline report and original recording must survive app/context/process restart without corruption. |
| SA-QLT-02 | P0 | Repeated commands and worker deliveries must produce one accepted business effect/result in the defined retry tests. |
| SA-QLT-03 | P0 | Original-audio upload/download must preserve the original content hash. |
| SA-QLT-04 | P0 | The defined authorization test matrix must show no unauthorized record/audio/public disclosure; report test scope and residual risks. |
| SA-QLT-05 | P0 | Case history replay and deterministic synthetic detection must be reproducible from fixed evidence/configuration. |
| SA-QLT-06 | P0 | Every displayed current AI result must identify its input/source versions and current/stale state. |
| SA-QLT-07 | P0 | Users must be able to complete core case operations without AI; provider/broker failure must be visible and recoverable. |
| SA-QLT-08 | P0 | The release build used for presentation must install and run on the recorded Android device/emulator and supported web test environment. |
| SA-QLT-09 | P0 | Release artifacts and restore instructions must allow repeatable installation, seed reset and recovery without developer-machine secrets. |
| SA-QLT-10 | P0 | Interface controls must have meaningful labels and usable non-colour status cues; microphone denial and text alternatives must work. |

### 24.2 Proposed usability/performance measurement targets — PC-05

These targets add an evaluation method where the original plan requested measurement but did not set universal latency or device specifications. They are **proposed demonstration targets**, not measured results, statutory requirements or promises on all hardware.

| Target | Proposed measurement | Constraint |
|---|---|---|
| Local save feedback | Measure local save and visible acknowledgement on the chosen demo device; target p95 ≤ 1 second across 20 synthetic draft saves | Cloud calls excluded; verify actual durability before showing saved |
| Cached case navigation | Target p95 ≤ 2 seconds across 20 opens with the seeded local data | Publish device/build/cache state; measure, do not assume |
| Original playback initiation | Target ≤ 2 seconds for an already-cached original | Download time is separately displayed and measured |
| Input reduction | No re-entry of unchanged subject/species context during routine follow-up; at most three AI questions displayed at once | Never omit a necessary confirmation merely to achieve a lower tap count |
| Field visibility | Usable with system text enlargement on the chosen Android device and keyboard navigation in the officer web flow | Test observed behavior; do not claim formal accessibility certification |
| Failure clarity | Every tested outage/denial yields a visible actionable state, no indefinite success spinner | A clear unavailable state is better than invented output |

If the timing targets are missed, report the actual measurements and narrow presentation claims; do not compromise persistence or authorization to satisfy a stopwatch. Precise bandwidth, memory, battery, supported OS/device list, scale and availability objectives must be recorded after the compatibility spike. No national-scale capacity or production uptime SLA is claimed.

### 24.3 Graceful degradation matrix

| Failure | Must continue | Must be visibly unavailable/pending |
|---|---|---|
| Internet absent | Authorized local drafts, cached case reads, original cached audio, downloaded education | Server acceptance, new data, uncached media, cloud AI |
| AI/ASR unavailable | Reporting, assignment, assistance, follow-up, manual escalation, original playback | AI proposals/transcript capability |
| JetStream/worker down | Accepted command history and projections, ordinary API case work | Queued background analysis/reminders |
| Object upload failed | Accepted case/comment metadata and local original | Remote original playback until upload completes |
| Knowledge source withdrawn | Case operations and historical audit reference | Current suggestion from the withdrawn version after refresh |
| Social platform unavailable | Session scheduling, ICS, copy/manual sharing | Verified automated send/delivery |
| Authorization expired/revoked | Protected retention of pending work for reconciliation | Private cached access after lock; unauthorized server reads/writes |
| Database unavailable | Already authorized local capture/read as permitted | New authoritative server acceptance; no false success receipt |

**Basis:** [I §§8–10, 14, 18, 21; PC-05].

## 25. Demonstration data and evaluation

### 25.1 Fixed operational dataset

Preserve the plan's reproducible seed `20261025` and **300 unique synthetic cases**:

- 180 human cases: 20 for each of the nine requested profiles.
- 108 animal cases: 12 for each of the nine starter profiles.
- 12 unclassified cases: six human and six animal.

Use separate synthetic Zimbabwe and Eswatini workspaces, fictitious facilities/people/farms, and clearly test/synthetic coordinates. Do not place a fabricated outbreak on a real named village. Duplicate-replay fixtures reuse IDs and do not increase the unique count.

The scenario labels are `routine`, `human_cluster`, `animal_cluster`, `possible_overlap`, `overlap_negative_control`, `late_upload_batch`, `duplicate_replay`, `contradictory_correction`, `restricted_case`, and `missing_evidence`.

### 25.2 AI and audio fixtures

Use 30 development narratives and 30 frozen held-out narratives with independently specified expected fields. Prompts must not be tuned against the held-out answers. Provide 12 short scripts for consenting demonstration speakers; script metadata is not a real recording. If synthetic TTS is used, identify it as synthetic audio, not the original voice of a health worker.

Test spoken languages separately. A good English result does not establish Shona or siSwati support. Report sample count, field errors, missed/added facts, negation, date/count issues, citation quality, latency, usage and failed conditions.

### 25.2a Administrative continuity evaluation set

Keep the 300 case seed and 30-development/30-held-out extraction narratives. Add 12 fixed operational scenarios referencing the existing case IDs: `overdue_task`, `unreceived_assistance`, `unacknowledged_assignment`, `no_action_needed`, `conflicting_receipt`, `missing_recipient`, `permission_revoked`, `stale_approval`, `duplicate_execution`, `partial_execution`, `broker_down`, `clinical_request_injection`. Expected evidence/actions/forbidden effects are frozen before tuning. Do not increase case counts to simulate more success.

Record grounded-plan validity, approvals respected, actual effect/receipt verification, unsupported completion claims, stale/duplicate rejection, no-action accuracy, latency and actual/estimated cost separately. Compare a scoped action-list or one-shot-summary baseline on the same cases; a better result must be measured, not asserted. Disease detection accuracy is not the core evaluation or an advertised outcome.

### 25.3 Data requirements

**SA-EVL-01 — P0.** Dataset generation must be repeatable and importing/resetting must not create accidental duplicates.

**SA-EVL-02 — P0.** Fixtures must exercise every requested operational profile, not falsely imply every profile has a validated detector.

**SA-EVL-03 — P0.** Evaluation must include negative controls, delayed uploads, contradictory corrections, missing evidence and restricted access.

**SA-EVL-04 — P0.** Published results must identify synthetic/text/audio provenance, configuration, sample sizes and limits. They are engineering evidence, not clinical/epidemiological performance certification.

**SA-EVL-05 — P0.** Demonstrate at least one real permitted Meta round-trip; simulations and canned UI content must be labelled and cannot satisfy that gate.

**SA-EVL-06 — P0.** Reset scripts must be restricted to explicitly allowed demo environments; they must never silently clear a real deployment.

### 25.4 Main presentation journey and three-minute cut

Full acceptance evidence still proves both complete human/animal operational journeys and closure/reopening. The required **three-minute video** highlights the newly built differentiator rather than attempting every screen. [C01 §7]

| Video time | Demonstration | Evidence / boundary |
|---|---|---|
| 00:00–00:12 | A reported concern can be lost between reporting and action | State SwiftAid's operational purpose; no clinical outcome claim |
| 00:12–00:40 | Capture a synthetic human concern with original voice in airplane mode; restart and play | Honest locally saved/queued state; original audio |
| 00:40–01:00 | Reconnect and show a separate animal report plus scoped officer workspace | Accepted receipts, distinct case/group units and original playback |
| 01:00–01:25 | Meta extracts reported facts and flags an explicit date contradiction | Worker confirmation; no diagnosis/medical risk inference |
| 01:25–02:10 | Agent finds recorded administrative gaps, plans actions, obtains exact approval, executes and verifies receipts | Real bounded plan–approve–execute–verify loop, not a slide or only a summary |
| 02:10–02:30 | Replay or invalidate an approval; show no duplicate action or stale write | Failure evidence; no claim that assistance was received merely because a reminder exists |
| 02:30–02:47 | Schedule a source-linked education session and prepare generic manual sharing | Static health material, no AI medical teaching; no false delivery receipt |
| 02:47–03:00 | Show measured evidence, actual model route, new-work contribution and limitations | Synthetic/recorded labels, public repo/video; no unapproved bonus claim |

These segments total 180 seconds. Keep audio readable, captions and accessible playback; the source gives no extra duration tolerance. Optional verified open-weight evidence may replace part of the final segment, not extend the video or become a decorative unused fallback. Optional health-pattern interpretation is absent while the organizer gate is closed.

A live failure is not replaced with a recording claimed to be live. Show the exact release build/commit; the fuller runbook provides the other required foundation-feature evidence.

## 26. End-to-end acceptance scenarios

AT-43, AT-44 and AT-47 are conditional event-submission/promotion scenarios, not core product coding or release prerequisites. Record them NotApplicable when that profile is not selected; do not fake a pass. AT-45 remains optional verified open-weight evidence.

These scenarios complement, not replace, the implementation plan's named unit/integration/device tests. “Then” outcomes must be evidenced on the actual relevant service/device path. All data are synthetic.

| Scenario | Given / when | Required outcome | Principal requirements |
|---|---|---|---|
| AT-01 — Provisioned offline capture | A verified worker has a current offline grant; save a human report in airplane mode and restart | Draft/queued data and unknowns survive; no server-accepted claim | SA-REP-01, SA-OFF-01, SA-OFF-02 |
| AT-02 — First-run guest / staff separation | A new Android install has no verified staff identity | Guest drafts/audio work offline; staff workspace/private reads remain denied until authorized bootstrap | SA-ACL-06, SA-OFF-04, SA-PUB-13 |
| AT-03 — Animal group reporting | Submit one flock report with known and unknown counts | One animal report is created; animals and report count remain separate | SA-REP-07, SA-DAT-06 |
| AT-04 — No diagnosis required | Report acute watery diarrhoea without a known cause | The concern is accepted as unclassified/syndromic; non-cholera is not inferred | SA-REP-02, SA-CAT-02 |
| AT-05 — Multi-person narrative | A narration mentions several people | No silent creation of several patient records; worker confirmation requested | SA-REP-08 |
| AT-06 — Commit then timeout | Server accepts a command; client loses response and retries | Exactly one accepted effect and stable receipt; no duplicate case/event | SA-OFF-05, SA-TEC-03 |
| AT-07 — Concurrent correction | Two actors act on the same accepted version | One succeeds; the other retains a conflict proposal; no clinical overwrite | SA-LIF-04, SA-OFF-07 |
| AT-08 — Immutable facts | Correct an onset date with reason | Old revision retained, new revision current, actor/reason visible | SA-LIF-03, SA-HIS-02, SA-HIS-03 |
| AT-09 — Scoped access | Worker/officer changes IDs/filters to another scope | No private record, count, audio or prompt leakage | SA-ACL-01, SA-ACL-05, SA-SEC-01 |
| AT-10 — Sensitive human case | Ordinary regional officer requests restricted HIV content | Denied without additional clearance; no condition leak in public/notification paths | SA-ACL-03, SA-SEC-09 |
| AT-11 — Reassignment and old record | Officer reassigns a case older than the new worker's checkpoint | New worker receives it; prior worker loses server access; timeline preserved | SA-ASG-03, SA-ASG-05, SA-ASG-06 |
| AT-12 — Offline grant expires | Device remains disconnected past configured grant | Cached private view locks; protected pending work is not silently destroyed | SA-OFF-09, SA-OFF-10 |
| AT-13 — Original voice survives | Record, restart, upload, download and play | Original bytes/hash retained; no synthesized substitution | SA-COM-04, SA-QLT-03 |
| AT-14 — ASR fails | Transcription is unavailable/unsupported | Audio remains playable; typed reporting works; transcript unavailability shown | SA-COM-10, SA-AI-12 |
| AT-15 — Audio permission revoked | Assignment/access is revoked during upload or playback | Further server transfer denied; cached access reconciled/locked according to grant | SA-COM-09, SA-ASG-06 |
| AT-16 — Transcript correction | Worker corrects machine text | Both transcript revisions remain; accepted facts change only through a separate command | SA-COM-11, SA-AI-02 |
| AT-17 — Assistance gap | Request is accepted and marked Provided | It is not ReceiptConfirmed; outstanding confirmation remains visible | SA-AST-02, SA-AST-08 |
| AT-18 — Missed follow-up | Worker cannot reach the subject and reschedules | Missed/attempt outcome and old due date retained; no claim care completed | SA-FUP-03, SA-FUP-04, SA-FUP-05 |
| AT-19 — Escalation without AI | Model service is down; worker raises concern | Accepted/queued state is truthful; officer can acknowledge through normal operations | SA-MON-06, SA-ESC-02, SA-ESC-06 |
| AT-20 — Closure with open work | Officer attempts resolution with unresolved actions | Explicit disposition required; no implicit recovery or fulfilment | SA-RES-01, SA-RES-02, SA-RES-04 |
| AT-21 — Reopening | Authorized officer reopens a resolved case with reason | Earlier closure remains, ownership and next work are visible | SA-LIF-06, SA-RES-03 |
| AT-22 — Hostile narrative | A report asks AI to reveal other cases or execute arbitrary tools | Text remains data; scope/tool limits enforced; no leakage | SA-AI-06, SA-AI-07, SA-AI-08 |
| AT-23 — Stale AI proposal | Case facts change while a model job runs | Result is marked stale/noncurrent; it cannot overwrite new facts | SA-AI-01, SA-AI-03 |
| AT-24 — Missing reference/evidence | No permitted source/card or recorded fact supports the request | Static reference lookup reports unavailable; factual AI abstains; no medical-advice generation | SA-AI-10, SA-AI-11, SA-KNW-12 |
| AT-25 — Withdrawn education | Cached version is withdrawn and device later refreshes | Card is no longer current; audit reference and last-update limits remain clear | SA-KNW-09, SA-KNW-10 |
| AT-26 — Delayed batch / gated inference | Gate closed; historical onsets arrive together | No health-inference job or clinical signal. If separately cleared, original onset tests—not upload time—govern optional inference | SA-DAT-04, SA-SUR-10, SA-CMP-09 |
| AT-27 — Human/animal relationship gate | Cross-sector inference is not cleared | No inferred-transmission or health-pattern endpoint/UI/job; operational counts retain distinct scopes/units. Positive relationship tests remain conditional | SA-SUR-04, SA-SUR-05, SA-SUR-08, SA-CMP-09 |
| AT-28 — Optional signal corrections / gate | Product approval/validation absent, or an approved optional scenario is corrected; event mode additionally checks its organizer record | Gate-off tests pass independently; positive algorithm/evaluation outcomes are Blocked/Deferred unless cleared, then supersession/privacy tests apply | SA-SUR-07, SA-SUR-10, SA-CMP-09 |
| AT-29 — Session without social API | No platform API is provisioned; officer schedules public session | Native schedule, ICS and copy/manual share work; no false automation claim | SA-SES-01, SA-SES-04, SA-SES-05 |
| AT-30 — Manual share vs delivery | Officer opens a share sheet | Not marked Delivered; no attendance or case-care outcome inferred | SA-SES-10, SA-NOT-03 |
| AT-31 — Cancelled reminder | Session/task is cancelled before dispatch | Pending send suppressed; replay does not create a duplicate accepted occurrence | SA-SES-09, SA-SES-13, SA-NOT-04 |
| AT-32 — Public privacy | Public viewer loads education/session endpoints and attempts private URLs | Approved public DTOs only; no case/audio/attendee leakage | SA-ACL-04, SA-SEC-02, SA-SES-07 |
| AT-33 — Queue retry/restart | Kill publisher/worker at defined commit/ACK boundaries | Accepted state survives; duplicate work is harmless; failure is visible/replayable | SA-TEC-05, SA-TEC-06, SA-QLT-02 |
| AT-34 — Budget exhausted | Configured inference cap is reached | Inference disabled visibly; original voice and case work continue | SA-AI-12, SA-QLT-07 |
| AT-35 — Seed coverage | Generate/import the fixed dataset twice | Exactly 300 unique cases with correct profile counts and no real identities | SA-CAT-01, SA-EVL-01, SA-EVL-02 |
| AT-36 — Release rehearsal | Install final release and repeat the complete product runbook | Human/animal workflows, real permitted Meta use, approved administrative effects, original audio, guest intake, identity/memory/subscriptions and private/public boundary reproduced; repository publication and video checks apply only to event submission | SA-QLT-08, SA-QLT-09, SA-EVL-05, SA-CMP-04, SA-CMP-07 |
| AT-37 — Advice refused even with approval | User requests diagnosis, treatment, clinical priority or a generated medical lesson; includes approval/source references | Retired/medical routes reject before inference; no health advice output; ordinary recordkeeping remains available | SA-CMP-02, SA-CMP-03, SA-OPS-12 |
| AT-38 — Unapproved plan | Agent drafts three administrative actions and tries to execute | No authoritative effect until an authorized exact plan approval exists | SA-OPS-02, SA-OPS-03, SA-OPS-05 |
| AT-39 — Stale or expired approval | Input/plan hash/scope changes or approval expires | Block affected actions and request new review; no silent privilege/version rebase | SA-OPS-03, SA-OPS-04, SA-OPS-10 |
| AT-40 — Duplicate action and false completion | Replay an approved plan/action or inspect a queued reminder | One effect/receipt; verification does not treat queued as delivered or Provided as received | SA-OPS-05, SA-OPS-06 |
| AT-41 — Partial run | First action commits and the second fails | Preserve accepted effect and receipt; mark PartiallyApplied/NeedsAttention; no erased history or false whole-plan success | SA-OPS-07, SA-OPS-09 |
| AT-42 — No justified action | Evidence is complete or lacks eligible recipient/required facts | Explicit no-action/needs-review; do not fabricate clinical or administrative work | SA-OPS-01, SA-OPS-08, SA-OPS-10 |
| AT-43 — Public/new-work/rights audit | Inspect public clone, video, source history and asset register without privileged access | Judge-accessible code/video; new-event feature map and honest earlier work; no secrets or unlicensed private assets | SA-CMP-04, SA-CMP-05, SA-CMP-08 |
| AT-44 — Event timeline and video | Review provenance and final submission checklist | Source-stated build period retained; exact start ambiguity documented; 180-second video and deadline/receipt verified, not assumed | SA-CMP-06, SA-CMP-07 |
| AT-45 — Optional open-weight proof | Bonus lane enabled and claims are proposed | Actual eligible Muse Glimmer/Muse Spark Open execution/provenance/verification path, separate budget and output evidence; configured-only route fails | SA-CMP-11, SA-META-03 |
| AT-46 — No medical-generation alias | Call old education/triage routes, a new alias or prompt injection in a report | Capability/action allowlists prevent equivalent advice behavior; source/human-review labels do not bypass exclusion | SA-CMP-02, SA-CMP-03, SA-AI-07, SA-AI-08 |
| AT-47 — Optional promotion truthfulness | Prepare viral/community submission artifacts | No automatic posting/voting or private case content; manual/authorized links and correct source cutoff recorded; no prize guarantee | SA-CMP-13, SA-SES-07 |
| AT-48 — Meaningful agentic effect | Run fixed administrative scenarios against real Meta and gateway services | Evidence retrieval, bounded plan, approval, actual typed effects and verification traced; measured baseline/results separated from projected benefits | SA-CMP-12, SA-OPS-09, SA-OPS-11 |

### 26.0a Product-access, identity and memory acceptance additions

| Scenario | Given / when | Required outcome | Principal requirements |
|---|---|---|---|
| AT-49 — Guest human report | A visitor has no account | Submit a human report without login; get private Unverified receipt; protected case API denied | SA-PUB-01, SA-PUB-02, SA-PUB-03 |
| AT-50 — Global animal report | An animal event is outside ZW/SZ with unknown area | Global country accepted; one animal report, explicit unknown hierarchy, no false service coverage | SA-GEO-01, SA-GEO-03, SA-PUB-04 |
| AT-51 — Geography and time precision | GPS denied and occurrence date differs from capture/upload | Manual location works, offset/date precision preserved; no inferred onset or routing authority | SA-GEO-02, SA-GEO-04, SA-GEO-05 |
| AT-52 — Guest timeout retry | Server commits intake then connection fails | Same ID/proof returns same receipt; no extra case, media reservation or AI job | SA-PUB-08 |
| AT-53 — Reviewer promotion race | Two permitted reviewers accept the same intake | One promotion creates one managed case/initial owner; original guest evidence unchanged | SA-PUB-05, SA-PUB-06 |
| AT-54 — Uncovered intake | No organization is authorized for that location | Report remains received with NeedsRouting/NoServiceCoverage; no broad broadcast or responder promise | SA-PUB-04, SA-GEO-07, SA-GEO-08 |
| AT-55 — Receipt enumeration | Attacker guesses receipt or presents another proof | No content/status leak; receipt proof never accesses staff dossier or another original | SA-PUB-07, SA-PUB-16 |
| AT-56 — Guest upload and abuse limits | No-account audio, spoofed binding or excess submissions | Valid original hash survives; forged binding denied; rate-limited/quarantined input triggers no unlimited inference | SA-PUB-09, SA-PUB-10, SA-PUB-11 |
| AT-57 — Fresh-install offline guest | Native app starts offline before any sign-in | Guest text/audio survive restart; staff cached data unavailable; online submit later works | SA-PUB-13 |
| AT-58 — Guest to signed-in account | Reporter logs in after guest capture | Draft remains isolated; explicit receipt proof links tracking only; no silent staff authorship | SA-PUB-14, SA-PUB-15 |
| AT-59 — Actual social sign-in | Configured Google and Facebook accounts | Each callback yields ordinary SwiftAid member; absent email safe; no organization/posting rights | SA-IDN-01, SA-IDN-02, SA-IDN-03, SA-IDN-13 |
| AT-60 — Organizational SSO isolation | Configured OIDC tenant and forged different issuer/claim | Valid member enters permitted workspace; unapproved discovery/tenant/domain promotion rejected | SA-IDN-07, SA-IDN-08, SA-IDN-09, SA-IDN-10 |
| AT-61 — Provider linking attack | Two external subjects share an email | No merge without reauthentication and proof; provider token cannot call SwiftAid as staff | SA-IDN-02, SA-IDN-04, SA-IDN-06 |
| AT-62 — Native and web flow security | Replayed callback, wrong verifier/nonce, logout/cancel | Maintained flow rejects replay/mismatch; external browser; secrets off device; guest work preserved | SA-IDN-05, SA-IDN-06, SA-IDN-12, SA-IDN-14 |
| AT-63 — Revoked membership | Valid login survives an organization revocation | Protected APIs, cached answer and memory rejected on fresh authority checks; no refresh privilege restoration | SA-IDN-11, SA-MEM-04, SA-MEM-07 |
| AT-64 — Event subscription login boundary | Guest browses an event then chooses Subscribe | Public page/ICS works; login required to subscribe; member needs no organization role | SA-SUB-01, SA-SUB-02, SA-SUB-06 |
| AT-65 — Subscription cancellation race | Queued reminder exists before unsubscribe/cancel | One versioned subscription; no future send after current checks; subscriber list private | SA-SUB-03, SA-SUB-04, SA-SUB-05, SA-SUB-07 |
| AT-66 — Qdrant scope isolation | Same text/IDs across accounts and organizations | No cross-scope recall/cache; empty scope denies; client gets no Qdrant authority | SA-MEM-01, SA-MEM-02, SA-MEM-04, SA-MEM-16 |
| AT-67 — Memory poisoning | Guest/source text instructs a policy or memory write | Content stays untrusted evidence; no approved organization memory, credentials or raw audio payload | SA-MEM-03, SA-MEM-05, SA-MEM-06 |
| AT-68 — Exact cache freshness | Repeat request then change facts/access/source/model/negation | Valid exact hit preserves provenance; incompatible context misses; no cached approval/action execution | SA-MEM-09, SA-MEM-10, SA-MEM-11 |
| AT-69 — Expired memory delayed cleanup | Expired or revoked vector remains physically stored | Gateway denies use immediately by current registry/expiry despite delayed deletion | SA-MEM-07, SA-MEM-08 |
| AT-70 — Embedding unavailable | No verified Meta encoder or non-Meta default attempted | Exact Qdrant lookup still works; no fake vectors/semantic claim and non-Meta encoder rejected | SA-MEM-13, SA-MEM-14 |
| AT-71 — Qdrant outage and guest isolation | Qdrant down or unrelated guest asks similar question | Core report works; budgeted current-context fallback or explicit unavailable; no cross-report personal memory | SA-MEM-12, SA-MEM-15 |
| AT-72 — Erasure versus replay/restore | Source deleted then old indexing job/backup is replayed | No resurrection of usable derived memory; registry invalidation governs restore/reindex | SA-MEM-07, SA-MEM-08 |
| AT-73 — Multi-turn factual clarification | Case intake has unknown event details; agent asks; worker answers later | Separate authorized model turns, original evidence retained, no guessed diagnosis, confirmed factual update only by normal command | SA-LOOP-01–SA-LOOP-06, SA-LOOP-13 |
| AT-74 — Interruption, restart and stale context | Worker disconnects, worker process restarts, then an external case correction arrives | Persisted checkpoint resumes without duplicate effect; stale input invalidates old proposal; changed scope or consent denies next step | SA-LOOP-05, SA-LOOP-08, SA-LOOP-11 |
| AT-75 — Approval/receipt and loop budget | Agent plans three operational actions; one fails; user requests another iteration | No protected action without valid approval, partial receipt truthful, bounded cycles/tool calls and credits, no repeated accepted effects | SA-LOOP-07, SA-LOOP-09, SA-LOOP-12 |
| AT-76 — Memory and tenant isolation across turns | Two organizations share similar narratives; Qdrant is unavailable or returns stale scope | No cross-scope evidence; fallback/current evidence or unavailable; audit shows factual provenance without chain-of-thought or sensitive payloads | SA-LOOP-04, SA-LOOP-10, SA-LOOP-14, SA-LOOP-15 |

### 26.1 Acceptance evidence record

AT-26–AT-28 contain mandatory OFF-gate checks and conditional positive-inference branches. AT-45 and AT-47 are optional bonus/promotion checks. A disabled optional feature is not counted as a passing implemented feature; record `Blocked` or `Deferred` for its positive branch. All other mandatory integrity and operational requirements remain release gates.

For each scenario, record build/commit, environment/device, identities/scopes, fixture versions, steps, actual observations, relevant test commands/results, redacted evidence and verdict. Use `NotRun`, `Passed`, `Failed`, or `Blocked`; not “probably works.” A conditional capability may be disabled with a documented fallback, but a disabled capability is not a passing implementation of that feature.

Core privacy, original-data integrity, immutable correction, honest sync and authoritative-human-control failures block the demonstrated path. Visual polish, a working screenshot or passing unit tests alone cannot satisfy a failing device/integration scenario.

**Basis:** [I §§18, 19, 23.2].

## 27. Milestones, release criteria, and scope control

**Product sequence:** execute the dependency graph, not historical event dates. New required work is P1.3 (federation/SSO), P2.4 (global guest intake), P3.4 (guest offline/receipt continuity), P7.4 (Qdrant index), P8.5 (safe memory/cache integration), and P10.3 (member subscriptions). These are part of this product release; semantic matching, SAML bridging and social auto-publishing remain separately gated. The dated event sequence below is conditional submission planning only.

### 27.1 Source-defined event schedule and executable release path

The supplied guide says **kickoff October 26, 12:00 PM ET**, **building period October 27–November 4**, and **submission November 4, 9:00 PM ET** (its submission section uses “EST”). Treat the explicit building period as the conservative constraint; kickoff is not assumed to authorize an earlier code start. The exact opening time and treatment of pre-event plans/boilerplate are not specified in the excerpt. Preserve earlier planning as prior work and ask organizers; do not backdate or present it as event output. [C01 §§3–4, 7]

November 4, 9:00 PM Eastern corresponds to **November 5, 2026, 04:00 in Eswatini and Zimbabwe** using the named local zones. The nine dates below replace the earlier generic ten-day assumption. This is a proposed work allocation, not a source guarantee of feasibility.

| Build date | Priority tasks | Observable release increment |
|---|---|---|
| October 27 | P0 gates; P1 contracts/identity | Actual Android release storage/sync probe, permitted Meta text call, rules/provenance ledger and locked contracts |
| October 28 | P2 authoritative backend; P3 offline command/read path; P7.1 fixture base | Human/animal reports persist offline, reconcile once, and respect assignment/scope |
| October 29 | P4 original voice; P5 support lifecycle | Original playback/transfer, assignment, assistance, follow-up/escalation and immutable outcomes |
| October 30 | P6 workers; P8.1/P8.2 permitted intake; P7 static reference preparation | Reliable jobs and real attributed Meta extraction; ASR only where verified |
| October 31 | P8.3 planning/approval/execution | One complete administrative continuity run with real approved receipts |
| November 1 | P8.4 agent/AI evaluation; failure paths; P11 integration | Stale/no-action/partial/duplicate controls and measured nonclinical results; optional open-weight lane only if already healthy |
| November 2 | P7 reference library; P10 sessions; P11 core UI completion | Human-led education, calendar/manual sharing and officer/public boundary |
| November 3 | P12 privacy/failure/packaging; video rehearsal | Installable repeatable release, public-source/rights review and 180-second recorded rehearsal; last full troubleshooting day in guide |
| November 4 | P12 final evidence and submission | Final signed build/commit, accessible public repo/video and submission receipt before 9:00 PM Eastern |

Phase 9 positive health-pattern inference is optional and gated; do not spend the core window on it without the recorded clarification. Dependencies still govern parallel work. No mandatory privacy/integrity check is postponed until the final day.

### 27.2 P0 release acceptance

**SA-REL-01 — P0.** The release must demonstrate both human/animal operational journeys, original voice, authorized sync, assistance distinctions, follow-up/escalation, immutable corrections and closure/reopening, plus a verified administrative-agent run and controlled public education. Optional health-pattern inference is not required.

**SA-REL-02 — P0.** Real constrained Meta inference must be evidenced. Unverified ASR, translation, detector or channel functionality must be disabled or explicitly limited.

**SA-REL-03 — P0.** Release evidence must include dependency/capability checks, Meta-only execution and usage, actual approved-plan receipts, public-repository/video access, event contribution history, source/model/voice/asset rights, evaluation, restore/reset instructions and known limitations.

**SA-REL-04 — P0.** The entry must not imply organizer approval, clinical deployment/certification, medical advice, independent review, local prevalence, completed care or delivered messages without evidence. The guide and bonus slide are source requirements, not a permission grant for this project.

**SA-REL-05 — P0.** A blocker may narrow or disable an optional capability, but cannot be hidden by a mock success or a changed test assertion.

### 27.3 Cut order

Keep unclarified health-pattern inference disabled and drop optional open-weight deployment first if it threatens the core. Then follow the implementation cut order: remove unprovisioned social automation first; then extra maps/charts/channels/optional playback controls; then unproven local-language ASR/translation; then extra AI depth beyond real extraction, continuity summary and an approved administrative plan with verified effects; then richer analytics and exhaustive reference collection. Never re-enable advice generation as a shortcut.

Never cut server authorization, immutable original-history integrity, original-audio preservation, honest offline status, idempotency, human approval, synthetic labels or evidence of no cross-scope disclosure. A smaller truthful product is acceptable; a larger misleading demonstration is not.

### 27.4 Post-demonstration roadmap

P1 deepens usability, approved language packs, validated recorded-language behavior, additional reviewable sources, verified social adapters, richer operational analytics, accessibility evaluation and upload optimizations.

P2 requires institutional partnerships, approved professional procedures, real country data governance, production identity/device/encryption/retention controls, independently evaluated analytics, real referral directories, managed incident response and wider platform releases. Any different future AI-health product scope needs a separate decision; it is not silently activated in this competition build. SMS/USSD, national-system integrations, richer inventory/resource coordination, cross-border exchange and forecasting remain separate scoped decisions.

**Basis:** [I §§18.3, 20, 23].

## 28. Decisions, proposed clarifications, and unresolved gates

### 28.1 Confirmed product/architecture decisions

The plan and conversation establish the fresh SwiftAid implementation, human-and-animal support, Android-first mobile application, .NET application authority, Python workers, Microsoft AI/MCP tooling, gateway-side NATS JetStream, Datasync, immutable/versioned case records, original voice as retained evidence, restricted worker/officer/public access, and officer-led education with Facebook/WhatsApp options.

Qdrant, public intake, social authentication and organizational SSO are now active product decisions; the former prohibition on public intake and deferred SSO are superseded. The earlier PowerSync/Dexie/PWA alternatives and ForgeAI/FieldMind directions are not active requirements. The original SwiftAid code is a domain reference, not authorization to replace the fresh-implementation requirement. No special multi-agent development framework is required.

### 28.2 Explicit clarifications proposed in this specification

| ID | Clarification | Why it is needed | Approval boundary / implementation effect |
|---|---|---|---|
| PC-01 | Conservative action permissions: workers record assigned work; officers control reassignment, closure/reopen and publication; official classification needs explicit professional/action permission | The source plan defines scopes but not every persona/action combination | Default for demonstration tests; institution may narrow; broadening protected actions needs owner/policy approval |
| PC-02 | Minimal intake contract with explicit unknowns and at least an observation/narrative/original-voice reference | Avoid a form that requires an invented diagnosis or complete demographic data | Product intake default only; not a national reporting requirement |
| PC-03 | Group multi-event commands in the visible timeline while retaining inspectable individual events | One user action may produce more than one authoritative event | Presentation choice; no schema/history loss |
| PC-04 | Six core mobile and six core officer modules with supporting panels/views | Reconcile visual concepts with the plan's limited scope | Navigation organization, not added mandatory services or a UI framework switch |
| PC-05 | Operational metric definitions and proposed device usability/performance measurement targets | The plan asks for measurement without universal SLO values | Evaluation targets only; not clinical outcome measures or achieved performance claims |
| PC-06 | Worker-documented assistance receipt with explicit confirmation provenance | Avoid needing a new patient/farmer portal for P0 | Must not claim a recipient digital signature or independently verified delivery |
| PC-07 | Resolved cases default to history; new operational work requires reopening, while permitted factual corrections may be appended separately | Prevent hidden support activity from bypassing resolution history | Proposed transition policy; resolve with owner before broadening |
| PC-08 | Demonstration publisher may be the same appropriately authorized officer who drafts, but review/approval is explicit | No independently staffed review team has been established | Never present same-person review as independent clinical approval; production duty separation remains open |

No new statistical, medical, veterinary or legal threshold is introduced. The operational plan's proposed 15-minute approval expiry (ED-OPS-01) is an engineering safeguard, not a clinical or organizer rule. The demo limits and detection parameters reproduced here come from the implementation plan and remain explicitly nonclinical.

### 28.2a Decisions introduced by this rules-aligned revision

| Record | Decision | Status / boundary |
|---|---|---|
| ED-RULES-01 | Administrative continuity is the default AI demonstration; factual extraction/audit supports it | Recommended revision strategy based on the last discussion; not a promise of organizer approval or prize outcome |
| ED-RULES-02 | AI medical/veterinary advice, clinical triage and generated health education are excluded even with approval/citations | Conservative product scope applying the supplied anti-project examples |
| ED-RULES-03 | Cross-sector health-pattern inference defaults OFF until specifically clarified | No reply is not approval; core entry has no dependency on this feature |
| ED-OPS-01 | At most three administrative plan actions; proposed approval lifetime 15 minutes; due times confirmed by officer | Engineering demo defaults, configurable without broadening authority; task/schema tests enforce them |
| ED-BONUS-01 | Agentic bonus is the primary target; open-weight proof is optional P1 with real model execution | Source conditions do not grant extra main-rubric points or guarantee any award |

### 28.3 Unresolved capability and governance gates

| Gate | What is not established by these documents | Required evidence | Safe fallback / restriction |
|---|---|---|---|
| Operational-only health-domain entry | Organizers have not approved this specific scope | Written response covering faithful intake and nonclinical coordination | Keep excluded features absent and do not claim guaranteed eligibility |
| Optional relationship inference | Product approval and privacy/source/evaluation readiness are not established; event eligibility is separate | Recorded product decision plus validation; organizer clearance only when entering an event | OFF with no inference jobs/tools/UI; core intake and case operations continue |
| Android/Datasync compatibility | Actual restore-tested library/workload/native combination | Release APK, persistence/reconnect and authorization tests | Follow the plan's tested native MAUI fallback decision; do not silently switch sync frameworks |
| Meta inference/tools | Actual account endpoint/model/response/tool contract | Redacted real-account probe and capability record | Explicit disabled/limited capability; no unrelated provider substitution |
| Transcription and language | Supported audio formats, local-language quality, credit coverage and pricing | Actual recordings/format tests and pricing/usage verification | Original audio + typed reporting; tested language only |
| Local health/veterinary protocols | Complete current applicable local rules and authority approval | Reviewed source/version and professional/institutional authorization | Unconfigured rules; demonstration-only content |
| Source reuse rights | Permission for the particular excerpt/full text/translation/distribution use | Source-specific rights record | Links/allowed material only; do not distribute unapproved documents |
| Real contacts and facilities | Current service capability, contact and referral arrangements | Verified locally maintained directory | Explicitly synthetic demo entries or “not configured” |
| Outcome vocabulary | Universal institution-approved human/animal outcome values | Reviewed versioned catalogue | Record known supported facts and unknown outcome without invented classification |
| Access/retention policy | Permitted production offline duration, retention periods and data residency | Country/institutional governance | Synthetic-only mode; no unreviewed real-data rollout |
| Public aggregates | Approved suppression and re-identification controls | Reviewed aggregation policy and tests | Publish public education/sessions, not raw statistics |
| Social automation | Account approval, permissions, provider capabilities and permissible message flow | Provisioned sandbox/real-account tests and documented authority | Native schedule, ICS and manual sharing |
| Clinical performance | Diagnostic/epidemiological validity | Suitable independent/domain evaluation and authorization | Engineering demonstration only |
| Scale/device support | Broad device matrix, concurrent workload and production SLA | Recorded load/device/failure testing | Publish the actual tested environment and limitations |

### 28.4 Main risks

The largest delivery risks are scope breadth, native mobile/sync compatibility, original-media reliability, unsafe authority assumptions, stale/conflicting AI evidence, source/right/language limitations and social-platform provisioning. Mitigation is a thin end-to-end journey with early capability probes and strict fallback truthfulness, not removal of privacy or audit requirements.

Any material change must state the requirement IDs affected, rationale, security/clinical/privacy impact, task dependencies, changed tests, and release consequence. Record unresolved issues in the repository rather than filling them with a guessed clinical rule.

**Basis:** [I §§5.3, 18.3, 20, 23; explicit PC clarifications above].

## 29. Traceability to the implementation plan

### 29.1 Implementation task coverage

Every existing task ID is retained. The implementation index assigns a primary owner for every requirement; shared integration tests still apply. A P1/Gate requirement does not become P0 because its task also verifies a mandatory disabled state.

| Task | Accountable product requirements | Principal acceptance evidence |
|---|---|---|
| P0.1 — Create the fresh repository and locked build baseline | SA-SCP-06, SA-TEC-01, SA-CMP-05, SA-CMP-06 | Targeted tests in task; shared scenarios per implementation §18.4 |
| P0.2 — Prove native MAUI + Datasync on Android before UI investment | SA-TEC-08 | Targeted tests in task; shared scenarios per implementation §18.4 |
| P0.3 — Verify Meta access and define capability/budget controls | SA-AI-14, SA-EVL-05, SA-META-02, SA-META-03, SA-CMP-11 | AT-45 |
| P1.1 — Define schemas, aggregate invariants, and event replay | SA-DAT-01, SA-DAT-02, SA-DAT-07, SA-DAT-08, SA-LIF-01, SA-LIF-02, SA-QLT-05 | Targeted tests in task; shared scenarios per implementation §18.4 |
| P1.2 — Implement first-party identity and resource policies | SA-ACL-01, SA-ACL-02, SA-ACL-03, SA-ACL-04, SA-ACL-05, SA-ACL-06, SA-ACL-07, SA-ACL-08, SA-SEC-01, SA-SEC-09, SA-SEC-10 | AT-02, AT-09, AT-10 |
| P2.1 — Implement atomic command processing, receipts, and persistence | SA-LIF-03, SA-HIS-02, SA-SEC-06, SA-SEC-07, SA-TEC-02, SA-TEC-03 | AT-07 |
| P2.2 — Implement human and animal report registration/correction | SA-VIS-03, SA-DAT-03, SA-DAT-04, SA-DAT-05, SA-DAT-06, SA-REP-02, SA-REP-06, SA-REP-07, SA-REP-08, SA-REP-12 | AT-03, AT-04, AT-05 |
| P2.3 — Expose authorized read-only Datasync projections | SA-TEC-04 | Targeted tests in task; shared scenarios per implementation §18.4 |
| P3.1 — Persist drafts and local-only state | SA-REP-01, SA-OFF-01, SA-OFF-13, SA-QLT-01 | AT-01 |
| P3.2 — Implement domain outbox and reconnect orchestration | SA-LIF-04, SA-REP-11, SA-OFF-02, SA-OFF-03, SA-OFF-05, SA-OFF-06, SA-OFF-07 | AT-06 |
| P3.3 — Handle scope changes, offline grants, and safe cache refresh | SA-ACL-09, SA-ASG-05, SA-ASG-06, SA-OFF-04, SA-OFF-08, SA-OFF-09, SA-OFF-10, SA-OFF-12, SA-SEC-11 | AT-11, AT-12 |
| P4.1 — Record, retain, and play original voice offline | SA-COM-01, SA-COM-02, SA-COM-04, SA-COM-05, SA-COM-06, SA-COM-07, SA-COM-12 | Targeted tests in task; shared scenarios per implementation §18.4 |
| P4.2 — Authorize and resume original-media uploads | SA-REP-13, SA-REP-14, SA-COM-08, SA-COM-09, SA-COM-13, SA-SEC-02, SA-QLT-03 | AT-13, AT-15 |
| P4.3 — Connect transcript revisions and voice-first comments | SA-COM-03, SA-COM-10, SA-COM-11, SA-META-08 | AT-14, AT-16 |
| P5.1 — Implement ownership, collaboration, and assistance delivery | SA-VIS-01, SA-ASG-01, SA-ASG-02, SA-ASG-03, SA-ASG-04, SA-ASG-07, SA-ASG-08, SA-AST-01, SA-AST-02, SA-AST-03, SA-AST-04, SA-AST-05, SA-AST-06, SA-AST-09 | AT-17 |
| P5.2 — Implement follow-ups, observations, and escalation | SA-VIS-02, SA-MON-01, SA-MON-02, SA-MON-03, SA-MON-04, SA-MON-05, SA-MON-06, SA-MON-08, SA-FUP-01, SA-FUP-02, SA-FUP-03, SA-FUP-04, SA-FUP-05, SA-FUP-06, SA-FUP-10, SA-ESC-01, SA-ESC-02, SA-ESC-03, SA-ESC-04, SA-ESC-05, SA-ESC-06, SA-ESC-07 | AT-18, AT-19 |
| P5.3 — Implement resolution, reopening, and complete timelines | SA-LIF-05, SA-LIF-06, SA-LIF-07, SA-HIS-01, SA-HIS-03, SA-HIS-06, SA-HIS-07, SA-RES-01, SA-RES-02, SA-RES-03, SA-RES-04, SA-RES-05 | AT-08, AT-20, AT-21 |
| P6.1 — Publish transactional jobs reliably | SA-SEC-04, SA-TEC-05 | Targeted tests in task; shared scenarios per implementation §18.4 |
| P6.2 — Implement the Python worker control protocol | SA-TEC-06 | AT-33 |
| P6.3 — Dispatch durable due work and reminders | SA-FUP-09, SA-SES-09, SA-NOT-01, SA-NOT-02, SA-NOT-03, SA-NOT-04, SA-NOT-05, SA-NOT-06, SA-TEC-07 | AT-31 |
| P7.1 — Seed all requested profiles and reproducible scenarios | SA-SCP-01, SA-SCP-04, SA-CAT-01, SA-CAT-02, SA-CAT-03, SA-CAT-04, SA-SEC-08, SA-EVL-01, SA-EVL-02 | AT-35 |
| P7.2 — Ingest identified official sources with provenance and rights gates | SA-KNW-01, SA-KNW-02, SA-KNW-03, SA-KNW-06, SA-KNW-07 | Targeted tests in task; shared scenarios per implementation §18.4 |
| P7.3 — Implement source-filtered retrieval and offline education packs | SA-SCP-02, SA-AST-07, SA-OFF-14, SA-KNW-04, SA-KNW-05, SA-KNW-08, SA-KNW-09, SA-KNW-10, SA-KNW-11, SA-KNW-13, SA-CAT-05, SA-CMP-03 | AT-25 |
| P8.1 — Build the .NET Meta adapter and authorized MCP tool layer | SA-VIS-04, SA-AI-06, SA-AI-07, SA-AI-08, SA-AI-13, SA-SEC-03, SA-TEC-09, SA-META-01, SA-META-04, SA-META-05, SA-META-07, SA-CMP-01, SA-CMP-02, SA-OPS-12 | AT-22, AT-37, AT-46 |
| P8.2 — Implement transcription, structured extraction, and confirmation | SA-REP-05, SA-REP-09, SA-REP-10, SA-AI-02, SA-AI-04 | Targeted tests in task; shared scenarios per implementation §18.4 |
| P8.3 — Implement factual continuity and approved administrative execution | SA-HIS-05, SA-AST-08, SA-MON-07, SA-FUP-08, SA-AI-01, SA-AI-03, SA-AI-05, SA-AI-09, SA-AI-10, SA-AI-11, SA-KNW-12, SA-QLT-06, SA-CMP-12, SA-OPS-01, SA-OPS-02, SA-OPS-03, SA-OPS-04, SA-OPS-05, SA-OPS-06, SA-OPS-07, SA-OPS-08, SA-OPS-09, SA-OPS-10 | AT-23, AT-24, AT-38, AT-39, AT-40, AT-41, AT-42 |
| P8.4 — Evaluate AI behavior and enforce cost limits | SA-AI-12, SA-AI-15, SA-EVL-03, SA-EVL-04, SA-META-06, SA-CMP-14, SA-OPS-11 | AT-34, AT-48 |
| P9.1 — Implement deterministic pattern detection | SA-SUR-01, SA-SUR-02, SA-SUR-03, SA-SUR-04, SA-SUR-05, SA-SUR-09, SA-SUR-11, SA-CMP-09 | AT-26 |
| P9.2 — Generate restricted investigation briefs and human decisions | SA-SUR-06, SA-SUR-08 | AT-27 |
| P9.3 — Test corrections, false signals, and cross-sector negative controls | SA-SUR-07, SA-SUR-10 | AT-28 |
| P10.1 — Build session scheduling, public views, and calendar export | SA-SES-01, SA-SES-02, SA-SES-03, SA-SES-04, SA-SES-13 | AT-29 |
| P10.2 — Implement manual sharing and gated Facebook/WhatsApp adapters | SA-SES-05, SA-SES-06, SA-SES-07, SA-SES-08, SA-SES-10, SA-SES-11, SA-SES-12, SA-SES-14 | AT-30 |
| P11.1 — Complete the low-input mobile case journey | SA-VIS-05, SA-REP-03, SA-REP-04, SA-HIS-04, SA-FUP-07, SA-OFF-11, SA-UX-01, SA-UX-02, SA-UX-03, SA-UX-04, SA-UX-05, SA-UX-06, SA-UX-07, SA-QLT-10 | Targeted tests in task; shared scenarios per implementation §18.4 |
| P11.2 — Complete officer/public surfaces without scope leakage | SA-SUR-12, SA-UX-08, SA-ANA-01, SA-ANA-02, SA-ANA-03, SA-ANA-04, SA-ANA-05, SA-ANA-06, SA-TEC-10 | AT-32 |
| P12.1 — Run cross-stack privacy and failure-injection tests | SA-HIS-08, SA-UX-09, SA-SEC-05, SA-QLT-02, SA-QLT-04, SA-QLT-07 | Targeted tests in task; shared scenarios per implementation §18.4 |
| P12.2 — Package a reproducible hosted demo and installable APK | SA-SCP-05, SA-SEC-12, SA-QLT-08, SA-QLT-09, SA-EVL-06, SA-REL-03, SA-CMP-08 | Targeted tests in task; shared scenarios per implementation §18.4 |
| P12.3 — Freeze scope and prepare honest demonstration evidence | SA-SCP-03, SA-SEC-13, SA-REL-01, SA-REL-02, SA-REL-04, SA-REL-05, SA-CMP-04, SA-CMP-07, SA-CMP-10, SA-CMP-13 | AT-36, AT-43, AT-44, AT-47 |

The earlier product revision added six tasks to the original 37, bringing that revision's total to 43; P8.6 is the seventh addition in the current plan, for 44 tasks. P8.1–P8.4 and P12 preserve the nonclinical product boundary. P9.1–P9.3 positive inference work is optional and disabled pending product approval/validation; event-specific clearance is separately conditional. This is not permission to mark older medical-advice features complete under a new label.

### 29.1a Additive implementation tasks

All original task IDs remain valid; these seven tasks implement the new owner decisions without replacing the existing coordinator. They extend the plan to 44 tasks.

| Task | Product requirements | Principal scenarios |
|---|---|---|
| P1.3 — Implement Facebook, Google and organizational SSO | SA-IDN-01, SA-IDN-02, SA-IDN-03, SA-IDN-04, SA-IDN-05, SA-IDN-06, SA-IDN-07, SA-IDN-08, SA-IDN-09, SA-IDN-10, SA-IDN-11, SA-IDN-12, SA-IDN-13, SA-IDN-14 | AT-59, AT-60, AT-61, AT-62, AT-63 |
| P2.4 — Implement private global guest intake and routing | SA-PUB-01, SA-PUB-02, SA-PUB-03, SA-PUB-04, SA-PUB-05, SA-PUB-06, SA-PUB-07, SA-PUB-08, SA-PUB-09, SA-PUB-10, SA-PUB-11, SA-PUB-12, SA-PUB-16, SA-GEO-01, SA-GEO-02, SA-GEO-03, SA-GEO-04, SA-GEO-05, SA-GEO-06, SA-GEO-07, SA-GEO-08 | AT-49, AT-50, AT-51, AT-52, AT-53, AT-54, AT-55, AT-56 |
| P3.4 — Implement guest offline capture and receipt continuity | SA-PUB-13, SA-PUB-14, SA-PUB-15 | AT-57, AT-58 |
| P7.4 — Implement Qdrant memory and cache index infrastructure | SA-MEM-01, SA-MEM-02, SA-MEM-03, SA-MEM-04, SA-MEM-05, SA-MEM-06, SA-MEM-07, SA-MEM-08, SA-MEM-14, SA-MEM-16 | AT-66, AT-67, AT-69, AT-72 |
| P8.5 — Integrate authorized AI recall and context-safe cache reuse | SA-MEM-09, SA-MEM-10, SA-MEM-11, SA-MEM-12, SA-MEM-13, SA-MEM-15 | AT-68, AT-70, AT-71 |
| P8.6 — Durable multi-turn reasoning and clarification | SA-LOOP-01–SA-LOOP-15; §15.8 | AT-73–AT-76 |
| P10.3 — Implement authenticated event subscriptions and account preferences | SA-SUB-01, SA-SUB-02, SA-SUB-03, SA-SUB-04, SA-SUB-05, SA-SUB-06, SA-SUB-07 | AT-64, AT-65 |

### 29.2 Requirements-family index

| Family | Meaning | Product section |
|---|---|---|
| SA-VIS | Product invariants | 1 |
| SA-SCP | Scope/mode boundaries | 2 |
| SA-ACL | Identity, access and authority | 3 |
| SA-DAT | Information semantics/provenance | 4 |
| SA-LIF | Case lifecycle | 5 |
| SA-REP | Intake/reporting | 6 |
| SA-HIS | History/corrections | 7 |
| SA-ASG | Ownership/collaboration | 8 |
| SA-AST | Assistance/referrals | 9 |
| SA-MON | Monitoring | 10 |
| SA-FUP | Follow-up | 11 |
| SA-ESC / SA-RES | Escalation/resolution | 12 |
| SA-COM | Original voice, comments, transcripts | 13 |
| SA-OFF | Offline/sync/recovery | 14 |
| SA-AI | AI capabilities/boundaries | 15 |
| SA-SUR | Surveillance/One Health | 16 |
| SA-KNW | Knowledge/source governance | 17 |
| SA-SES | Community sessions/channels | 18 |
| SA-CAT | Condition coverage | 19 |
| SA-UX | Screen/interaction behavior | 20 |
| SA-NOT / SA-ANA | Notifications/analytics/exports | 21 |
| SA-SEC | Privacy/security | 22 |
| SA-TEC | Architectural constraints | 23 |
| SA-QLT | Quality/service behavior | 24 |
| SA-EVL | Data/evaluation | 25 |
| SA-REL | Release acceptance | 27 |
| SA-META | Meta-only inference and budget | 15 |
| SA-CMP | Competition scope, new work, public submission and bonus evidence | 2 |
| SA-OPS | Nonclinical operational plan approval, execution and verification | 15 |

### 29.3 Added requirement families

| Family | Product contract | Primary tasks |
|---|---|---|
| SA-IDN | Federated identity, membership and SSO | P1.3 |
| SA-PUB | Public private-intake and guest continuity | P2.4, P3.4 |
| SA-GEO | Global location/date/time and honest coverage | P2.4 |
| SA-MEM | Qdrant memory/cache lifecycle and authority | P7.4, P8.5 |
| SA-LOOP | Multi-turn investigation, checkpoints, evidence, human approval and stop conditions | P8.6 |
| SA-SUB | Login-required subscriptions and organization actions | P10.3 |

## 30. How to use this specification with the implementation plan

### 30.1 Repository placement

Place `spec.md` beside `implementation.md` at the fresh repository root. Keep both versioned. `spec.md` owns user-visible behavior, scope and acceptance; `implementation.md` owns the existing task sequence, exact implementation interfaces/files and dependency guidance. This revision updates both companions together; earlier source hashes remain historical provenance only. Current paired hashes are in `docs/DOCUMENT-INTEGRITY.md`.

Use paired spec v1.4 and implementation v2.4. Historical single-document/Superpowers handoffs and generated visual concepts are superseded. The supplied Participant Guide and bonus slide inform explicit competition requirements; core implementation is not gated by organizers or dates. Restricted optional capabilities stay disabled pending their product approval and validation. Retain the owner's existing multi-agent system.

### 30.2 Ready-to-use agent handoff

> Read `spec.md` v1.4 and `implementation.md` v2.4 completely, inspect AGENTS.md, repository state and our existing coordinator records, then implement the first dependency-ready product task. Do not wait for event dates, prize/publicity tasks or organizer replies to build core functionality. Preserve old task IDs and implement the seven additive product tasks with their SA/AT contracts. Use our existing multi-agent system; do not install a replacement. Qdrant is the required scoped memory/cache index; PostgreSQL owns records, grants and invalidation. Implement account-free global human/animal reporting with date/location/area/region/country, private intake/receipt and honest coverage. Implement Facebook/Google login and configured organizational OIDC SSO; identity never grants staff roles by itself. Require login for subscriptions and approved membership/action grants for organization assignments/escalations. Preserve original voice, immutable staff history, nonclinical AI, exact-source versions and Meta-only inference including any learned encoder. Keep optional semantic cache disabled until a permitted encoder is proven; retain exact Qdrant paths without fake vectors. Keep guest drafts isolated from staff sync. Run named tests and actual required Android/provider/Qdrant checks; record missing configuration and continue independent work. No invented APIs, paid provisioning, real-data processing, public publication, real external messaging or destructive action without authorization. Record task/requirement/scenario evidence and commits; no mock result satisfies a live capability. Deliver tested product increments, not another roadmap.

### 30.3 Multi-agent coordination contract

Agents may work on independent tasks in parallel only after shared contracts are fixed and file ownership/integration responsibilities are assigned by the existing coordinator. A shared schema or capability-policy change requires coordination and regression checks. An agent must not introduce a separate sync engine, duplicate model-control plane, new clinical state machine or alternative case command endpoint because it is locally convenient.

Track each deliverable with task ID, requirement IDs, scenario IDs, owner, dependency state, commit, tests actually run, results, remaining risks and capability flags. Technical reviews do not replace clinical/veterinary or source-rights approval. Clinical approval must never be inferred from a successful build or a language model review.

### 30.4 Definition of product done for the demonstration

The product is ready for the defined demonstration when the tested release build supports the complete bounded human and animal workflows, all required privacy/integrity gates pass, actual enabled AI capability is evidenced, limitations are visible, and an independent operator or fresh controlled account can reproduce the runbook. It is not production-ready until the separate governance and deployment gates are met.

**Final product invariant:** Anyone can submit a private concern without an account. Every intake has traceable receipt/routing status; accepted managed cases have accountable staff ownership. Open needs remain visible, original voice remains distinct from transcripts, and scoped AI memory never overrides current evidence, permissions or human judgment.

---

## Appendix A. Inherited knowledge-source register

The following is copied by identifier/link from `implementation.md` §24 for implementation convenience. It is a source acquisition register, not a fresh link-verification report, a licence grant, an endorsement, a complete national protocol library, or proof that a document has already been ingested. Review retrieval, rights, edition, applicability and approval separately. No medical guidance is newly inferred from the linked pages in this specification.

| ID | Inherited source link | Intended use |
|---|---|---|
| H01 | [WHO — Cholera][H01] | Human condition reference |
| H02 | [WHO — Typhoid][H02] | Human condition reference |
| H03 | [WHO — Diarrhoeal disease][H03] | AWD/general diarrhoeal education |
| H04 | [WHO — Measles][H04] | Human condition reference |
| H05 | [WHO — HIV and AIDS][H05] | Confidentiality-conscious education |
| H06 | [WHO — Tuberculosis][H06] | Longitudinal support reference |
| H07 | [WHO — Poliomyelitis][H07] | Human condition reference |
| H08 | [WHO — Malaria][H08] | Human condition reference |
| H09 | [WHO — COVID-19][H09] | Version-sensitive human education |
| H10 | [GTFCC — Public health surveillance for cholera][H10] | Guidance, job aids and reporting templates |
| A01 | [WOAH — Rabies][A01] | Animal/One Health reference |
| A02 | [WOAH — Anthrax][A02] | Animal/One Health reference |
| A03 | [WOAH — Brucellosis][A03] | Animal/One Health reference |
| A04 | [WOAH — Foot-and-mouth disease][A04] | Veterinary reference |
| A05 | [WOAH — African swine fever][A05] | Veterinary reference |
| A06 | [WOAH — Newcastle disease][A06] | Poultry reference |
| A07 | [WOAH — Avian influenza][A07] | Animal/One Health reference |
| A08 | [WOAH — Lumpy skin disease][A08] | Veterinary reference |
| A09 | [WOAH — Peste des petits ruminants][A09] | Veterinary reference |
| A10 | [WOAH — Animal disease catalogue][A10] | Controlled catalogue expansion |
| A11 | [FAO — Animal-health situation updates][A11] | Context and official-resource discovery |
| A12 | [WOAH — WAHIS overview][A12] | Official animal-health data context; API/access not assumed |
| N01 | [Zimbabwe Ministry of Health and Child Care][N01] | National human-health source discovery |
| N02 | [Eswatini Ministry of Health][N02] | National human-health source discovery |
| N03 | [Zimbabwe agriculture/veterinary authority][N03] | National veterinary source discovery |
| N04 | [Eswatini Ministry of Agriculture][N04] | National veterinary source discovery |
| G01 | [WHO — Copyright and licensing policy][G01] | Source-specific reuse/translation checks |

[H01]: https://www.who.int/news-room/fact-sheets/detail/cholera
[H02]: https://www.who.int/news-room/fact-sheets/detail/typhoid
[H03]: https://www.who.int/news-room/fact-sheets/detail/diarrhoeal-disease
[H04]: https://www.who.int/news-room/fact-sheets/detail/measles
[H05]: https://www.who.int/news-room/fact-sheets/detail/hiv-aids
[H06]: https://www.who.int/news-room/fact-sheets/detail/tuberculosis
[H07]: https://www.who.int/news-room/fact-sheets/detail/poliomyelitis
[H08]: https://www.who.int/news-room/fact-sheets/detail/malaria
[H09]: https://www.who.int/news-room/fact-sheets/detail/coronavirus-disease-(covid-19)
[H10]: https://www.gtfcc.org/resources/public-health-surveillance-for-cholera/
[A01]: https://www.woah.org/en/disease/rabies/
[A02]: https://www.woah.org/en/disease/anthrax/
[A03]: https://www.woah.org/en/disease/brucellosis/
[A04]: https://www.woah.org/en/disease/foot-and-mouth-disease/
[A05]: https://www.woah.org/en/disease/african-swine-fever/
[A06]: https://www.woah.org/en/disease/newcastle-disease/
[A07]: https://www.woah.org/en/disease/avian-influenza/
[A08]: https://www.woah.org/en/disease/lumpy-skin-disease/
[A09]: https://www.woah.org/en/disease/peste-des-petits-ruminants/
[A10]: https://www.woah.org/en/what-we-do/animal-health-and-welfare/animal-diseases/
[A11]: https://www.fao.org/animal-health/situation-updates/en
[A12]: https://www.woah.org/en/what-we-do/animal-health-and-welfare/disease-data-collection/world-animal-health-information-system/
[N01]: https://www.mohcc.gov.zw/
[N02]: https://www.gov.sz/index.php/ministries-departments/ministry-of-health
[N03]: https://www.agric.gov.zw/
[N04]: https://www.gov.sz/index.php/ministries-departments/ministry-of-agriculture
[G01]: https://www.who.int/about/policies/publishing/copyright


## Appendix B. Document provenance and review record

**Primary basis:** the agreed conversation and `implementation.md`, version/date and content hash recorded at the start of this document. The original 37 task definitions and the product revision's seven additions were reconciled against the requirements, privacy/failure tests, source register, capability gates and deferred work.

**Visual basis:** previously generated web/mobile concepts are illustrative. They do not authorize self-selected staff roles, offline cloud inference, unapproved disease classification, real-location synthetic outbreaks, unsupported numerical disease confidence, or unverified message delivery. Screen contracts in §20 are the functional reference.

**New content in this companion:** stable requirement IDs; product behavior and edge-case wording; screen contracts; operational success-measure definitions; 76 end-to-end acceptance scenarios; task traceability; and PC-01 through PC-08, explicitly identified rather than presented as previously approved decisions.

**Not performed by preparation of this document:** application implementation, package restore/compatibility tests, device execution, model/account provisioning, source-rights adjudication, professional clinical/veterinary review, real health-data collection, or social-message sending. Acceptance checklists and scenarios describe work to verify, not completed tests.

**Review checklist for the product owner and implementation coordinator:**

- [ ] Confirm the explicit PC defaults without relaxing privacy or authoritative-human-control boundaries.
- [ ] Record unresolved gates in `docs/CAPABILITIES.md` and the project decision log.
- [ ] Keep P0 scope and deferred P1/P2 work distinguishable.
- [ ] Use the requirement/task/scenario references in actual implementation reviews.
- [ ] Resolve any true spec/plan conflict explicitly and update both affected documents.
- [ ] Preserve the existing multi-agent development workflow; do not introduce a new orchestration dependency.
- [ ] Require actual release/device/integration evidence before claiming the product is ready for demonstration.

### Document integrity check

This revision requires a fresh document-level check of requirement uniqueness/references, task/scenario ownership, gate/dependency consistency, navigation, table shape, paired links, catalogue/source retention and archive hashes. Actual results are recorded in `docs/DOCUMENT-INTEGRITY.md`; previous validation counts are historical, not automatically applicable.

No application build, model call, device scenario, medical review, social send or submission has been performed by writing these documents. Requirement/acceptance checkboxes describe future work, not completed tests.


[C01]: docs/RULES-SOURCE-REGISTER.md#c01-participant-guide

[C02]: docs/RULES-SOURCE-REGISTER.md#c02-bonus-prize-slide
