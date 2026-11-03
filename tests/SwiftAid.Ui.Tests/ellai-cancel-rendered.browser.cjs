const assert = require("node:assert/strict");
const { chromium } = require("playwright");

// Opt-in: this exercises a synthetic guest message through a live provider.
async function main() {
    assert.equal(process.env.SWIFTAID_RUN_LIVE_ELLAI, "1", "Explicit live Ellai test opt-in is required.");
    const baseUrl = process.env.SWIFTAID_GATEWAY_URL ?? "http://localhost:8080";
    const browser = await chromium.launch({ headless: true });
    const context = await browser.newContext();
    const page = await context.newPage();
    const errors = [];
    page.on("pageerror", error => errors.push(error.message));
    try {
        await page.goto(baseUrl);
        await page.getByRole("button", { name: "Continue with essential cookies", exact: true }).click();
        await page.locator(".ellai-launcher-menu__button").click();
        await page.getByRole("menuitem", { name: "Ask", exact: true }).click();
        const input = page.getByRole("textbox", { name: "Message Ellai" });
        await input.fill("Explain in several paragraphs how SwiftAid handles an intake receipt.");
        await page.getByRole("button", { name: "Send to Ellai", exact: true }).click();
        await page.waitForFunction(() => localStorage.getItem("swiftaid.ellai.session"));
        const sessionId = await page.evaluate(() => localStorage.getItem("swiftaid.ellai.session"));
        const turn = await page.evaluate(async id => {
            const deadline = Date.now() + 15000;
            while (Date.now() < deadline) {
                const response = await fetch(`/api/v1/ellai/guest/sessions/${id}/responses?cursor=0&includeProgress=true`);
                if (!response.ok) throw new Error(`Feed failed: ${response.status}`);
                const item = (await response.json()).conversation?.at(-1);
                if (item?.outcome === "Processing") return item.clientUtteranceId;
                if (item?.completedAtUtc) throw new Error(`Turn finished before cancellation: ${item.outcome}`);
                await new Promise(resolve => setTimeout(resolve, 100));
            }
            throw new Error("Turn did not reach processing.");
        }, sessionId);
        const cancellation = page.waitForResponse(response => response.request().method() === "POST" &&
            new URL(response.url()).pathname === `/api/v1/ellai/guest/sessions/${sessionId}/cancel`);
        await page.getByRole("button", { name: "End Ellai session", exact: true }).click();
        assert.equal((await cancellation).status(), 200);
        const result = await page.evaluate(async ({ sessionId, turn }) => {
            const session = await fetch(`/api/v1/ellai/guest/sessions/${sessionId}`);
            if (!session.ok) throw new Error(`Session read failed: ${session.status}`);
            const status = (await session.json()).status;
            const deadline = Date.now() + 60000;
            while (Date.now() < deadline) {
                const response = await fetch(`/api/v1/ellai/guest/sessions/${sessionId}/responses?cursor=0&includeProgress=true`);
                if (!response.ok) throw new Error(`Feed failed: ${response.status}`);
                const feed = await response.json();
                const item = feed.conversation?.find(value => value.clientUtteranceId === turn);
                if (item?.completedAtUtc) return { status, outcome: item.outcome, answer: item.assistantText,
                    chunks: feed.responses?.filter(value => value.clientUtteranceId === turn && !value.progress && value.text).length ?? 0 };
                await new Promise(resolve => setTimeout(resolve, 250));
            }
            throw new Error("Cancelled turn did not reach a terminal outcome.");
        }, { sessionId, turn });
        assert.equal(result.status, "Cancelled");
        assert.equal(result.outcome, "Cancelled");
        assert.ok(!result.answer);
        assert.equal(result.chunks, 0);
        assert.deepEqual(errors, []);
        console.log("PASS: rendered End cancelled the processing turn without publishing or persisting an answer.");
    } finally {
        await context.close();
        await browser.close();
    }
}

main().catch(error => { console.error(error); process.exitCode = 1; });
