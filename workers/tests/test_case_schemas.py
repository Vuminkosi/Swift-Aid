"""Contract checks for the versioned managed-case command and event schemas."""

from __future__ import annotations

import copy
import json
from pathlib import Path
from typing import Any

from jsonschema import Draft202012Validator, FormatChecker
from referencing import Registry, Resource

ROOT = Path(__file__).resolve().parents[2]
GOLDEN_EVENT_FIXTURE_DIR = ROOT / "contracts/fixtures/case-events"
GOLDEN_EVENT_FIXTURE_NAMES = (
    "case-action-requested.v1.json",
    "case-action-review-approved.v1.json",
    "case-action-review-denied.v1.json",
    "case-assigned.v1.json",
    "case-escalation-acknowledged.v1.json",
    "case-escalation-closed.v1.json",
    "case-escalation-raised.v1.json",
    "case-follow-up-outcome-missed.v1.json",
    "case-follow-up-outcome-rescheduled.v1.json",
    "case-follow-up-scheduled.v1.json",
    "case-observation-recorded.v1.json",
    "case-reopened.v1.json",
    "case-report-corrected.v1.json",
    "case-reported.v1.json",
    "case-resolved.v1.json",
)
SCHEMA_PATHS = {
    "command": ROOT / "contracts/commands/case-command.v1.schema.json",
    "event": ROOT / "contracts/events/case-event.v1.schema.json",
    "alignment": ROOT / "contracts/events/case-event-alignment.v1.schema.json",
}


def _load_schemas() -> dict[str, dict[str, Any]]:
    return {
        name: json.loads(path.read_text(encoding="utf-8"))
        for name, path in SCHEMA_PATHS.items()
    }


SCHEMAS = _load_schemas()
REGISTRY = Registry()
for _schema in SCHEMAS.values():
    REGISTRY = REGISTRY.with_resource(
        _schema["$id"],
        Resource.from_contents(_schema),
    )

VALIDATORS = {
    name: Draft202012Validator(
        schema,
        registry=REGISTRY,
        format_checker=FormatChecker(),
    )
    for name, schema in SCHEMAS.items()
}


def _command_fixture() -> dict[str, Any]:
    return {
        "schemaVersion": 1,
        "commandId": "11111111-1111-4111-8111-111111111111",
        "caseId": "22222222-2222-4222-8222-222222222222",
        "expectedVersion": 0,
        "commandType": "CaseReported",
        "payload": {
            "workspaceId": "workspace-1",
            "accountableOwnerId": "owner-1",
            "report": {
                "kind": "Human",
                "sector": "HumanHealth",
                "narrative": "A community member requested follow-up.",
                "observations": [{"narrative": "The report was received."}],
            },
        },
    }


def _event_fixture(event_id: str, event_type: str, payload: dict[str, Any]) -> dict[str, Any]:
    return {
        "schemaVersion": 1,
        "eventId": event_id,
        "eventType": event_type,
        "caseId": "22222222-2222-4222-8222-222222222222",
        "aggregateVersion": 2,
        "reportRevision": 1,
        "commandId": "33333333-3333-4333-8333-333333333333",
        "actorId": "reviewer-1",
        "occurredAtUtc": "2026-10-26T12:00:00Z",
        "recordedAtUtc": "2026-10-26T12:00:01Z",
        "payload": payload,
    }


def _request_event() -> dict[str, Any]:
    return _event_fixture(
        "44444444-4444-4444-8444-444444444444",
        "CaseActionRequested",
        {
            "requestId": "55555555-5555-4555-8555-555555555555",
            "requestedOperation": "Reassign",
            "reason": "Request reassignment review.",
            "evidenceEventIds": [],
            "proposedOwnerId": "owner-2",
        },
    )


def _accepted_event() -> dict[str, Any]:
    return _event_fixture(
        "66666666-6666-4666-8666-666666666666",
        "CaseAssigned",
        {"accountableOwnerId": "owner-2"},
    )


