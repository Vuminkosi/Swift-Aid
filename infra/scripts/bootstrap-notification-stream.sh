#!/bin/sh
set -eu

dry_run=false
if [ "${1:-}" = "--dry-run" ]; then
    dry_run=true
    shift
fi
fail() {
    printf '%s\n' "Notification stream bootstrap: $1" >&2
    exit 1
}

[ "$#" -eq 0 ] || fail "only the optional --dry-run argument is supported."

require_uint_range() {
    name=$1
    value=$2
    minimum=$3
    maximum=$4

    case "$value" in
        ''|*[!0-9]*) fail "$name must be an unsigned integer between $minimum and $maximum." ;;
    esac

    while [ "${value#0}" != "$value" ]; do
        value=${value#0}
    done
    [ -n "$value" ] || value=0
    [ "${#value}" -le 9 ] || fail "$name is outside its supported range."
    [ "$value" -ge "$minimum" ] && [ "$value" -le "$maximum" ] ||
        fail "$name must be between $minimum and $maximum."

    printf '%s' "$value"
}

setting() {
    jq -er --arg key "$1" '.Nats[$key] | select(type == "number")' "$APPSETTINGS_PATH" ||
        fail "Nats:$1 must be configured as a number in appsettings.json."
}

effective_setting() {
    environment_key=$1
    settings_key=$2

    eval "environment_is_set=\${$environment_key+x}"
    if [ "$environment_is_set" = x ]; then
        eval "printf '%s' \"\${$environment_key}\""
    else
        setting "$settings_key"
    fi
}

require_exact() {
    name=$1
    value=$2
    expected=$3
    [ "$value" = "$expected" ] || fail "$name must be $expected for this Compose deployment."
}

: "${APPSETTINGS_PATH:?APPSETTINGS_PATH is required}"
: "${NATS_URL:?NATS_URL is required}"
if [ "$dry_run" != true ]; then
    : "${NATS_USER:?NATS_USER is required}"
    : "${NATS_PASSWORD:?NATS_PASSWORD is required}"
fi
[ -r "$APPSETTINGS_PATH" ] || fail "appsettings.json is not readable."

app_stream=$(jq -er '.Nats.NotificationStream | select(type == "string")' "$APPSETTINGS_PATH") ||
    fail "Nats:NotificationStream must be configured in appsettings.json."
app_subject=$(jq -er '.Nats.NotificationSubject | select(type == "string")' "$APPSETTINGS_PATH") ||
    fail "Nats:NotificationSubject must be configured in appsettings.json."
stream=${NATS_NOTIFICATION_STREAM:-$app_stream}
subject=${NATS_NOTIFICATION_SUBJECT:-$app_subject}
require_exact NATS_NOTIFICATION_STREAM "$stream" SWIFTAID_NOTIFICATIONS
require_exact NATS_NOTIFICATION_SUBJECT "$subject" swiftaid.notifications.inapp
require_exact APPSETTINGS_NATS_NOTIFICATION_STREAM "$app_stream" "$stream"
require_exact APPSETTINGS_NATS_NOTIFICATION_SUBJECT "$app_subject" "$subject"

max_age_seconds=$(require_uint_range Nats__NotificationStreamMaxAgeSeconds \
    "$(effective_setting Nats__NotificationStreamMaxAgeSeconds NotificationStreamMaxAgeSeconds)" 1 31536000)
max_age_nanos=$((max_age_seconds * 1000000000))
max_bytes=$(require_uint_range Nats__NotificationStreamMaxBytes \
    "$(effective_setting Nats__NotificationStreamMaxBytes NotificationStreamMaxBytes)" 1048576 1073741824)
max_messages=$(require_uint_range Nats__NotificationStreamMaxMessages \
    "$(effective_setting Nats__NotificationStreamMaxMessages NotificationStreamMaxMessages)" 1 10000000)
max_message_size=$(require_uint_range Nats__NotificationStreamMaxMessageSizeBytes \
    "$(effective_setting Nats__NotificationStreamMaxMessageSizeBytes NotificationStreamMaxMessageSizeBytes)" 1024 1048576)
replicas=$(require_uint_range Nats__NotificationStreamReplicas \
    "$(effective_setting Nats__NotificationStreamReplicas NotificationStreamReplicas)" 1 5)
