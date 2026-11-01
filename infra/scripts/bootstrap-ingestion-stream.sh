#!/bin/sh
set -eu

# Provision only; never update, purge or delete an existing stream. Fixed versioned subjects
# prevent broad accidental subscriptions. Discard-new applies backpressure instead of losing old work.
: "${NATS_URL:?NATS_URL is required}"
: "${NATS_USER:?NATS_USER is required}"
: "${NATS_PASSWORD:?NATS_PASSWORD is required}"
stream=SWIFTAID_INGESTION
matches() {
    jq -e '.config.name == "SWIFTAID_INGESTION" and
      (.config.subjects | sort) == ["swiftaid.ingestion.v1.case", "swiftaid.ingestion.v1.intake"] and
      .config.storage == "file" and .config.retention == "limits" and .config.discard == "new" and
      .config.max_msgs == 1000000 and .config.max_bytes == 1073741824 and
      .config.max_msg_size == 4096 and .config.max_age == 0 and .config.num_replicas == 1 and
      .config.duplicate_window == 120000000000' >/dev/null
}
if info=$(nats stream info "$stream" --json 2>/dev/null); then
    printf '%s' "$info" | matches || { echo 'Ingestion stream differs; operator review required.' >&2; exit 1; }
else
    nats stream add "$stream" --subjects 'swiftaid.ingestion.v1.case,swiftaid.ingestion.v1.intake' \
      --storage file --retention limits --discard new --max-age 0s --max-msgs 1000000 \
      --max-bytes 1073741824 --max-msg-size 4096 --replicas 1 --dupe-window 120s --ack --defaults
    nats stream info "$stream" --json | matches
fi
printf 'Verified %s. Publication does not imply processing completion.\n' "$stream"
