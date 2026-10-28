"""Strict wire model for opaque gateway-published jobs.

This module validates envelopes only. It does not claim, execute, acknowledge, or
otherwise enable any job consumer.
"""

from __future__ import annotations

import json
from datetime import datetime, timedelta
from enum import StrEnum
from typing import Literal
from uuid import UUID

from pydantic import BaseModel, ConfigDict, Field, field_validator

MAX_JOB_ENVELOPE_BYTES = 64 * 1024
_JOB_ENVELOPE_FIELDS = frozenset(
    {
        "schemaVersion",
        "messageId",
        "jobId",
        "jobType",
        "organizationId",
        "inputSnapshotId",
        "correlationId",
        "createdAtUtc",
    }
)


class JobEnvelopeTooLargeError(ValueError):
    """Raised before JSON parsing when a broker message exceeds the contract limit."""


class JobEnvelopeFormatError(ValueError):
    """Raised when a broker payload is not the exact documented JSON object."""


def _reject_duplicate_json_keys(pairs: list[tuple[str, object]]) -> dict[str, object]:
    """Build JSON objects while rejecting duplicate keys at any nesting depth."""

    values: dict[str, object] = {}
    for key, value in pairs:
        if key in values:
            raise JobEnvelopeFormatError("Job envelope JSON cannot contain duplicate keys.")
        values[key] = value

    return values


def _reject_non_json_constant(value: str) -> None:
    """Reject Python JSON extensions such as NaN and Infinity."""

    raise JobEnvelopeFormatError(f"Job envelope contains unsupported JSON constant: {value}.")


class JobType(StrEnum):
    """Job types corresponding to the documented JetStream subjects."""

    REPORT = "Report"
    CASE_SUMMARY = "CaseSummary"
    OPS_CONTINUITY = "OpsContinuity"
    SURVEILLANCE = "Surveillance"
    KNOWLEDGE = "Knowledge"
    REMINDER = "Reminder"
    MEMORY_INDEX = "MemoryIndex"


JOB_SUBJECT_SUFFIXES: dict[JobType, str] = {
    JobType.REPORT: "report",
    JobType.CASE_SUMMARY: "case-summary",
    JobType.OPS_CONTINUITY: "ops-continuity",
    JobType.SURVEILLANCE: "surveillance",
    JobType.KNOWLEDGE: "knowledge",
    JobType.REMINDER: "reminder",
    JobType.MEMORY_INDEX: "memory-index",
}


class JobEnvelope(BaseModel):
    """The version-1 opaque message contract from implementation.md §10.2."""

    model_config = ConfigDict(
        extra="forbid",
        frozen=True,
        populate_by_name=False,
        strict=True,
    )

    schema_version: Literal[1] = Field(alias="schemaVersion")
    message_id: UUID = Field(alias="messageId")
    job_id: UUID = Field(alias="jobId")
    job_type: JobType = Field(alias="jobType")
    organization_id: str = Field(alias="organizationId")
    input_snapshot_id: UUID = Field(alias="inputSnapshotId")
    correlation_id: UUID = Field(alias="correlationId")
    created_at_utc: datetime = Field(alias="createdAtUtc")

    @field_validator("organization_id")
    @classmethod
    def validate_organization_id(cls, value: str) -> str:
        if not value.strip():
            raise ValueError("organizationId must be a non-empty opaque identifier")
        return value

    @field_validator("created_at_utc")
    @classmethod
    def require_utc_timestamp(cls, value: datetime) -> datetime:
        if value.tzinfo is None or value.utcoffset() != timedelta(0):
            raise ValueError("createdAtUtc must include a UTC offset")
        return value

    @classmethod
    def from_bytes(cls, message: bytes) -> JobEnvelope:
        """Validate a broker payload after enforcing its byte limit."""

        if not isinstance(message, bytes):
            raise TypeError("job envelope must be supplied as bytes")
        if len(message) > MAX_JOB_ENVELOPE_BYTES:
            raise JobEnvelopeTooLargeError(
                f"job envelope exceeds the {MAX_JOB_ENVELOPE_BYTES}-byte limit"
            )

        try:
            utf8_message = message.decode("utf-8", errors="strict")
        except UnicodeDecodeError as exception:
            raise JobEnvelopeFormatError("Job envelope must use valid UTF-8.") from exception

        try:
            decoded = json.loads(
                utf8_message,
                object_pairs_hook=_reject_duplicate_json_keys,
                parse_constant=_reject_non_json_constant,
            )
        except (json.JSONDecodeError, JobEnvelopeFormatError, RecursionError) as exception:
            raise JobEnvelopeFormatError("Job envelope must be valid strict JSON.") from exception

        if not isinstance(decoded, dict):
            raise JobEnvelopeFormatError("Job envelope must be a JSON object.")

        if set(decoded) != _JOB_ENVELOPE_FIELDS:
            raise JobEnvelopeFormatError(
                "Job envelope must contain exactly the documented camelCase fields."
            )

        return cls.model_validate_json(message)
