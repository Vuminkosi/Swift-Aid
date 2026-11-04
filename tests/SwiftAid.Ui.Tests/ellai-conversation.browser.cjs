const assert = require("node:assert/strict");
const { chromium } = require(process.env.SWIFTAID_PLAYWRIGHT_PACKAGE || "playwright");

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
        const thinking = page.locator(".ellai-status__thinking");
        await thinking.waitFor({ state: "visible" });
        const dot = thinking.locator(".ellai-status__dot").first();
        assert.notEqual(await dot.evaluate(element => getComputedStyle(element).animationName), "none");
        await page.emulateMedia({ reducedMotion: "reduce" });
        assert.equal(await dot.evaluate(element => getComputedStyle(element).animationName), "none");
        await page.emulateMedia({ reducedMotion: "no-preference" });
        await page.setViewportSize({ width: 390, height: 844 });
        const thinkingBox = await thinking.boundingBox();
        assert.ok(thinkingBox && thinkingBox.x >= 0 && thinkingBox.x + thinkingBox.width <= 390);
        if (process.env.SWIFTAID_THINKING_SCREENSHOT) {
            await page.locator(".ellai-composer").screenshot({ path: process.env.SWIFTAID_THINKING_SCREENSHOT });
        }
        await page.setViewportSize({ width: 1280, height: 800 });
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
        await thinking.waitFor({ state: "hidden" });
        const originalSession = await page.evaluate(() => localStorage.getItem("swiftaid.ellai.session"));
        assert.ok(originalSession);
        if (sessionId) assert.equal(originalSession, sessionId);
        if (process.env.SWIFTAID_WAIT_FOR_GATEWAY_RESTART === "1") {
            console.log(JSON.stringify({ checkpoint: "ready-for-isolated-gateway-restart", sessionId: originalSession }));
            await new Promise((resolve, reject) => {
                const cleanup = () => { clearTimeout(timeout); process.stdin.pause(); process.stdin.removeListener("data", onData); process.stdin.removeListener("end", onEnd); };
                const onData = () => { cleanup(); resolve(); };
                const onEnd = () => { cleanup(); reject(new Error("Gateway restart signal closed.")); };
                const timeout = setTimeout(() => { cleanup(); reject(new Error("Gateway restart was not signalled within 60 seconds.")); }, 60000);
                process.stdin.once("data", onData);
                process.stdin.once("end", onEnd);
                process.stdin.resume();
            });
        }
        latestPage = null;
        await page.reload();
        await input.waitFor();
        await page.waitForFunction(text => document.querySelector(".ellai-composer__response")?.textContent?.trim() === text, turn.assistantText);
        assert.equal(await page.evaluate(() => localStorage.getItem("swiftaid.ellai.session")), originalSession);
        assert.deepEqual(errors, []);
        if (process.env.SWIFTAID_SECOND_PROMPT) {
            const priorUtteranceId = turn.clientUtteranceId;
            latestPage = null;
            await input.fill(process.env.SWIFTAID_SECOND_PROMPT);
            await page.getByRole("button", { name: "Send to Ellai", exact: true }).click();
            const followupDeadline = Date.now() + 120000;
            let followup;
            while (Date.now() < followupDeadline) {
                followup = latestPage?.conversation?.find(item => item.clientUtteranceId !== priorUtteranceId && item.completedAtUtc);
                if (followup) break;
                await page.waitForTimeout(200);
            }
            assert.equal(followup?.outcome, "Completed", `Follow-up outcome: ${followup?.outcome ?? "missing"}`);
            if (process.env.SWIFTAID_EXPECT_SECOND_TEXT)
                assert.ok(followup.assistantText?.toLowerCase().includes(process.env.SWIFTAID_EXPECT_SECOND_TEXT.toLowerCase()),
                    "Follow-up did not recall the expected synthetic detail.");
            await page.waitForFunction(text => document.querySelector(".ellai-composer__response")?.textContent?.trim() === text, followup.assistantText);
            assert.deepEqual(errors, []);
            console.log(JSON.stringify({ followupOutcome: followup.outcome,
                syntheticDetailRecalled: process.env.SWIFTAID_EXPECT_SECOND_TEXT ? true : null }));
        }
        // Wait for the server before closing the browser: click completion alone
        // does not mean the asynchronous cancellation request has finished.
        const cancellation = page.waitForResponse(response =>
            response.request().method() === "POST" &&
            new URL(response.url()).pathname === `/api/v1/ellai/guest/sessions/${originalSession}/cancel`);
        await page.getByRole("button", { name: "End Ellai session", exact: true }).click();
        const cancelled = await cancellation;
        assert.equal(cancelled.status(), 200, `Cancellation HTTP status: ${cancelled.status()}`);
        const durableSession = await page.evaluate(async id => {
            const response = await fetch(`/api/v1/ellai/guest/sessions/${id}`);
            if (!response.ok) throw new Error(`Session read failed: ${response.status}`);
            return response.json();
        }, originalSession);
        assert.equal(durableSession.status, "Cancelled");
        console.log(JSON.stringify({ result: "PASS", sessionId: originalSession,
            firstChunkMs: firstChunkAt ? firstChunkAt - started : null, completedMs,
            outcome: turn.outcome, restoredAfterRefresh: true, thinkingAnimation: true,
            reducedMotion: true, mobileStatusVisible: true, pageErrors: errors.length,
            cancellationStatus: durableSession.status }));
    } finally {
        await context.close();
        await browser.close();
    }
}

main().catch(error => { console.error(error); process.exitCode = 1; });