def _approved_review(linked_event_id: str | None) -> dict[str, Any]:
    return _event_fixture(
        "77777777-7777-4777-8777-777777777777",
        "CaseActionRequestReviewed",
        {
            "requestId": "55555555-5555-4555-8555-555555555555",
            "outcome": "Approved",
            "linkedAcceptedEventId": linked_event_id,
            "reason": "The requested reassignment was authorized.",
        },
    )


def _action_linkage_matches(
    request_event: dict[str, Any],
    accepted_event: dict[str, Any],
    review_event: dict[str, Any],
) -> bool:
    """Check that the accepted effect and review match the pending request."""
    return (
        request_event["eventType"] == "CaseActionRequested"
        and request_event["payload"]["requestedOperation"] == "Reassign"
        and accepted_event["eventType"] == "CaseAssigned"
        and request_event["caseId"] == accepted_event["caseId"] == review_event["caseId"]
        and accepted_event["commandId"] == review_event["commandId"]
        and review_event["payload"]["requestId"] == request_event["payload"]["requestId"]
        and review_event["payload"]["linkedAcceptedEventId"] == accepted_event["eventId"]
    )


def test_case_command_schema_accepts_valid_report_and_rejects_invalid_payload() -> None:
    valid_command = _command_fixture()
    invalid_command = copy.deepcopy(valid_command)
    invalid_command["payload"]["report"]["observations"] = [{"text": "wrong property"}]

    assert VALIDATORS["command"].is_valid(valid_command)
    assert not VALIDATORS["command"].is_valid(invalid_command)


def test_case_event_schema_requires_link_for_approved_review_and_links_effect() -> None:
    request_event = _request_event()
    accepted_event = _accepted_event()
    approved_review = _approved_review(accepted_event["eventId"])
    approved_without_link = _approved_review(None)

    for event in (request_event, accepted_event, approved_review):
        assert VALIDATORS["event"].is_valid(event)
    assert not VALIDATORS["event"].is_valid(approved_without_link)
    assert _action_linkage_matches(request_event, accepted_event, approved_review)

    unrelated_effect = copy.deepcopy(approved_review)
    unrelated_effect["payload"]["linkedAcceptedEventId"] = "88888888-8888-4888-8888-888888888888"
    assert VALIDATORS["event"].is_valid(unrelated_effect)
    assert not _action_linkage_matches(request_event, accepted_event, unrelated_effect)


def test_alignment_schema_enforces_approved_review_link_and_request_match() -> None:
    request_event = _request_event()
    accepted_event = _accepted_event()
    approved_review = _approved_review(accepted_event["eventId"])
    approved_without_link = _approved_review(None)

    for event in (request_event, approved_review):
        assert VALIDATORS["alignment"].is_valid(event)
    assert not VALIDATORS["alignment"].is_valid(approved_without_link)
    assert _action_linkage_matches(request_event, accepted_event, approved_review)

    mismatched_request = copy.deepcopy(approved_review)
    mismatched_request["payload"]["requestId"] = "99999999-9999-4999-8999-999999999999"
    assert VALIDATORS["alignment"].is_valid(mismatched_request)
    assert not _action_linkage_matches(request_event, accepted_event, mismatched_request)


def test_golden_case_event_fixtures_validate_against_event_schema() -> None:
    actual_names = {path.name for path in GOLDEN_EVENT_FIXTURE_DIR.glob("*.json")}
    expected_names = set(GOLDEN_EVENT_FIXTURE_NAMES)
    assert actual_names == expected_names, (
        f"Expected exactly {sorted(expected_names)} in {GOLDEN_EVENT_FIXTURE_DIR}; "
        f"found {sorted(actual_names)}"
    )

    for fixture_name in GOLDEN_EVENT_FIXTURE_NAMES:
        fixture_path = GOLDEN_EVENT_FIXTURE_DIR / fixture_name
        event = json.loads(fixture_path.read_text(encoding="utf-8"))
        errors = list(VALIDATORS["event"].iter_errors(event))
        assert not errors, f"{fixture_path.name} violates the event schema: {errors}"
