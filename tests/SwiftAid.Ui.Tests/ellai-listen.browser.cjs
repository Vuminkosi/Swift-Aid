const assert = require("node:assert/strict");
const { chromium } = require(process.env.SWIFTAID_PLAYWRIGHT_PACKAGE || "playwright");

// Opt-in, synthetic text through the real Gateway and MMS-TTS. Observes native
// Web Audio playback; it does not establish loudspeaker quality or echo rejection.
async function main() {
    assert.equal(process.env.SWIFTAID_RUN_LIVE_ELLAI, "1", "Live test opt-in required.");
    const browser = await chromium.launch({ headless: true,
        executablePath: process.env.SWIFTAID_CHROMIUM_PATH || undefined });
    const page = await browser.newPage();
    const errors = [];
    const audio = [];
    let feed;
    let sessionId;
    await page.addInitScript(() => {
        window.ellaiPlaybackProbe = { started: 0, ended: 0 };
        const create = AudioContext.prototype.createBufferSource;
        AudioContext.prototype.createBufferSource = function (...args) {
            const source = create.apply(this, args);
            const start = source.start;
            source.start = function (...startArgs) {
                window.ellaiPlaybackProbe.started++;
                return start.apply(this, startArgs);
            };
            source.addEventListener("ended", () => window.ellaiPlaybackProbe.ended++);
            return source;
        };
    });
    page.on("pageerror", error => errors.push(error.message));
    page.on("response", async response => {
        try {
            const path = new URL(response.url()).pathname;
            if (path.endsWith("/responses") && response.ok()) feed = await response.json();
            if (/\/responses\/\d+\/audio$/.test(path)) audio.push({ status: response.status(), path });
        } catch { /* A closed test page may discard an in-flight poll. */ }
    });
    async function send(text) {
        await page.getByRole("textbox", { name: "Message Ellai" }).fill(text);
        const started = Date.now();
        await page.getByRole("button", { name: "Send to Ellai", exact: true }).click();
        while (Date.now() - started < 120000) {
            const turn = feed?.conversation?.find(item => item.userText === text);
            if (turn && ["Completed", "Cancelled", "TerminalFailure"].includes(turn.outcome)) {
                assert.equal(turn.outcome, "Completed");
                await page.waitForFunction(answer =>
                    document.querySelector(".ellai-composer__response")?.textContent?.trim() === answer,
                    turn.assistantText);
                return Date.now() - started;
            }
            await page.waitForTimeout(200);
        }
        throw new Error("No durable outcome within 120 seconds.");
    }
    async function playedAfter(count) {
        await page.waitForFunction(prior => window.ellaiPlaybackProbe.started > prior, count,
            { timeout: 60000 });
        await page.waitForFunction(() => window.ellaiPlaybackProbe.started === window.ellaiPlaybackProbe.ended,
            null, { timeout: 60000 });
    }
    try {
        await page.goto(process.env.SWIFTAID_GATEWAY_URL ?? "http://localhost:8080");
        await page.getByRole("button", { name: "Continue with essential cookies", exact: true }).click();
        await page.locator(".ellai-launcher-menu__button").click();
        await page.getByRole("menuitem", { name: "Ask", exact: true }).click();
        const firstMs = await send("Hello. Reply with one short greeting.");
        sessionId = await page.evaluate(() => localStorage.getItem("swiftaid.ellai.session"));
        assert.equal(audio.length, 0, "Typed responses begin silent.");
        await page.getByRole("button", { name: "Listen to Ellai", exact: true }).click();
        await playedAfter(0);
        const enabled = page.getByRole("button", { name: "Turn off Ellai voice", exact: true });
        assert.equal(await enabled.getAttribute("aria-pressed"), "true");
        const firstSources = await page.evaluate(() => window.ellaiPlaybackProbe.started);
        const secondMs = await send("Thank you. Reply with a short goodbye.");
        await playedAfter(firstSources);
        const spokenSources = await page.evaluate(() => window.ellaiPlaybackProbe.started);
        await enabled.click();
        const requestsBeforeSilentTurn = audio.length;
        const thirdMs = await send("Hello again. Reply with one short greeting.");
        await page.waitForTimeout(1000);
        assert.equal(audio.length, requestsBeforeSilentTurn, "Disabled voice must not fetch new speech.");
        assert.equal(await page.evaluate(() => window.ellaiPlaybackProbe.started), spokenSources);
        assert.equal(await page.getByRole("button", { name: "Listen to Ellai", exact: true })
            .getAttribute("aria-pressed"), "false");
        assert.ok(audio.length >= 2 && audio.every(item => item.status === 200));
        assert.equal(new Set(audio.map(item => item.path)).size, audio.length, "No reconciled answer replay.");
        await page.waitForFunction(() => document.querySelector(".ellai-composer__sent-message")?.textContent?.trim()
            === "Hello again. Reply with one short greeting.");
        assert.equal(await page.locator(".ellai-composer__response").count(), 1);
        assert.ok(await page.locator(".ellai-composer__response").evaluate(element =>
            parseFloat(getComputedStyle(element).fontSize)) <= 12);
        await page.setViewportSize({ width: 390, height: 844 });
        const status = await page.locator(".ellai-composer__status-row .ellai-status").boundingBox();
        const voice = await page.locator(".ellai-composer__speech-action").boundingBox();
        assert.ok(status && voice && voice.x > status.x && Math.abs((voice.y + voice.height / 2) - (status.y + status.height / 2)) < 5);
        if (process.env.SWIFTAID_COMPOSER_SCREENSHOT)
            await page.locator(".ellai-composer").screenshot({ path: process.env.SWIFTAID_COMPOSER_SCREENSHOT });
        assert.deepEqual(errors, []);
        await page.getByRole("button", { name: "End Ellai session", exact: true }).click();
        const nextSession = page.waitForResponse(response => response.request().method() === "POST"
            && new URL(response.url()).pathname.endsWith("/guest/sessions") && response.ok());
        await page.locator(".ellai-launcher-menu__button").click();
        await page.getByRole("menuitem", { name: "Ask", exact: true }).click();
        const secondSessionId = (await (await nextSession).json()).sessionId;
        assert.notEqual(secondSessionId, sessionId);
        await page.goto(`${process.env.SWIFTAID_GATEWAY_URL ?? "http://localhost:8080"}/ellai/about`);
        await page.getByRole("tab", { name: "Session history", exact: true }).click();
        await page.waitForFunction(() => document.querySelectorAll(".ellai-settings__session").length === 2);
        await page.getByRole("button", { name: "View conversation", exact: true }).last().click();
        await page.waitForFunction(() => document.querySelectorAll(".ellai-settings__turn").length === 3);
        await page.reload();
        await page.getByRole("tab", { name: "Session history", exact: true }).click();
        await page.waitForFunction(() => document.querySelectorAll(".ellai-settings__session").length === 2);
        const historyRoute = "**/api/v1/ellai/guest/sessions?offset=*";
        await page.route(historyRoute, route => route.fulfill({ status: 503, body: "" }));
        await page.getByRole("button", { name: "Refresh sessions", exact: true }).click();
        await page.getByText("Your sessions couldn't be loaded. Please try Refresh sessions.", { exact: true }).waitFor();
        await page.unroute(historyRoute);
        await page.getByRole("button", { name: "Refresh sessions", exact: true }).click();
        await page.waitForFunction(() => document.querySelectorAll(".ellai-settings__session").length === 2);
        const stranger = await browser.newContext();
        const otherHistory = await stranger.request.get(`${process.env.SWIFTAID_GATEWAY_URL ?? "http://localhost:8080"}/api/v1/ellai/guest/sessions`);
        assert.deepEqual((await otherHistory.json()).sessions, []);
        const privateReply = await stranger.request.get(`${process.env.SWIFTAID_GATEWAY_URL ?? "http://localhost:8080"}/api/v1/ellai/guest/sessions/${sessionId}/responses`);
        assert.equal(privateReply.status(), 401);
        await stranger.close();
        assert.deepEqual(errors, []);
        console.log(JSON.stringify({ result: "PASS", sessionId, completionMs: [firstMs, secondMs, thirdMs],
            audioRequests: audio.length, playbackSources: spokenSources, automaticNextReply: true,
            disabledNextReply: true, captionReply: true, statusAndListenSameRow: true,
            restoredSessions: 2, historyTurns: 3, historyFailureRecovery: true, otherGuestDenied: true,
            pageErrors: errors.length }));
    } finally {
        await browser.close();
    }
}

main().catch(error => { console.error(error); process.exitCode = 1; });
