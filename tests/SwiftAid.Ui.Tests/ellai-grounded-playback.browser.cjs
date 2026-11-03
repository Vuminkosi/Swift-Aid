const assert = require("node:assert/strict");
const { chromium } = require(process.env.SWIFTAID_PLAYWRIGHT_PACKAGE || "playwright");

// Synthetic guest input against the configured live Gateway, model and speech service.
async function main() {
    assert.equal(process.env.SWIFTAID_RUN_LIVE_ELLAI, "1", "Live test opt-in required.");
    const browser = await chromium.launch({ headless: true });
    const page = await browser.newPage({ viewport: { width: 390, height: 844 } });
    const errors = [], audio = [];
    let feed, early;
    const prompt = "Use ellai_productfacts with topic receipt. Copy the complete receipt fact exactly, cite its matching evidence, and stream that exact fact.";
    await page.addInitScript(() => {
        window.ellaiGroundedPlayback = { started: 0, ended: 0, currentSequence: null, starts: [] };
        const fetchOriginal = window.fetch.bind(window);
        window.fetch = async (...args) => {
            const response = await fetchOriginal(...args);
            const match = new URL(response.url).pathname.match(/\/responses\/(\d+)\/audio$/);
            if (match && response.body) {
                const getReader = response.body.getReader.bind(response.body);
                response.body.getReader = (...readerArgs) => {
                    const reader = getReader(...readerArgs), read = reader.read.bind(reader);
                    reader.read = async (...readArgs) => {
                        const result = await read(...readArgs);
                        if (!result.done) window.ellaiGroundedPlayback.currentSequence = Number(match[1]);
                        return result;
                    };
                    return reader;
                };
            }
            return response;
        };
        const create = AudioContext.prototype.createBufferSource;
        AudioContext.prototype.createBufferSource = function (...args) {
            const source = create.apply(this, args), start = source.start;
            source.start = function (...values) {
                window.ellaiGroundedPlayback.started++;
                window.ellaiGroundedPlayback.starts.push({ sequence: window.ellaiGroundedPlayback.currentSequence,
                    peak: source.buffer ? source.buffer.getChannelData(0).reduce((value, sample) => Math.max(value, Math.abs(sample)), 0) : 0 });
                return start.apply(this, values);
            };
            source.addEventListener("ended", () => window.ellaiGroundedPlayback.ended++);
            return source;
        };
    });
    page.on("pageerror", error => errors.push(error.message));
    page.on("response", async response => {
        try {
            const path = new URL(response.url()).pathname;
            if (path.endsWith("/responses") && response.ok()) {
                const current = await response.json();
                feed = current;
                const turn = current.conversation?.find(item => item.userText === prompt);
                const chunk = current.responses?.find(item => item.clientUtteranceId === turn?.clientUtteranceId &&
                    !item.isFinal && !item.progress && item.text?.startsWith("An intake receipt confirms"));
                if (turn && !turn.completedAtUtc && chunk) early ??= chunk;
            }
            if (/\/responses\/\d+\/audio$/.test(path)) audio.push({ path, status: response.status() });
        } catch { /* A closing page can discard an in-flight response. */ }
    });
    async function send(text) {
        await page.getByRole("textbox", { name: "Message Ellai" }).fill(text);
        await page.getByRole("button", { name: "Send to Ellai", exact: true }).click();
        const deadline = Date.now() + 120000;
        while (Date.now() < deadline) {
            const turn = feed?.conversation?.find(item => item.userText === text && item.completedAtUtc);
            if (turn) { assert.equal(turn.outcome, "Completed"); return turn; }
            await page.waitForTimeout(100);
        }
        throw new Error("No durable terminal turn within 120 seconds.");
    }
    try {
        await page.goto(process.env.SWIFTAID_GATEWAY_URL ?? "http://localhost:8080");
        await page.getByRole("button", { name: "Continue with essential cookies", exact: true }).click();
        await page.locator(".ellai-launcher-menu__button").click();
        await page.getByRole("menuitem", { name: "Ask", exact: true }).click();
        await send("Hello. Reply with one short greeting.");
        await page.getByRole("button", { name: "Listen to Ellai", exact: true }).click();
        await page.waitForFunction(() => window.ellaiGroundedPlayback.started > 0 &&
            window.ellaiGroundedPlayback.started === window.ellaiGroundedPlayback.ended, null, { timeout: 60000 });
        const turn = await send(prompt);
        assert.ok(early, "No grounded segment was observed before durable completion.");
        assert.equal(turn.assistantText, early.text);
        const deadline = Date.now() + 60000;
        while (!audio.some(item => item.path.endsWith(`/responses/${early.sequence}/audio`)) && Date.now() < deadline)
            await page.waitForTimeout(100);
        assert.ok(audio.some(item => item.path.endsWith(`/responses/${early.sequence}/audio`) && item.status === 200));
        await page.waitForFunction(sequence => window.ellaiGroundedPlayback.starts.some(item => item.sequence === sequence && item.peak > 0) &&
            window.ellaiGroundedPlayback.started === window.ellaiGroundedPlayback.ended, early.sequence, { timeout: 60000 });
        assert.deepEqual(errors, []);
        console.log(JSON.stringify({ result: "PASS", earlySequence: early.sequence, audio,
            playback: await page.evaluate(() => window.ellaiGroundedPlayback) }));
    } finally { await browser.close(); }
}

main().catch(error => { console.error(error); process.exitCode = 1; });
