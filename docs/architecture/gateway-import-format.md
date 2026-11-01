# Case CSV compatibility and Ellai capture assistance

The supplied `designs/data/zimbabwe_10500_cases.csv` and `designs/data/eswatini_4500_cases.csv` define the accepted version-one format. They are local, Git-ignored authoring fixtures and are not copied into source control. Both were read through the streaming adapter: 10,500 Zimbabwe records and 4,500 Eswatini records, including Human, Herd and Flock subjects. This check parsed and mapped records only; it did not ingest them into the shared application database.

```csv
schema_version,command_id,case_id,external_record_id,subject_scope,narrative,observations_json,captured_at,occurred_at,country_code,region,area,locality,location_id,latitude,longitude,location_precision,accountable_owner_id,workspace_id,source_language,source_reference
```

`observations_json` remains an array of original strings, escaped as a CSV field. Objects, null/empty observations, more than 32 observations or individual observations over 300 characters are rejected. Narrative is bounded to 6,000 characters. Empty narrative is allowed when observations supply evidence. Missing clinical fields remain explicitly unknown; neither the parser nor Ellai infers diagnoses, species or animal counts.

- `command_id` is a stable nonempty UUID retained on retry. `case_id` is the **source** case identifier, not a request to overwrite or choose a destination managed-case ID. The registration service assigns destination identity and returns a durable receipt.
- `external_record_id`, `source_reference` and `source_language` remain original source metadata. Original field values and their SHA-256 hash are available to the import ledger independently of normalized report facts.
- Empty `workspace_id` uses the authorized import context. A supplied workspace must match it. Empty `accountable_owner_id` uses the submitting officer via normal registration; a different supplied owner is rejected. Import does not bypass assignment workflow.
- The country must match the explicitly reviewed import context. Region/area/locality names remain reported names; supplied `location_id` is a reported normalized reference, not proof of geographic verification.
- Times retain the supplied offset and normalize actual instants to UTC; no server-local timezone inference. Empty times remain unknown. Date-only occurrence precision is supported.
- Both supplied files omit coordinates. Future coordinate rows must include an optional `accuracy_metres` column; the adapter never invents coordinate accuracy. Existing geographic bounds/precision rules still apply.
- Header order may vary, but required column names must be present once. Unknown columns are rejected, preventing silently dropped source fields.

`BoundedCsvReader` reads 4,096 characters at a time, supports quoted commas/newlines/doubled quotes and bounds records to 65,536 characters, fields to 16,384 characters and columns to 32. Malformed CSV or an overlarge field stops at the failing structural boundary; earlier durable records must be reconciled by the import service. No full-file `ReadToEnd` or collection is used in this path. The development demo seeder remains separate.

## Ellai tools

- `ellai_ingestion_schema`: returns the current format, capture requirements and authority boundaries.
- `ellai_validate_observations`: validates string-array shape and bounds; returns only count/format status, without echoing the evidence or claiming submission.
- `ellai_locations`: uses the existing public registry, returns bounded candidates with registry-version provenance, and requires user selection.

These tools run through the existing broker, session scope, budget, catalogue and progress narration. They are available in the public capture-assistance profile without exposing case queues, receipts or staff authority. They do not submit cases or approve administrative actions. Existing report forms and canonical services own durable submission; actual import endpoint/ledger delivery is tracked separately in the implementation plan.

Verification: focused application tests cover malformed CSV, field bounds, cancellation, string-array compatibility, source preservation, unknown facts, owner mismatch, deterministic source hashes, tool scope/budget and registry provenance. The supplied-file fixture test is opt-in via `SWIFTAID_TEST_CASE_CSV_DIRECTORY`; missing local files are never silently fabricated.
