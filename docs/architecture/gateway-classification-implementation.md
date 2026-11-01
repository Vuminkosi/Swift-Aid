# Persistent classification implementation

Baseline: `a64bd6f`, 2026-11-01. Implements the owner-authorized continuation in `designs/data/pipeline-requirements-data-classification.md`. Existing intake, Workspace imports, approvals, protected case journals and PostgreSQL outboxes remain canonical.

## Audit and contract

- Python `jobs/consumer.py` validates envelopes only; the entry point refuses execution. Existing job persistence contracts are not registered as a working repository. They prohibit the legacy Surveillance job type. A classification-specific registry is required; this does not enable that legacy job or change official clinical classification.
- Managed case reports have immutable protected v1/v2 journal events. The Gateway journal reader and payload protection store are reusable. Broker messages contain IDs only, never authorized input or tenant authority.
- Domain outbox rows require case-event foreign keys. Derived intelligence must not masquerade as a case command/event. Classification lifecycle records therefore need an additive outbox lane using the existing dispatch/lease pattern and broker, with no sensitive payload.
- A worker credential is scoped to explicit organizations. Gateway resolves ownership from PostgreSQL, checks active organization/workspace participation and binds each lease to that worker, source event, configuration and fingerprint. There is no direct Python database/case-write access.
- Fingerprints include organization, workspace, sensitivity, normalized source, language, immutable configuration (model revision, taxonomy/labels, preprocessing, hypothesis, inference settings). Conservative scope prevents sharing across authorization domains. Source event references preserve provenance separately from reusable predictions.
- Completed artifacts are immutable. Revision associations preserve older results; only a matching current report revision is current. Administrative events do not create new classifiable content.
- Pull consumers use explicit ACK only after Gateway confirms a persisted terminal outcome or completed cache reuse. Transient control-plane failures remain unacknowledged. Lease expiry fences old completion; bounded retries produce durable failure. Invalid broker data is quarantined by digest/stream sequence without retaining raw payload.

## Acceptance and dependencies

First deliver registry + scoped claim/complete/fail + bounded Python consumer and pinned BART inference. Test duplicate delivery, concurrent cache misses, expired completion, changed configuration, cross-organization denial, retry exhaustion and result-before-ACK. Then implement backfill/read tools and downstream quality/detection/signal/investigation stages. Keep canonical documents current and commit verified slices.

The historical 15,000-row benchmark database was explicitly disposable and removed after its tests. Do not describe it as retained production data or silently repopulate the shared database. Backfill must enumerate available durable source revisions and report actual counts.

Model source: [facebook/bart-large-mnli](https://huggingface.co/facebook/bart-large-mnli), pinned revision `d7645e127eaf1aefc7862fd59a17a5aa8558b8ce` verified from the repository API. Its semantic scores are not calibrated clinical probabilities. English-only inference is the initial supported profile; unknown/Shona/siSwati require explicit abstention until independently labeled evaluation approves a language path. Hierarchical pruning and inferred clinical urgency are not enabled. Local GPU observed: RTX 5050 Laptop, 8 GiB; this is inventory, not inference capacity proof.

Rollout remains opt-in; no shared Gateway restart/migration is implied. No diagnosis, official classification, outbreak confirmation or consequential action is authorized by a model output. Human review and existing approvals remain separate.

## First implemented vertical slice

Tables in `swiftaid_classification`: content-addressed `configurations`, leased `results`, immutable case/revision `associations`, notice/configuration `inbox`, ordered metadata `outbox`, and digest-only `quarantine`. Migrations `20261101073125_PersistentClassificationRegistry` and `20261101073438_ClassificationDeliveryFencing` are additive. Database triggers reject completed-result replacement and provenance/configuration changes. The domain's clinical classification is untouched.

Gateway machine routes: `/api/internal/classification/{claim,renew,complete,fail,quarantine}`. Requests are bounded to 32 KiB by Kestrel before endpoint binding. SHA-256-verified bearer credentials, enabled configuration and explicit organization allowlists are required; ordinary cookies do not authorize them. Each request uses a scoped database context. A claim revalidates the committed notice/event identity, active organizational workspace, strict protected journal, configuration and fingerprint. Transient control-plane outages do not authorize ACK. Inference gets at most five attempts by default; renewal cannot extend an attempt beyond ten minutes. No transaction spans inference. Completed/reused and terminal failure states authorize ACK; busy/stale/retry states do not.

Classification lifecycle records publish on `swiftaid.ingestion.v1.classification` through the existing ingestion dispatch loop/stream. This additive outbox lane is needed because the case outbox's foreign key deliberately requires a canonical case event. It has stable message IDs, per-result sequence ordering, lease fencing, eight bounded publication attempts and retained exhaustion. A publication means broker storage, not completed downstream quality/detection.

The Python worker binds an explicitly provisioned durable. Its broker identity has no consumer-create, stream administration or case-publication authority. It has bounded pull lanes and one inference semaphore; token-length-aware batches contain at most four premise/hypothesis pairs and 2,048 padded tokens by default. Source text remains inside the scoped HTTP response and model memory. Reconnect reclaims durable work; a late completion cannot overwrite a newer lease.

## Executed validation (local Windows / synthetic fixtures)

- PASS: three application tests for fingerprints, normalization preserving negation/Unicode, and bounded complete predictions/abstention.
- PASS: six real PostgreSQL registry tests covering duplicate completion, cache reuse/configuration upgrade, concurrent claims, bounded renewal/expiry/exhaustion, worker-scope/notice denial, and database-enforced artifact immutability.
- PASS: real isolated Kestrel → Python → pinned BART → Gateway/PostgreSQL → JetStream replay/quarantine. Twelve raw semantic labels; 22 maximum premise/hypothesis tokens; **726.09 ms inference**, **863.56 ms claim/inference/commit/replay/quarantine interval**, one broker redelivery and zero pending acknowledgements. Model initialization is excluded from that interval; the whole integration test took about five seconds. Subsequent lifecycle publication was verified through the actual dispatcher. This is one short synthetic report, not classification accuracy or sustained throughput.
- PASS: three real repository-ACL broker tests including worker fetch/ACK and denied case publication.
- PASS: broader .NET regression 35 passed, three SQLAUTH opt-in skips. Four initial failures came from the newly created fixture lacking the existing `lock_case_authority` helper; provisioning the documented helper fixed replay without product changes.
- PASS: Python suite 52 passed, two broker opt-in skips before the new ACL test; Ruff and strict mypy passed. The wider run first exposed an outdated golden-fixture filename list (seven already-supported case events were omitted); the list now validates all current fixtures against the existing schema.
- A new fingerprint property initially recursively serialized itself; the regression test crashed. Excluding the derived hash from serialization corrected it; tests then passed. Model download encountered timeout/DNS errors but resumed and the pinned safetensors loaded successfully. Neither failure is counted as a passed attempt.

NOT VERIFIED: deployment of the published Gateway, physical clients, GPU inference, Shona/siSwati/translation accuracy, whole-process worker termination, long network partitions, 15,000-record classification backfill, 100k/million-record end-to-end processing, or downstream surveillance. Human review APIs, source-language propagation from import provenance, processing UI, backfill orchestration, read tools and downstream intelligence are subsequent slices. The default unknown-language abstention is intentional; an English test-profile override is not evidence of automatic language identification.
