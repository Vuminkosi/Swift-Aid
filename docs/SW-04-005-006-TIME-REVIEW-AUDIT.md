# SW-04-005/006 report time and review confirmation audit - 2 November 2026

## SW-04-005 — partial

The report contract separates capture time from occurrence time. `ReportedTime` retains precision, local date/range, original offset and zone. The guided wizard offers unknown, approximate, date-only and exact occurrence choices; exact time asks for the reported UTC offset. It also carries country catalogue version and country confirmation. Relative-date voice interpretation, country/locality conversion, and upload/receipt instants were not fully traced end-to-end, so this remains partial.

## SW-04-006 — partial

The guided report includes explicit review and processing acknowledgements; `PublicReportDraft` booleans default false, validation requires them, and a material field change clears the report-review confirmation. The UI copy states the workflow records observations and does not diagnose, treat, promise service or assign a worker. This audit found no auto-checked confirmation in the reviewed source. However proposed extraction/sensitive-permission review is not part of this source path, and no rendered interaction test verified the confirmation resets.

## Evidence and verification boundary

Sources: `src/SwiftAid.Contracts/Geography/ReportedTime.cs`, `src/SwiftAid.Contracts/Intake/IntakeSubmission.cs`, `src/SwiftAid.UI/Components/Public/PublicReportDraft.cs`, and `src/SwiftAid.UI/Components/Public/GuidedReportStepper/GuidedReportStepper.razor`.

No tests or UI/device journey were run for this audit. Confirm exact time, local date, offset, unknown and country/locality selections through serialized draft/receipt; alter a material field after review and verify the final submission requires reconfirmation. Keep transcription/extraction optional, proposed and separately reviewed if enabled.
