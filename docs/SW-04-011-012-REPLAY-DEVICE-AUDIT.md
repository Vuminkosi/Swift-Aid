# SW-04-011/012 duplicate prevention and Android acceptance audit - 2 November 2026

## SW-04-011 — partial

The GuestSubmissionJourneyTests suite passed 9/9 under EV-047 and covers stable local submission identity, repeated queue, retry/terminal transport states and receipt binding. Intake server/application tests cover proof-bound idempotent receipt and promotion replay under EV-049/050. PostgreSQL attachment-binding tests cover duplicate attachment reuse/replay but are opt-in and were not run. Concurrent reviewers, timeout after server commit, live duplicate prevention and cross-process replay are not proven end-to-end.

## SW-04-012 — NOT VERIFIED

The Android Release build passed in the earlier baseline record, and a prior device test covered first-run Welcome only. No fresh Android Release report journey has been evidenced for offline draft → force-stop/reboot → reconnect → accepted receipt → independent authorized readback. Mobile guest transport remains local-only/unavailable, making this acceptance currently unavailable end-to-end.

## Closure work

First implement the approved guest upload/intake transport and durable proof/replay path under its owning task. Then run on a fresh Android Release install with synthetic human and animal cases, process death, reconnect after ambiguous response, exact receipt and separate authorized staff readback. Include concurrent reviewer/replay races on a disposable PostgreSQL environment. No Android device or PostgreSQL operation was run for this audit.
