# Product acceptance register

Current authority is spec v1.5 and implementation v2.5. The scenario inventory below is retained pending reconciliation to this pair; the prior 76-scenario count belongs to v1.4/v2.4.

Source of truth: spec.md v1.5, sections 26-27; implementation.md v2.5, section 18.4 and task contracts. Snapshot: 2 November 2026. The current product specification defines scenarios through AT-86; existing status entries do not constitute execution evidence.

The source-alignment note below is historical v1.4/v2.4 context; acceptance scope is governed by the current v1.5/v2.5 source pair.

**Evidence rule:** each result must name the test/device/provider, command or procedure, source revision, output/evidence path, and verdict. A mock or OFF-gate result cannot stand in for live provider, Android, licensing, rights, professional, or positive optional-feature evidence.

## Current baseline run

Repository baseline `865964dbe9664a557fd0f0e303af03e115f22839`, branch `interactive-ellai`; focused endpoint correction committed as `5489f2c20ef62d8b9033d901358b943baf4de4a4`. Full commands and limitations are recorded in [BASELINE-AUDIT.md](BASELINE-AUDIT.md).

| Check | Result |
|---|---|
| Domain/Application/Client suites | PASS in the solution baseline: Domain 83, Application 867 passed and 3 skipped, Client 293. |
| Full Integration suite after correction | PASS: 602 passed, 0 failed, 226 skipped. |
| Focused Ellai authorization suite after commit | PASS: 20 passed, 0 failed, 0 skipped. |
| UI suite | FAIL: 520 passed, 7 failed; three display assertions and four renderer exceptions remain open. |
| Android Release build | PASS: 0 warnings, 0 errors; device execution NOT VERIFIED. |
| Python worker suite | PASS: 59 passed, 3 skipped. |
| Compose configuration | PASS: parse only; provider workflows NOT VERIFIED. |
| Full solution / iOS | FAIL: missing local GoogleDataTransport package assets (`MSB3030`). |
| Browser guest-storage harness | NOT VERIFIED: local Playwright package unavailable. |

