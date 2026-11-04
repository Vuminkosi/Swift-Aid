export function downloadFile(fileName, contentType, content) {
    if (typeof fileName !== "string" || !fileName ||
        typeof contentType !== "string" || !contentType ||
        content == null) {
        return false;
    }

    const bytes = content instanceof Uint8Array ? content : new Uint8Array(content);
    if (bytes.length === 0 || bytes.length > 16 * 1024) {
        return false;
    }

    const blob = new Blob([bytes], { type: contentType });
    const url = URL.createObjectURL(blob);
    const anchor = document.createElement("a");
    anchor.href = url;
    anchor.download = fileName;
    anchor.hidden = true;
    document.body.appendChild(anchor);

    try {
        anchor.click();
    } finally {
        anchor.remove();
        window.setTimeout(() => URL.revokeObjectURL(url), 1000);
    }

    return true;
}
