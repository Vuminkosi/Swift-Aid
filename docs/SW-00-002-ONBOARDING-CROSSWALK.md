# Onboarding local-ID crosswalk to current authority

Date: 2 November 2026
Source: `designs/onboarding/requirements.md` (references spec v1.4 / implementation v2.4)
Current authority: `spec.md` v1.5 and `implementation.md` v2.5

This crosswalk accounts for all 27 onboarding-local IDs. The design is retained as historical UI/flow guidance. It does not add requirements to the current authority pair, and a candidate SA mapping is not evidence that a flow is implemented or accepted.

| Local ID | Current contract crosswalk | Reconciliation |
|---|---|---|
| M-ONB-01 | SA-PUB-01, SA-UX-04, SA-UX-06 | Guest entry remains available; carousel persistence and reduced-motion behavior are design guidance pending current-flow verification. |
| M-ONB-02 | SA-UX-02, SA-HIS-01, SA-MON-04 | Informational onboarding slide only; any case-management destination requires current authorization. |
| M-ONB-03 | SA-KNW-01..12, SA-CMP-03, SA-UX-08 | Education description is illustrative; current rules require source-linked, reviewed material and prohibit AI-authored health education. |
| M-ONB-04 | SA-PUB-01, SA-PUB-02, SA-PUB-12, SA-IDN-03 | Guest reporting and account actions are separate. Saved-draft navigation must preserve local guest state. |
| M-AUTH-01 | SA-IDN-05, SA-IDN-06, SA-IDN-12, SA-UX-04, SA-UX-06 | Use current Gateway authentication/session contracts; provider buttons are configuration-gated; do not infer membership from login. |
| M-AUTH-02 | SA-IDN-02, SA-IDN-03, SA-IDN-06, SA-UX-04 | Registration creates an ordinary account only. Password policy and validation must come from the current backend. |
| M-AUTH-03 | SA-IDN-06, SA-IDN-12, SA-UX-04 | Password-reset request must use generic account-existence-safe responses and the current server contract. |
| M-AUTH-04 | SA-IDN-06, SA-UX-04 | Pending/reset-request state must describe request acceptance, not email delivery. |
| M-AUTH-05 | SA-IDN-06, SA-IDN-12, SA-UX-04 | Reset authority remains token-bound and does not create an authenticated session. |
| M-AUTH-06 | SA-IDN-01, SA-IDN-05, SA-IDN-14 | Only actual configured Google federation may be offered; the pictured provider account chooser is externally hosted, not a SwiftAid mock. |
| M-AUTH-07 | SA-IDN-01, SA-IDN-05, SA-IDN-14 | Same gate as Google: only real configured Facebook federation; no simulated approval or identity picker. |
| M-AUTH-08 | SA-IDN-07..10 | Only allowlisted, administrator-configured OIDC organizations; arbitrary issuer discovery and role promotion are prohibited. |
| M-AUTH-09 | SA-IDN-06, SA-IDN-14 | Source explicitly allows an existing email-link contract instead of OTP. Preserve actual backend verification; do not invent six-digit codes or timers. |
| M-PRF-01 | SA-IDN-03, SA-UX-04, SA-UX-05, SA-UX-06 | Profile fields must reflect verified/current server data; profile location is not case occurrence and GPS is not required. |
| M-PRF-02 | SA-IDN-03, SA-IDN-10, SA-UX-08 | Participation/role choice is preference only and cannot create permissions, membership or clinical authority. |
| M-PRF-03 | SA-IDN-03, SA-IDN-07, SA-IDN-10 | Organization access requires authoritative invitation/request/admin grant state; a selection or pending request is not access. |
| M-DONE-01 | SA-IDN-03, SA-IDN-11, SA-IDN-12 | Destination must derive from actual membership/authorization state; ordinary registration cannot open a private workspace. |
| M-TOUR-01 | SA-ELV-01..12, SA-UX-04, SA-UX-06 | Conditional on an installed and approved guidance renderer; tour availability/completion cannot authorize or complete business actions. |
| W-ONB-01 | SA-PUB-01, SA-PUB-12, SA-UX-04, SA-UX-06, SA-UX-08 | Public reporting remains prominent and account-free; marketing copy is subordinate to current safety and capability evidence. |
| W-AUTH-01 | SA-IDN-05, SA-IDN-06, SA-IDN-12, SA-UX-04, SA-UX-06 | Reuse current authentication contract and safe return-path rules; provider availability remains runtime-configured. |
| W-AUTH-02 | SA-IDN-02, SA-IDN-03, SA-IDN-06, SA-UX-04 | Registration and verification continuity must follow server state; no unauthorized access after reload. |
| W-AUTH-03 | SA-IDN-06, SA-IDN-12, SA-IDN-14, SA-UX-04 | Reset/verification routes must work from fresh navigation and preserve actual token/link semantics. |
| W-PRF-01 | SA-IDN-03, SA-UX-04, SA-UX-05, SA-UX-06 | Same profile and unknown-data rules as mobile; responsive composition does not create a second business flow. |
| W-PRF-02 | SA-IDN-03, SA-IDN-07, SA-IDN-10, SA-UX-08 | Clearly separate participation preference from server-authorized membership and workspace access. |
| W-DONE-01 | SA-IDN-03, SA-IDN-11, SA-IDN-12 | Completion destination follows server-authoritative account/access state; tour remains optional. |
| W-PUB-01 | SA-PUB-01, SA-PUB-14, SA-PUB-15, SA-UX-04 | Public/auth navigation must preserve guest draft and return context; account login does not silently attach or submit guest work. |
| W-ACC-01 | SA-IDN-04, SA-IDN-06, SA-IDN-11, SA-IDN-12, SA-UX-04 | Settings expose only real authorized account, identity, membership and session state; fake devices or federation links are excluded. |

## Current evidence and remaining verification

- The source names 27 unique local IDs and references historical spec v1.4 / implementation v2.4; the crosswalk above covers all 27 exactly once.
- Current spec families are candidate mappings, not proof that every old screen or control remains required. Detailed implementation/acceptance still belongs to the current v1.5/v2.5 SA/AT contracts.
- `docs/SW-02-010-ONBOARDING-UX-AUDIT.md` records existing surface/source observations and browser/device gaps. Provider callback, Android identity storage, server-persisted profile/onboarding continuity, responsive/accessibility and full account-switch journeys remain separately unverified in the linked SW-02 audits.
- No screenshot, provider mock, local preference or source description is treated as runtime acceptance. SA-IDN-01/14 provider claims remain blocked until real configured callbacks are tested.

SW-00-002 source-local onboarding mapping gap is closed at the inventory/crosswalk level. This document does not close the remaining broader source-conflict review or assert product behavior.
