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
    const chunks = new Map();
    let feed;
    let sessionId;
    await page.addInitScript(() => {
        window.ellaiPlaybackProbe = { started: 0, ended: 0, starts: [] };
        const create = AudioContext.prototype.createBufferSource;
        AudioContext.prototype.createBufferSource = function (...args) {
            const source = create.apply(this, args);
            const start = source.start;
            source.start = function (...startArgs) {
                window.ellaiPlaybackProbe.started++;
                window.ellaiPlaybackProbe.starts.push({ state: source.context.state,
                    duration: source.buffer?.duration, sampleRate: source.buffer?.sampleRate,
                    peak: source.buffer ? source.buffer.getChannelData(0).reduce((p, v) => Math.max(p, Math.abs(v)), 0) : 0 });
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
            if (path.endsWith("/responses") && response.ok()) {
                feed = await response.json();
                for (const chunk of feed.responses || []) chunks.set(chunk.sequence, chunk);
            }
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
        const secondMs = await send("Please read these three sentences aloud: Welcome to SwiftAid. Your observations help your community. You decide when to submit your report.");
        await playedAfter(firstSources);
        const secondTurn = feed.conversation.find(turn => turn.userText.startsWith("Please read these three"));
        const expected = [...chunks.values()].filter(chunk => chunk.clientUtteranceId === secondTurn.clientUtteranceId
            && !chunk.progress && !chunk.isReconciliation && chunk.text);
        assert.ok(expected.length >= 2, "Exercise multiple sentence chunks.");
        const playbackDeadline = Date.now() + 60000;
        const allSentencesRequested = () => expected.every(chunk =>
            audio.some(item => item.path.endsWith(`/responses/${chunk.sequence}/audio`)));
        while (!allSentencesRequested() && Date.now() < playbackDeadline) await page.waitForTimeout(100);
        assert.ok(allSentencesRequested(), "Every sentence must reach the browser player.");
        // A source is one network buffer, not a sentence. Between buffers the
        // ended counter can briefly catch up, so also await the playback promise.
        await page.waitForFunction(() => window.ellaiPlaybackProbe.started === window.ellaiPlaybackProbe.ended,
            null, { timeout: 60000 });
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
        assert.deepEqual(errors, []);
        console.log(JSON.stringify({ result: "PASS", audio, playback: await page.evaluate(() => window.ellaiPlaybackProbe), multiSentenceVoiceOn: true, voiceOff: true }));
    } catch (error) {
        console.error(JSON.stringify({ audio, errors, playback: await page.evaluate(() => window.ellaiPlaybackProbe) }));
        throw error;
    } finally { await browser.close(); }
}
main().catch(error => { console.error(error); process.exitCode = 1; });