| ID | Scenario | Status |
|---|---|---|
| AT-01 | Provisioned offline capture | NOT RUN |
| AT-02 | First-run guest / staff separation | NOT RUN |
| AT-03 | Animal group reporting | NOT RUN |
| AT-04 | No diagnosis required | NOT RUN |
| AT-05 | Multi-person narrative | NOT RUN |
| AT-06 | Commit then timeout | NOT RUN |
| AT-07 | Concurrent correction | NOT RUN |
| AT-08 | Immutable facts | NOT RUN |
| AT-09 | Scoped access | NOT RUN |
| AT-10 | Sensitive human case | NOT RUN |
| AT-11 | Reassignment and old record | NOT RUN |
| AT-12 | Offline grant expires | NOT RUN |
| AT-13 | Original voice survives | NOT RUN |
| AT-14 | ASR fails | NOT RUN |
| AT-15 | Audio permission revoked | NOT RUN |
| AT-16 | Transcript correction | NOT RUN |
| AT-17 | Assistance gap | NOT RUN |
| AT-18 | Missed follow-up | NOT RUN |
| AT-19 | Escalation without AI | NOT RUN |
| AT-20 | Closure with open work | NOT RUN |
| AT-21 | Reopening | NOT RUN |
| AT-22 | Hostile narrative | NOT RUN |
| AT-23 | Stale AI proposal | NOT RUN |
| AT-24 | Missing reference/evidence | NOT RUN |
| AT-25 | Withdrawn education | NOT RUN |
| AT-26 | Delayed batch / gated inference | NOT RUN |
| AT-27 | Human/animal relationship gate | NOT RUN |
| AT-28 | Optional signal corrections / gate | NOT RUN |
| AT-29 | Session without social API | NOT RUN |
| AT-30 | Manual share vs delivery | NOT RUN |
| AT-31 | Cancelled reminder | NOT RUN |
| AT-32 | Public privacy | NOT RUN |
| AT-33 | Queue retry/restart | NOT RUN |
| AT-34 | Budget exhausted | NOT RUN |
| AT-35 | Seed coverage | NOT RUN |
| AT-36 | Release rehearsal | NOT RUN |
| AT-37 | Advice refused even with approval | NOT RUN |
| AT-38 | Unapproved plan | NOT RUN |
| AT-39 | Stale or expired approval | NOT RUN |
| AT-40 | Duplicate action and false completion | NOT RUN |
| AT-41 | Partial run | NOT RUN |
| AT-42 | No justified action | NOT RUN |
| AT-43 | Public/new-work/rights audit | NOT RUN |
| AT-44 | Event timeline and video | NOT RUN |
| AT-45 | Optional open-weight proof | NOT RUN |
| AT-46 | No medical-generation alias | NOT RUN |
| AT-47 | Optional promotion truthfulness | NOT RUN |
| AT-48 | Meaningful agentic effect | NOT RUN |
| AT-49 | Guest human report | NOT RUN |
| AT-50 | Global animal report | NOT RUN |
| AT-51 | Geography and time precision | NOT RUN |
| AT-52 | Guest timeout retry | NOT RUN |
| AT-53 | Reviewer promotion race | NOT RUN |
| AT-54 | Uncovered intake | NOT RUN |
| AT-55 | Receipt enumeration | NOT RUN |
| AT-56 | Guest upload and abuse limits | NOT RUN |
| AT-57 | Fresh-install offline guest | NOT RUN |
| AT-58 | Guest to signed-in account | NOT RUN |
| AT-59 | Actual social sign-in | NOT RUN |
| AT-60 | Organizational SSO isolation | NOT RUN |
| AT-61 | Provider linking attack | NOT RUN |
| AT-62 | Native and web flow security | NOT RUN |
| AT-63 | Revoked membership | NOT RUN |
| AT-64 | Event subscription login boundary | NOT RUN |
| AT-65 | Subscription cancellation race | NOT RUN |
| AT-66 | Qdrant scope isolation | NOT RUN |
| AT-67 | Memory poisoning | NOT RUN |
| AT-68 | Exact cache freshness | NOT RUN |
| AT-69 | Expired memory delayed cleanup | NOT RUN |
| AT-70 | Embedding unavailable | NOT RUN |
| AT-71 | Qdrant outage and guest isolation | NOT RUN |
| AT-72 | Erasure versus replay/restore | NOT RUN |
| AT-73 | Multi-turn factual clarification | NOT RUN |
| AT-74 | Interruption, restart and stale context | NOT RUN |
| AT-75 | Approval/receipt and loop budget | NOT RUN |
| AT-76 | Memory and tenant isolation across turns | NOT RUN |
| AT-77 | Microphone denied | NOT RUN |
| AT-78 | Voice Q&A and duplicate finalized turns | NOT RUN |
| AT-79 | Barge-in and checkpoint revisit | NOT RUN |
| AT-80 | Cancellation with queued audio | NOT RUN |
| AT-81 | Authentication or consent revoked mid-speech | NOT RUN |
| AT-82 | Speech provider, network, or budget outage | NOT RUN |
| AT-83 | TTS checkpoint rights gate | NOT RUN |
| AT-84 | Web/Android PCM conformance | NOT RUN |
| AT-85 | Speech is not action proof | NOT RUN |
| AT-86 | Accessible conversation | NOT RUN |

P8.6 evidence remains NOT VERIFIED for AT-73–AT-76. The source requires separate model turns, durable checkpoint/restart behavior, stale/version and permission checks, approval/receipt idempotency, tenant-scoped memory, bounded cycles, and one real supported Meta multi-turn smoke with synthetic case data. Simulated calls and real provider calls must be identified separately. No P8.6 smoke or behavioral test result is recorded here.
## Mandatory release boundaries

- P0 release acceptance is not achieved by scaffolding or by a passing build alone.
- Preserve the account-free guest intake and staff authorization boundary.
- Clinical/veterinary diagnosis, triage, treatment advice, and generated health teaching remain excluded.
- Cross-sector inference remains off until its separate product decision and validation are recorded.
- Before any release claim, resolve applicable privacy/security, real device, integration, source-rights, accessibility, and independent review evidence in the root specification and plan.
