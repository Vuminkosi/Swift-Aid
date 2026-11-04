# Optional inference gate audit — 2 November 2026

## Default and admission controls

| Capability | Source gate | Observed default / boundary |
|---|---|---|
| Experimental NLLB translation bridge | `TranslationOptions` is bound from `ExperimentalMultilingualIntelligence`; `Allows` requires `Enabled`, research-only mode, mandatory human review, Development/Test environment and an explicitly listed organization. `TranslationRegistry` applies this check on admission, claim and completion. | `Enabled` defaults to `false`; Compose supplies no enabling override. `EllaiTranslationWorker` only claims through the registry and only when its provider is configured. The main application's `ExperimentalMultilingualIntelligenceOptions` declaration is not itself wired to the runtime; the actual admission gate is `TranslationOptions` / `TranslationRegistry`. |
| Classification-driven surveillance | `SurveillanceOptions` defaults disabled and requires explicit workspace and organization scopes plus `SyntheticEvaluation` mode when enabled. The outbox dispatcher and signal-review service check `Enabled`; `SurveillanceWorker` exits when disabled and rejects Production execution. | `Surveillance` is not enabled by the shipped Compose configuration. Job persistence and `JetStreamPublisher` reject surveillance job dispatch. |
| Cross-sector inference | The product contract requires `CrossSectorInferenceEnabled=false` and no signal investigation tool/job/UI by default. | No `CrossSectorInferenceEnabled` runtime option, `signal.investigate` tool, or cross-sector inference dispatcher was found in current source. The Python job-envelope module explicitly validates envelopes only; it does not execute jobs. |

## UI and tool boundary

The current Ellai tool catalogue exposes authorized classification evidence as read-only reported semantic patterns; no signal-investigation tool is registered. Current surveillance map descriptions state that report groups are not individual case locations or confirmed outbreaks. These are source observations, not rendered UI acceptance.

## Result and limits

The source-level default-off and admission gates for experimental translation and synthetic surveillance are confirmed. Cross-sector inference has no runtime activation path in the inspected source. This does not prove deployed environment values, worker/provider runtime behavior, browser/mobile rendering, absence of out-of-repository activation, or licensing and policy approval. No optional inference capability is claimed as accepted or production-ready.
