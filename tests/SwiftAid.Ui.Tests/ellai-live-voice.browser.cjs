const assert = require("node:assert/strict");
const { chromium } = require("playwright");

async function main() {
    assert.equal(process.env.SWIFTAID_RUN_LIVE_ELLAI, "1");
    assert.ok(process.env.SWIFTAID_SYNTHETIC_AUDIO, "Supply a synthetic speech WAV, never private audio.");
    const browser = await chromium.launch({ headless: true, args: [
        "--use-fake-device-for-media-stream", "--use-fake-ui-for-media-stream",
        `--use-file-for-fake-audio-capture=${process.env.SWIFTAID_SYNTHETIC_AUDIO}`,
    ] });
    const context = await browser.newContext({ permissions: ["microphone"] });
    const page = await context.newPage();
    const errors = [];
    let frames = 0;
    let lastVoice;
    let conversation = [];
    let submitted = 0;
    let sessionId;
    let firstCompletedAt;
    let responseAudioRequests = 0;
    let blockedVoicePolls = 0;
    const voiceStates = [];
    const expectedTurns = Number(process.env.SWIFTAID_EXPECTED_VOICE_TURNS ?? 5);
    assert.ok(Number.isInteger(expectedTurns) && expectedTurns > 0);
    page.on("pageerror", error => errors.push(error.message));
    page.on("request", request => {
        if (new URL(request.url()).pathname.endsWith("/utterances") && request.method() === "POST") submitted++;
    });
    // Voice status is a UI notification, never a prerequisite for admission.
    // Hold it unavailable for the whole conversation to prove that separation.
    if (process.env.SWIFTAID_BLOCK_VOICE_STATUS === "1") {
        await page.route("**/voice/state*", async route => {
            blockedVoicePolls++;
            await route.fulfill({ status: 503, body: "" });
        });
    }
    page.on("websocket", socket => {
        if (socket.url().includes("/voice/live-stream")) socket.on("framesent", () => frames++);
    });
    page.on("response", async response => {
        try {
        if (!response.ok()) return;
        const url = new URL(response.url());
        if (url.pathname.endsWith("/voice/state")) {
            lastVoice = await response.json();
            sessionId ??= lastVoice.sessionId;
            if (voiceStates.at(-1)?.state !== lastVoice.state) voiceStates.push({
                state: lastVoice.state, speechMs: lastVoice.confirmedSpeechMilliseconds,
                silenceMs: lastVoice.silenceMilliseconds,
            });
        }
        if (url.pathname.endsWith("/guest/sessions") && response.request().method() === "POST") sessionId = (await response.json()).sessionId;
        if (/\/responses\/\d+\/audio$/.test(url.pathname)) responseAudioRequests++;
        if (url.pathname.endsWith("/responses")) {
            conversation = (await response.json()).conversation ?? [];
            if (!firstCompletedAt && conversation.some(turn => turn.outcome === "Completed")) firstCompletedAt = Date.now();
        }
        } catch { /* A closed page may discard response bodies; outcome assertions still apply. */ }
    });
    await page.addInitScript(() => {
        window.ellaiTestCaptures = [];
        const original = navigator.mediaDevices.getUserMedia.bind(navigator.mediaDevices);
        navigator.mediaDevices.getUserMedia = async constraints => {
            const stream = await original(constraints);
            window.ellaiTestCaptures.push(stream);
            return stream;
        };
    });
    try {
        await page.goto(process.env.SWIFTAID_GATEWAY_URL ?? "http://localhost:8080");
        await page.getByRole("button", { name: "Continue with essential cookies", exact: true }).click();
        await page.locator(".ellai-launcher-menu__button").click();
        await page.getByRole("menuitem", { name: "Ask", exact: true }).click();
        await page.getByRole("button", { name: "Start live voice question", exact: true }).click();
        const started = Date.now();
        const deadline = started + Number(process.env.SWIFTAID_VOICE_TEST_MS ?? 140000);
        while (Date.now() < deadline && conversation.filter(turn => turn.outcome === "Completed").length < expectedTurns) {
            await page.waitForTimeout(1000);
        }
        const capture = await page.evaluate(() => ({
            starts: window.ellaiTestCaptures.length,
            tracks: window.ellaiTestCaptures.flatMap(stream => stream.getAudioTracks().map(track => ({
                state: track.readyState, settings: track.getSettings(),
            }))),
        }));
        const report = {
            sessionId, elapsedMs: Date.now() - started, frames, submitted, blockedVoicePolls,
            firstCompletedMs: firstCompletedAt ? firstCompletedAt - started : null,
            responseAudioRequests,
            voiceState: lastVoice?.state, voiceEpoch: lastVoice?.epoch, voiceStates,
            completedTurns: conversation.filter(turn => turn.outcome === "Completed").length,
            outcomes: conversation.map(turn => turn.outcome),
            captureStarts: capture.starts,
            trackStates: capture.tracks.map(track => track.state),
            processing: capture.tracks.map(track => ({
                aec: track.settings.echoCancellation, ns: track.settings.noiseSuppression,
                agc: track.settings.autoGainControl, sampleRate: track.settings.sampleRate,
            })), pageErrors: errors,
        };
        console.log(JSON.stringify(report));
        assert.equal(report.captureStarts, 1);
        assert.equal(submitted, 0, "Confirmed voice turns must enter the outbox without client resubmission.");
        assert.ok(report.trackStates.every(state => state === "live"));
        assert.equal(report.completedTurns, expectedTurns);
        if (process.env.SWIFTAID_EXPECTED_FIRST_WORDS) {
            assert.ok(conversation[0]?.userText?.toLowerCase().includes(process.env.SWIFTAID_EXPECTED_FIRST_WORDS.toLowerCase()),
                "The first spoken utterance must be present in durable history.");
        }
        assert.deepEqual(errors, []);
        await page.getByRole("button", { name: "Stop the live microphone session", exact: true }).click();
        await page.getByRole("button", { name: "End Ellai session", exact: true }).click();
    } finally {
        await context.close();
        await browser.close();
    }
}

main().catch(error => { console.error(error); process.exitCode = 1; });