require_exact NATS_NOTIFICATION_REPLICAS "$replicas" 1
duplicate_window_seconds=$(require_uint_range Nats__NotificationStreamDuplicateWindowSeconds \
    "$(effective_setting Nats__NotificationStreamDuplicateWindowSeconds NotificationStreamDuplicateWindowSeconds)" 1 86400)
duplicate_window_nanos=$((duplicate_window_seconds * 1000000000))
consumer_ack_wait_seconds=$(require_uint_range Nats__NotificationConsumerAckWaitSeconds \
    "$(effective_setting Nats__NotificationConsumerAckWaitSeconds NotificationConsumerAckWaitSeconds)" 5 300)
consumer_max_deliveries=$(require_uint_range Nats__NotificationConsumerMaxDeliveries \
    "$(effective_setting Nats__NotificationConsumerMaxDeliveries NotificationConsumerMaxDeliveries)" 1 8)
consumer_inactive_threshold_seconds=$(require_uint_range Nats__NotificationConsumerInactiveThresholdSeconds \
    "$(effective_setting Nats__NotificationConsumerInactiveThresholdSeconds NotificationConsumerInactiveThresholdSeconds)" 60 3600)

# These limits are applied by the Gateway when it creates its scoped durable
# consumers. Validate them here as part of the same deployment preflight.
[ "$consumer_ack_wait_seconds" -ge 5 ] && [ "$consumer_max_deliveries" -le 8 ] &&
    [ "$consumer_inactive_threshold_seconds" -ge 60 ] ||
    fail "notification consumer limits are outside their supported ranges."

if [ "$dry_run" = true ]; then
    printf 'Resolved %s subject=%s max-age=%s max-bytes=%s max-messages=%s max-message-size=%s replicas=%s duplicate-window=%s consumer-ack-wait=%s consumer-max-deliveries=%s consumer-inactive-threshold=%s\n' \
        "$stream" "$subject" "$max_age_seconds" "$max_bytes" "$max_messages" \
        "$max_message_size" "$replicas" "$duplicate_window_seconds" \
        "$consumer_ack_wait_seconds" "$consumer_max_deliveries" "$consumer_inactive_threshold_seconds"
    exit 0
fi

temp_dir=$(mktemp -d)
trap 'rm -rf "$temp_dir"' EXIT HUP INT TERM
stream_info="$temp_dir/stream-info.json"

stream_matches() {
    jq -e \
        --arg name "$stream" \
        --arg subject "$subject" \
        --argjson max_age_seconds "$max_age_seconds" \
        --argjson max_bytes "$max_bytes" \
        --argjson max_msgs "$max_messages" \
        --argjson max_msg_size "$max_message_size" \
        --argjson duplicate_window_seconds "$duplicate_window_seconds" \
        '.config.name == $name and
         .config.subjects == [$subject] and
         .config.storage == "file" and
         .config.retention == "limits" and
         .config.discard == "old" and
         (.config.max_age / 1000000000) == $max_age_seconds and
         .config.max_bytes == $max_bytes and
         .config.max_msgs == $max_msgs and
         .config.max_msg_size == $max_msg_size and
         .config.num_replicas == 1 and
         (.config.duplicate_window / 1000000000) == $duplicate_window_seconds' >/dev/null
}

if nats stream info "$stream" --json > "$stream_info" 2>/dev/null; then
    stream_matches < "$stream_info" ||
        fail "existing $stream does not match appsettings and its explicit overrides; operator review is required. No update or purge was attempted."
    printf 'Verified existing %s stream configuration.\n' "$stream"
    exit 0
fi

# `stream add` uses the CREATE API. The bootstrap account has no UPDATE or
# PURGE permission; a race with an existing stream therefore fails safely.
# `--defaults` accepts all prompts: `stream add` asks for confirmation even
# when every flag is supplied, and there is no terminal in Compose.
nats stream add "$stream" \
    --subjects "$subject" \
    --storage file \
    --retention limits \
    --discard old \
    --max-age "${max_age_seconds}s" \
    --max-bytes "$max_bytes" \
    --max-msgs "$max_messages" \
    --max-msg-size "$max_message_size" \
    --replicas 1 \
    --dupe-window "${duplicate_window_seconds}s" \
    --ack \
    --defaults

nats stream info "$stream" --json > "$stream_info" ||
    fail "created $stream but could not verify it; operator inspection is required."
stream_matches < "$stream_info" ||
    fail "created $stream does not match appsettings and its explicit overrides; operator inspection is required. No update or purge was attempted."

printf 'Created and verified %s stream configuration.\n' "$stream"
