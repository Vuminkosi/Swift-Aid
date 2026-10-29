export async function loadCountryGeometry() {
    const sourceUrl = new URL("./natural-earth-countries-v1.json", import.meta.url);
    const response = await fetch(sourceUrl, { cache: "force-cache" });

    if (!response.ok) {
        throw new Error("The local country geometry could not be loaded.");
    }

    return await response.text();
}
