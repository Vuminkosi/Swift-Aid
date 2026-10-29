export function readPublicPolicy() {
    const root = document.getElementById("guest-root");
    if (!root) {
        return null;
    }

    const dataset = root.dataset;
    return {
        reference: dataset.processingPolicyReference || null,
        version: dataset.processingPolicyVersion || null,
        noticeText: dataset.processingPolicyNoticeText || null,
        acknowledgementLabel: dataset.processingPolicyAcknowledgementLabel || null,
        provenance: dataset.processingPolicyProvenance || null,
        metadataNotice: dataset.processingPolicyMetadataNotice || null,
        updatedAtUtc: dataset.processingPolicyUpdatedAtUtc || null,
        isAvailable: dataset.processingPolicyAvailable === "true"
    };
}
