const assert = require("node:assert/strict");
const { chromium } = require("playwright");

// Opt-in: uses the configured local Gateway/provider with synthetic text only.
// This is a real browser/API/persistence check, not a provider mock or hardware voice test.
async function main() {
    assert.equal(process.env.SWIFTAID_RUN_LIVE_ELLAI, "1", "Explicit live Ellai test opt-in is required.");
    const baseUrl = process.env.SWIFTAID_GATEWAY_URL ?? "http://localhost:8080";
    const browser = await chromium.launch({ headless: true });
    const context = await browser.newContext();
    const page = await context.newPage();
    const errors = [];
    page.on("pageerror", error => errors.push(error.message));
    let sessionId;
    let latestPage;
    let firstChunkAt;
    page.on("response", async response => {
        try {
        const url = new URL(response.url());
        if (response.request().method() === "POST" && url.pathname.endsWith("/guest/sessions") && response.ok()) {
            sessionId = (await response.json()).sessionId;
        }
        if (url.pathname.endsWith("/responses") && response.ok()) {
            latestPage = await response.json();
            if (latestPage.responses?.some(item => item.text) && !firstChunkAt) firstChunkAt = Date.now();
        }
        } catch { /* Refresh may discard an in-flight response body. */ }
    });
    try {
        await page.goto(baseUrl);
        await page.getByRole("button", { name: "Continue with essential cookies", exact: true }).click();
        await page.locator(".ellai-launcher-menu__button").click();
        await page.getByRole("menuitem", { name: "Ask", exact: true }).click();
        const input = page.getByRole("textbox", { name: "Message Ellai" });
        await input.waitFor();
        const started = Date.now();
        await input.fill(process.env.SWIFTAID_TEXT_PROMPT ?? "Hi. Reply with one short friendly greeting.");
        await page.getByRole("button", { name: "Send to Ellai", exact: true }).click();
        const deadline = Date.now() + 120000;
        let turn;
        while (Date.now() < deadline) {
            turn = latestPage?.conversation?.at(-1);
            if (turn && ["Completed", "TerminalFailure", "Cancelled"].includes(turn.outcome)) break;
            await page.waitForTimeout(200);
        }
        if (turn?.outcome !== "Completed") console.log(JSON.stringify({ sessionId, outcome: turn?.outcome, elapsedMs: Date.now() - started }));
        assert.equal(turn?.outcome, "Completed", `Durable outcome: ${turn?.outcome ?? "missing"}`);
        assert.ok(turn.userText && turn.assistantText);
        await page.waitForFunction(text => document.querySelector(".ellai-composer__response")?.textContent?.trim() === text, turn.assistantText);
        const completedMs = Date.now() - started;
        const originalSession = sessionId;
        assert.ok(originalSession);
        latestPage = null;
        await page.reload();
        await input.waitFor();
        await page.waitForFunction(text => document.querySelector(".ellai-composer__response")?.textContent?.trim() === text, turn.assistantText);
        assert.equal(await page.evaluate(() => localStorage.getItem("swiftaid.ellai.session")), originalSession);
        assert.deepEqual(errors, []);
        console.log(JSON.stringify({ result: "PASS", sessionId: originalSession,
            firstChunkMs: firstChunkAt ? firstChunkAt - started : null, completedMs,
            outcome: turn.outcome, restoredAfterRefresh: true, pageErrors: errors.length }));
        // End only this synthetic test conversation through the regular user control.
        await page.getByRole("button", { name: "End Ellai session", exact: true }).click();
    } finally {
        await context.close();
        await browser.close();
    }
}

main().catch(error => { console.error(error); process.exitCode = 1; });
