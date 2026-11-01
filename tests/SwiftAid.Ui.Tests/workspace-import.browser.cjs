// Invoked by CaseImportBrowserTests: real Gateway component/services and disposable PostgreSQL.
// Identity and policy are fixtures. No shared Gateway, real patient data or provider is contacted.
const assert = require("node:assert/strict");
const path = require("node:path");
const fs = require("node:fs");
const { chromium } = (() => { try { return require("playwright"); } catch { return require("playwright-core"); } })();

async function main() {
    const base = process.env.SWIFTAID_IMPORT_BROWSER_URL;
    assert.match(base || "", /^http:\/\/127\.0\.0\.1:\d+$/);
    const artifacts = process.env.SWIFTAID_IMPORT_BROWSER_ARTIFACTS;
    const browser = await chromium.launch({ headless: true, ...(process.env.SWIFTAID_CHROMIUM_PATH ? { executablePath: process.env.SWIFTAID_CHROMIUM_PATH } : {}) });
    try {
        const page = await browser.newPage({ viewport: { width: 1440, height: 1000 } });
        const errors = [];
        page.on("pageerror", error => errors.push(error.message));
        const started = Date.now();
        await page.goto(base + "/workspace/imports");
        await page.getByLabel("Case reports file", { exact: true }).waitFor();
        await page.getByLabel("Case reports file", { exact: true }).setInputFiles(process.env.SWIFTAID_IMPORT_BROWSER_FILE);
        await page.getByRole("checkbox", { name: /reviewed these reports/ }).check();
        await page.getByRole("checkbox", { name: /may be processed/ }).check();
        await page.getByRole("button", { name: "Import cases", exact: true }).click();
        await page.getByRole("button", { name: "Pause", exact: true }).click();
        await page.getByText("Paused. Select Resume import to continue with the same file.", { exact: true }).waitFor({ timeout: 60000 });
        await page.screenshot({ path: path.join(artifacts, "workspace-import-paused.png"), fullPage: true });
        await page.reload();
        await page.getByRole("button", { name: "Resume", exact: true }).first().click();
        await page.getByText("Select the original file and confirm its details to resume.", { exact: true }).waitFor();
        await page.getByLabel("Case reports file", { exact: true }).setInputFiles(process.env.SWIFTAID_IMPORT_BROWSER_FILE);
        await page.getByRole("checkbox", { name: /reviewed these reports/ }).check();
        await page.getByRole("checkbox", { name: /may be processed/ }).check();
        await page.getByRole("button", { name: "Resume import", exact: true }).click();
        await page.getByText("Import complete. Your cases are saved.", { exact: true }).waitFor({ timeout: 60000 });
        const counts = await page.locator(".case-imports__counts").innerText();
        assert.match(counts, /Cases saved\s+250/);
        assert.match(counts, /Reports not imported\s+0/);
        await page.screenshot({ path: path.join(artifacts, "workspace-import-desktop.png"), fullPage: true });
        await page.setViewportSize({ width: 390, height: 844 });
        await page.screenshot({ path: path.join(artifacts, "workspace-import-mobile-width.png"), fullPage: true });
        assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth), true, "No horizontal page overflow at 390px");
        await page.reload();
        await page.getByRole("button", { name: "View result", exact: true }).first().click();
        await page.getByText("Import complete. Your cases are saved.", { exact: true }).waitFor();
        assert.match(await page.locator(".case-imports__counts").innerText(), /Cases saved\s+250/);
        await page.getByRole("button", { name: "New import", exact: true }).click();
        // Wait for the server render to replace the completed import's disabled file picker.
        await page.getByRole("button", { name: "Import cases", exact: true }).waitFor();
        await page.waitForFunction(() => !document.getElementById("case-import-file")?.disabled);
        const invalidCsv = fs.readFileSync(process.env.SWIFTAID_IMPORT_BROWSER_FILE, "utf8").split(/\r?\n/).slice(0, 3).join("\n").replace(/^"1",/m, '"99",') + "\n";
        await page.getByLabel("Case reports file", { exact: true }).setInputFiles({ name: "synthetic-invalid.csv", mimeType: "text/csv", buffer: Buffer.from(invalidCsv) });
        await page.getByRole("checkbox", { name: /reviewed these reports/ }).check();
        await page.getByRole("checkbox", { name: /may be processed/ }).check();
        await page.getByRole("button", { name: "Import cases", exact: true }).click();
        await page.getByText("Import finished with reports to review.", { exact: true }).waitFor();
        assert.match(await page.locator(".case-imports__counts").innerText(), /Reports not imported\s+1/);
        await page.reload();
        await page.getByRole("button", { name: "View result", exact: true }).first().click();
        await page.getByRole("heading", { name: "Reports to review", exact: true }).waitFor();
        assert.match(await page.locator(".case-imports__counts").innerText(), /Cases saved\s+1/);
        await page.screenshot({ path: path.join(artifacts, "workspace-import-restored-issue.png"), fullPage: true });
        await page.getByRole("button", { name: "New import", exact: true }).click();
        await page.getByRole("button", { name: "Import cases", exact: true }).waitFor();
        const sharedLabel = process.env.SWIFTAID_IMPORT_BROWSER_SHARED_ORG + " / workspace-1 · Zimbabwe";
        const selectSharedOrganization = async () => {
            await page.getByLabel("Organization, workspace and country", { exact: true }).click();
            await page.getByText(sharedLabel, { exact: true }).last().click();
            await page.getByText("Organization: " + process.env.SWIFTAID_IMPORT_BROWSER_SHARED_ORG, { exact: true }).waitFor();
        };
        await selectSharedOrganization();
        await page.getByLabel("Case reports file", { exact: true }).setInputFiles(process.env.SWIFTAID_IMPORT_BROWSER_SHARED_FILE);
        await page.getByRole("checkbox", { name: /reviewed these reports/ }).check();
        await page.getByRole("checkbox", { name: /may be processed/ }).check();
        await page.getByRole("button", { name: "Import cases", exact: true }).click();
        await page.getByText("Import complete. Your cases are saved.", { exact: true }).waitFor();
        assert.match(await page.locator(".case-imports__counts").innerText(), /Cases saved\s+1/);
        await page.reload();
        await selectSharedOrganization();
        await page.getByRole("button", { name: "View result", exact: true }).first().click();
        await page.getByText("Import complete. Your cases are saved.", { exact: true }).waitFor();
        assert.match(await page.locator(".case-imports__counts").innerText(), /Cases saved\s+1/);
        await page.screenshot({ path: path.join(artifacts, "workspace-import-shared-organization.png"), fullPage: true });
        assert.deepEqual(errors, []);
        const result = { scenario: "250 rows, pause, refresh, resume, durable restore, invalid-row recovery, second organization in shared workspace", elapsedMs: Date.now() - started, pageErrors: errors, desktop: "1440x1000", compact: "390x844", authentication: "fixture", persistence: "real PostgreSQL" };
        fs.writeFileSync(path.join(artifacts, "result.json"), JSON.stringify(result, null, 2));
        console.log(JSON.stringify(result));
    } finally { await browser.close(); }
}
main().catch(error => { console.error(error); process.exitCode = 1; });
