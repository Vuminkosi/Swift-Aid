const assert = require("node:assert/strict");
const { chromium } = require("playwright");

// Real Gateway/provider/MMS check with synthetic public requests. Hardware
// acoustics and audible quality require separate device validation.
async function main() {
    assert.equal(process.env.SWIFTAID_RUN_LIVE_ELLAI, "1");
    const browser = await chromium.launch({ headless: true });
    const page = await browser.newPage({ viewport: { width: 390, height: 844 } });
    const errors = [], progress = new Map(), audio = [], timings = [];
    let conversation = [], sessionId;
    await page.addInitScript(() => {
        window.ellaiAudio = { started: 0, ended: 0 };
        const create = AudioContext.prototype.createBufferSource;
        AudioContext.prototype.createBufferSource = function (...args) {
            const source = create.apply(this, args), start = source.start;
            source.start = function (...values) { window.ellaiAudio.started++; return start.apply(this, values); };
            source.addEventListener("ended", () => window.ellaiAudio.ended++);
            return source;
        };
    });
    page.on("pageerror", error => errors.push(error.message));
    page.on("response", async response => {
        try {
            const path = new URL(response.url()).pathname;
            if (path.endsWith("/responses") && response.ok()) {
                const feed = await response.json();
                conversation = feed.conversation ?? [];
                for (const row of feed.responses ?? []) if (row.progress)
                    progress.set(row.progress.eventId, { ...row, receivedAt: Date.now() });
            }
            if (path.endsWith("/guest/sessions") && response.request().method() === "POST" && response.ok())
                sessionId = (await response.json()).sessionId;
            if (/\/responses\/\d+\/audio$/.test(path)) audio.push({ sequence: Number(path.split("/").at(-2)), status: response.status(), at: Date.now() });
        } catch { /* Closed-page polls are not proof of delivery. */ }
    });
    async function send(text, inspectThinking = false) {
        await page.getByRole("textbox", { name: "Message Ellai" }).fill(text);
        const start = Date.now();
        await page.getByRole("button", { name: "Send to Ellai", exact: true }).click();
        if (inspectThinking) {
            const label = page.locator(".ellai-status__thinking");
            await label.waitFor();
            const light = await label.evaluate(node => ({ color: getComputedStyle(node).color,
                weight: getComputedStyle(node.querySelector(".ellai-status__dot")).fontWeight }));
            assert.equal(light.color, "rgb(124, 58, 237)");
            assert.equal(light.weight, "900");
            await page.evaluate(() => document.documentElement.dataset.colorMode = "dark");
            assert.equal(await label.evaluate(node => getComputedStyle(node).color), "rgb(196, 161, 255)");
            await page.emulateMedia({ reducedMotion: "reduce" });
            assert.equal(await label.locator(".ellai-status__dot").first().evaluate(node => getComputedStyle(node).animationName), "none");
            await page.emulateMedia({ reducedMotion: "no-preference" });
            await page.evaluate(() => document.documentElement.dataset.colorMode = "light");
            if (process.env.SWIFTAID_THINKING_SCREENSHOT)
                await page.locator(".ellai-composer").screenshot({ path: process.env.SWIFTAID_THINKING_SCREENSHOT });
        }
        while (Date.now() - start < 120000) {
            const turn = conversation.find(item => item.userText === text);
            if (turn && ["Completed", "TerminalFailure", "Cancelled"].includes(turn.outcome)) {
                timings.push({ turnId: turn.clientUtteranceId, outcome: turn.outcome, totalMs: Date.now() - start,
                    acknowledgementMs: [...progress.values()].find(item => item.clientUtteranceId === turn.clientUtteranceId
                        && item.progress.state === "Acknowledgement")?.receivedAt - start });
                assert.equal(turn.outcome, "Completed");
                return turn;
            }
            await page.waitForTimeout(100);
        }
        throw new Error("No durable result within 120 seconds.");
    }
    try {
        await page.goto(process.env.SWIFTAID_GATEWAY_URL ?? "http://localhost:8080");
        await page.getByRole("button", { name: "Continue with essential cookies", exact: true }).click();
        await page.locator(".ellai-launcher-menu__button").click();
        await page.getByRole("menuitem", { name: "Ask", exact: true }).click();
        const greeting = await send("Hello. Reply with one short greeting.", true);
        assert.equal([...progress.values()].filter(row => row.clientUtteranceId === greeting.clientUtteranceId).length, 0);
        await page.getByRole("button", { name: "Listen to Ellai", exact: true }).click();
        await page.waitForFunction(() => window.ellaiAudio.started > 0 && window.ellaiAudio.started === window.ellaiAudio.ended);
        const result = await send("Check the verified SwiftAid product facts and tell me how a guest can submit a report. Keep the answer short.");
        await page.waitForTimeout(500);
        const acks = [...progress.values()].filter(row => row.clientUtteranceId === result.clientUtteranceId
            && row.progress.state === "Acknowledgement");
        assert.equal(acks.length, 1, "A real provider tool call must produce exactly one acknowledgement.");
        assert.ok(audio.some(item => item.sequence === acks[0].sequence && item.status === 200), "Acknowledgement must use Gateway PCM.");
        assert.ok(!result.assistantText.includes(acks[0].text), "Progress must not contaminate the saved answer.");
        const legacy = await page.request.get(`${process.env.SWIFTAID_GATEWAY_URL ?? "http://localhost:8080"}/api/v1/ellai/guest/sessions/${sessionId}/responses?cursor=0`);
        assert.ok((await legacy.json()).responses.every(row => !row.progress), "Legacy clients must not receive progress as answers.");
        await page.waitForFunction(() => window.ellaiAudio.started === window.ellaiAudio.ended, null, { timeout: 60000 });
        console.log(JSON.stringify({ stage: "conversation", sessionId, timings, acknowledgementSequence: acks[0].sequence, audio }));
        const audioCount = audio.length;
        await page.getByRole("button", { name: "End Ellai session", exact: true }).click();
        await page.goto(`${process.env.SWIFTAID_GATEWAY_URL ?? "http://localhost:8080"}/ellai/about`);
        await page.getByRole("tab", { name: "Session history", exact: true }).click();
        await page.getByRole("button", { name: "View conversation", exact: true }).first().click();
        await page.getByRole("dialog").waitFor();
        await page.waitForFunction(() => document.querySelectorAll(".ellai-conversation__turn").length === 2);
        await page.reload();
        await page.waitForTimeout(1500);
        assert.equal(audio.length, audioCount, "Refreshing history must not replay acknowledgement speech.");
        assert.deepEqual(errors, []);
        console.log(JSON.stringify({ result: "PASS", sessionId, timings, acknowledgements: acks.length,
            audioRequests: audio, thinkingPurple: true, dotsWeight: 900, reducedMotion: true,
            restoredTurns: 2, errors }));
    } finally { await browser.close(); }
}
main().catch(error => { console.error(error); process.exitCode = 1; });
