"""Strict validation tests for the version-1 opaque job envelope."""

from __future__ import annotations

import json
from datetime import timedelta
from uuid import UUID

import pytest
from pydantic import ValidationError

from swiftaid_workers.jobs.consumer import (
    JOB_SUBJECT_SUFFIXES,
    MAX_JOB_ENVELOPE_BYTES,
    JobEnvelope,
    JobEnvelopeFormatError,
    JobEnvelopeTooLargeError,
    JobType,
)


def _valid_envelope() -> dict[str, object]:
    return {
        "schemaVersion": 1,
        "messageId": "9160e07e-1385-4b30-995b-eae5fd2651db",
        "jobId": "a8671879-8132-4f46-a689-628fe3791d57",
        "jobType": "CaseSummary",
        "organizationId": "demo-zw",
        "inputSnapshotId": "d31a5e8b-0445-4134-894c-57519ce031b9",
        "correlationId": "081a93cf-1353-43b7-ae5d-dd045786a527",
        "createdAtUtc": "2026-10-25T07:00:00Z",
    }


def _encode(envelope: dict[str, object]) -> bytes:
    return json.dumps(envelope, separators=(",", ":")).encode("utf-8")


def test_job_envelope_accepts_documented_v1_sample_without_reinterpreting_opaque_ids() -> None:
    message = _valid_envelope()

    parsed = JobEnvelope.from_bytes(_encode(message))

    assert parsed.schema_version == 1
    assert parsed.message_id == UUID("9160e07e-1385-4b30-995b-eae5fd2651db")
    assert parsed.job_id == UUID("a8671879-8132-4f46-a689-628fe3791d57")
    assert parsed.job_type is JobType.CASE_SUMMARY
    assert parsed.organization_id == "demo-zw"
    assert parsed.input_snapshot_id == UUID("d31a5e8b-0445-4134-894c-57519ce031b9")
    assert parsed.correlation_id == UUID("081a93cf-1353-43b7-ae5d-dd045786a527")
    assert parsed.created_at_utc.utcoffset() == timedelta(0)


def test_job_envelope_accepts_organization_id_at_200_utf16_units() -> None:
    message = _valid_envelope()
    message["organizationId"] = "x" * 200

    parsed = JobEnvelope.from_bytes(_encode(message))

    assert parsed.organization_id == "x" * 200


def test_job_envelope_rejects_organization_id_over_200_utf16_units() -> None:
    message = _valid_envelope()
    message["organizationId"] = "x" * 199 + "😀"

    with pytest.raises(ValidationError):
        JobEnvelope.from_bytes(_encode(message))


@pytest.mark.parametrize(
    "timestamp",
    ["2026-10-25T07:00:00Z", "2026-10-25T07:00:00+00:00"],
)
def test_job_envelope_accepts_rfc3339_utc_timestamps(timestamp: str) -> None:
    message = _valid_envelope()
    message["createdAtUtc"] = timestamp

    parsed = JobEnvelope.from_bytes(_encode(message))

    assert parsed.created_at_utc.utcoffset() == timedelta(0)


def test_job_type_subject_mapping_matches_documented_job_subjects() -> None:
    assert JOB_SUBJECT_SUFFIXES == {
        JobType.REPORT: "report",
        JobType.CASE_SUMMARY: "case-summary",
        JobType.OPS_CONTINUITY: "ops-continuity",
        JobType.SURVEILLANCE: "surveillance",
        JobType.KNOWLEDGE: "knowledge",
        JobType.REMINDER: "reminder",
        JobType.MEMORY_INDEX: "memory-index",
    }


@pytest.mark.parametrize(
    "extra_field",
    [
        "patientName",
        "diagnosis",
        "exactGps",
        "transcript",
        "media",
        "credentials",
        "signedUrl",
        "sourceText",
        "payload",
        "actorId",
    ],
)
def test_job_envelope_rejects_private_or_unapproved_fields(extra_field: str) -> None:
    message = _valid_envelope()
    message[extra_field] = "must-not-enter-the-broker-envelope"

    with pytest.raises(JobEnvelopeFormatError):
        JobEnvelope.from_bytes(_encode(message))


@pytest.mark.parametrize(
    ("wire_name", "python_name"),
    [
        ("schemaVersion", "schema_version"),
        ("messageId", "message_id"),
        ("jobId", "job_id"),
        ("jobType", "job_type"),
        ("organizationId", "organization_id"),
        ("inputSnapshotId", "input_snapshot_id"),
        ("correlationId", "correlation_id"),
        ("createdAtUtc", "created_at_utc"),
    ],
)
def test_job_envelope_rejects_python_field_names_even_when_wire_alias_is_present(
    wire_name: str, python_name: str
) -> None:
    message = _valid_envelope()
    message[python_name] = message[wire_name]

    with pytest.raises(JobEnvelopeFormatError):
        JobEnvelope.from_bytes(_encode(message))


@pytest.mark.parametrize(
    ("field", "value"),
    [
        ("schemaVersion", 2),
        ("messageId", "not-a-uuid"),
        ("jobType", "case-summary"),
        ("jobType", "UnknownJob"),
        ("organizationId", "  "),
        ("organizationId", " demo-zw"),
        ("organizationId", "demo-zw "),
        ("organizationId", "x" * 201),
        ("organizationId", "demo\x1fzw"),
        ("createdAtUtc", "2026-10-25T07:00:00"),
        ("createdAtUtc", "2026-10-25T09:00:00+02:00"),
        ("createdAtUtc", "not-a-timestamp"),
    ],
)
def test_job_envelope_rejects_invalid_contract_values(field: str, value: object) -> None:
    message = _valid_envelope()
    message[field] = value

    with pytest.raises(ValidationError):
        JobEnvelope.from_bytes(_encode(message))


@pytest.mark.parametrize(
    "payload",
    [
        b"[]",
        b"\xff",
        b'{"schemaVersion":1,,}',
        _encode(_valid_envelope())[:-1] + b',"jobId":"a8671879-8132-4f46-a689-628fe3791d57"}',
        _encode(_valid_envelope()).replace(
            b'"organizationId":"demo-zw"',
            b'"organizationId":{"code":"one","code":"two"}',
        ),
    ],
)
def test_job_envelope_rejects_non_object_invalid_utf8_and_duplicate_json_keys(
    payload: bytes,
) -> None:
    with pytest.raises(JobEnvelopeFormatError):
        JobEnvelope.from_bytes(payload)


@pytest.mark.parametrize(
    "missing_field",
    [
        "schemaVersion",
        "messageId",
        "jobId",
        "jobType",
        "organizationId",
        "inputSnapshotId",
        "correlationId",
        "createdAtUtc",
    ],
)
def test_job_envelope_requires_all_v1_fields(missing_field: str) -> None:
    message = _valid_envelope()
    del message[missing_field]

    with pytest.raises(JobEnvelopeFormatError):
        JobEnvelope.from_bytes(_encode(message))


def test_job_envelope_checks_byte_limit_before_attempting_json_parse() -> None:
    oversized_invalid_json = b"{" + (b" " * MAX_JOB_ENVELOPE_BYTES)

    with pytest.raises(JobEnvelopeTooLargeError):
        JobEnvelope.from_bytes(oversized_invalid_json)


def test_job_envelope_is_immutable_after_validation() -> None:
    envelope = JobEnvelope.from_bytes(_encode(_valid_envelope()))

    with pytest.raises(ValidationError):
        envelope.organization_id = "another-organization"
