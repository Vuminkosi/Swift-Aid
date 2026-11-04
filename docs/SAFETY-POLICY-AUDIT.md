# Safety policy source audit — 2 November 2026

## Policy confirmed in current requirements

| Boundary | Current source requirement |
|---|---|
| AI diagnosis, medical/veterinary advice, treatment, symptom-based clinical triage, or generated health teaching | `SA-CMP-02` and `SA-CMP-03` in `spec.md` prohibit these capabilities, including via RAG or human approval. `SA-VIS-04` prohibits generated advice and clinical triage. |
| Automatic outbreak confirmation or unsupported disease probability | `SA-SUR-05` requires “Possible pattern for review”; it rejects confirmed-outbreak language and unsupported disease probability. |
| Public advisory publication | `SA-SUR-12` permits only authorized, non-identifying, staff-authored material; an AI health-signal decision cannot publish or generate medical advice. |
| Experimental or unvalidated inference | `SA-SUR-11`, `SA-CAT-02`, and the independent-decision gates require disabled behavior until appropriate approval and validation. |

## Source implementation evidence

- `src/SwiftAid.Infrastructure/Surveillance/SurveillanceOptions.cs`: `Enabled` defaults to `false`; enabled mode requires explicitly scoped `SyntheticEvaluation` settings.
- `src/SwiftAid.Gateway/Surveillance/SurveillanceWorker.cs`: exits immediately when disabled and rejects execution in Production.
- `src/SwiftAid.Infrastructure/Messaging/JetStreamPublisher.cs`: rejects `JobType.Surveillance` publication with `DisabledJobTypeException`.
- `src/SwiftAid.Application/Ellai/EllaiPolicyService.cs` and `ScenarioManifestLoader.cs`: carry refusal/prohibition terms for clinical and outbreak requests.
- `src/SwiftAid.Gateway/Endpoints/CaseClassificationEndpoints.cs` and the UI manifests identify classification as reported semantic patterns, not diagnosis or outbreak confirmation.

## Verification boundary

`SW-00-006` is confirmed at the requirements and source-configuration level. No application test, deployed configuration review, external provider call, or complete browser/mobile user journey was run for this checklist item. This audit therefore does not establish runtime enforcement or prove that every rendered surface is safe. Separate product acceptance remains subject to the applicable AT scenarios and gates.
