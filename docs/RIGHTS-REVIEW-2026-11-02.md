# Demo data and model rights review — 2 November 2026

## Demo data and recordings

The source requirements state that `DEMO_MODE` uses clearly identified synthetic operational data (`SA-SCP-01`) and exclude unconsented recordings (`SA-CMP-08`). Current fixture paths include Ellai dialogue, guidance and scenario JSON plus contract/test fixtures. The tracked tree contains three audio assets: `swiftaid_gentle_notification.wav` in Gateway and Mobile, and `relaxing_synth_ping.mp3` in Mobile. Their names and use indicate notification sounds, but creator/source/license evidence is not recorded here; they are not evidence of consented speech recordings. No recording-consent register was supplied. These observations do not prove every fixture is synthetic or establish consent for external/local recordings; retain the synthetic-only rule and do not include real patient/farmer data.

## Model cards checked

Official Hugging Face model cards were checked on 2 November 2026:

| Model | Model-card evidence | Consequence |
|---|---|---|
| [`facebook/nllb-200-distilled-1.3B`](https://huggingface.co/facebook/nllb-200-distilled-1.3B) | Card lists CC-BY-NC, describes research use, says it is not released for production deployment, and says it is not intended for medical-domain text or certified translation. | Keep NLLB optional/research-only and disabled by default. Do not use for production, medical translation, or certified translation absent separate rights and validation decisions. |
| [`facebook/mms-1b-all`](https://huggingface.co/facebook/mms-1b-all) | Card lists CC-BY-NC 4.0; its training-data section references Google FLEURS. | Noncommercial limits apply; training-data/source rights and suitability are not cleared by this model-card check. |
| [`facebook/mms-tts-eng`](https://huggingface.co/facebook/mms-tts-eng) | Card lists CC-BY-NC 4.0. The pinned local notice records revision/hash and owner-approved noncommercial use. | Preserve the opt-in, noncommercial, attribution and non-redistribution restrictions in `infra/ellai-tts/MODEL-LICENSE.md`. |
| [`facebook/bart-large-mnli`](https://huggingface.co/facebook/bart-large-mnli) | Card lists MIT and describes the checkpoint as BART-large trained on MultiNLI. | Model-card label alone does not clear dataset rights, intended use, source attribution or the product's optional classification gate. |

The cards are evidence about the named repositories, not legal advice or a grant of rights to training data, downstream outputs, or commercial use. Recheck the exact immutable artifact revisions and associated notices before any distribution or changed use.

## Remaining blockers

- `docs/RIGHTS-REGISTER.md` still records unresolved creator/license/redistribution evidence for the owner-supplied brand icon and loader assets, health/veterinary source material, package transitive notices/SBOM, and Qdrant image.
- No signed/recorded consent and provenance entry exists for any demo speaker recording. Source scripts are not consent evidence.
- The checklist's 16 claimed source attachments remain unavailable, so the promised source-by-source rights comparison is incomplete.
- This review did not inspect all packages, source datasets, bundled media, every fixture value, downstream translations, or applicable jurisdictional terms.

## Checklist result

`SW-00-008` remains **Blocked / partial**. The model-card and inventory findings are recorded, but unresolved rights, absent consent records and the missing source documents prevent a pass. No deployment, publication or commercial-use clearance is implied.
