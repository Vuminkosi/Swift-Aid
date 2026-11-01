export async function loadCountryGeometry() {
    const sourceUrl = new URL("./natural-earth-countries-v1.json", import.meta.url);
    const response = await fetch(sourceUrl, { cache: "force-cache" });

    if (!response.ok) {
        throw new Error("The local country geometry could not be loaded.");
    }

    // Stream the asset instead of returning a large string through a single
    // Blazor Server message. Keep the same path for browser and native clients.
    const bytes = new Uint8Array(await response.arrayBuffer());
    if (bytes.byteLength > 256 * 1024) {
        throw new Error("The country geometry exceeds the supported size.");
    }
    return bytes;
}
