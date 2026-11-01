const assert = require("node:assert/strict");
const { chromium } = require("playwright");

// Opt-in, synthetic text through the real Gateway and MMS-TTS. Observes native
// Web Audio playback; it does not establish loudspeaker quality or echo rejection.
async function main() {
    assert.equal(process.env.SWIFTAID_RUN_LIVE_ELLAI, "1", "Live test opt-in required.");
    const browser = await chromium.launch({ headless: true });
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
        assert.deepEqual(errors, []);
        console.log(JSON.stringify({ result: "PASS", sessionId, completionMs: [firstMs, secondMs, thirdMs],
            audioRequests: audio.length, playbackSources: spokenSources, automaticNextReply: true,
            disabledNextReply: true, pageErrors: errors.length }));
        await page.getByRole("button", { name: "End Ellai session", exact: true }).click();
    } finally {
        await browser.close();
    }
}

main().catch(error => { console.error(error); process.exitCode = 1; });
