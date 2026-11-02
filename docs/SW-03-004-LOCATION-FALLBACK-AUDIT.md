# SW-03-004 location fallback and verification audit - 2 November 2026

## Outcome

The shared picker offers a generic country → region/province → town/village/locality cascade and a manual provisional entry callback in the guided report. Source search did not find an authorized registry verification/review workflow for provisional places, and country-specific administrative schema behavior was not established. Keep SW-03-004 partial.

## Evidence

- `src/SwiftAid.UI/Components/Geography/SwiftAidLocationPicker.razor` shows browse selectors and a delegated provisional-action button; `GuidedReportStepper.razor` binds that action to manual place fields.
- The report model/mapping paths distinguish selected registry IDs from manual draft fields; provisional entry does not itself prove the value is a canonical verified registry node.
- The catalogue is explicitly partial, so a no-result path is necessary. No authorization-protected registry moderation/verification route was found in the focused geography source search.
- No source/test/browser evidence here establishes country-specific cascading options, offline persistence/reconciliation, or an authorized reviewer promotion workflow. No tests were run for this audit.

## Closure work

Define how a manual/unknown location is stored without fabricated IDs/coordinates; provide explicit country-aware hierarchy and unknown/provisional labels; implement or document an authorized verification path with source/actor/time audit; test offline draft, no-match, invalid parent and verification denial flows. No source or database was changed.
