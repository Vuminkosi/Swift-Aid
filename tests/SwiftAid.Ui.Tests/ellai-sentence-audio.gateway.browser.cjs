const assert = require('node:assert/strict');
const { chromium } = require(process.env.SWIFTAID_PLAYWRIGHT_PACKAGE || 'playwright');

if (process.env.SWIFTAID_RUN_LIVE_PROVIDER !== '1') {
    console.log('NOT VERIFIED: set SWIFTAID_RUN_LIVE_PROVIDER=1 for synthetic provider inference.');
    process.exit(0);
}

(async () => {
    const browser = await chromium.launch({ headless: true,
        ...(process.env.SWIFTAID_CHROMIUM_PATH ? { executablePath: process.env.SWIFTAID_CHROMIUM_PATH } : {}) });
    try {
        const page = await browser.newPage();
        await page.goto(process.env.SWIFTAID_TEST_GATEWAY || 'http://localhost:8080');
        const result = await page.evaluate(async () => {
            const root = '/api/v1/ellai/guest';
            const token = (await (await fetch(root + '/antiforgery')).json()).requestToken;
            const post = (url, body) => fetch(root + url, { method: 'POST',
                headers: { 'Content-Type': 'application/json', 'X-CSRF-TOKEN': token }, body: JSON.stringify(body) });
            const start = await post('/sessions', { mode: 0, clientRequestId: crypto.randomUUID() });
            if (!start.ok) throw new Error('Session creation failed: ' + start.status);
            const session = await start.json();
            const id = crypto.randomUUID();
            const started = performance.now();
            const chunks = new Map();
            const requests = new Map();
            let firstTextMs = null;
            const submission = await post(`/sessions/${session.sessionId}/utterances`, {
                clientUtteranceId: id, sessionId: session.sessionId, expectedSessionVersion: session.sessionVersion,
                finalizedText: 'Please read these three sentences aloud: Welcome to SwiftAid. Your observations help your community. You decide when to submit your report.',
                inputSource: 'typed', clientEpoch: session.sessionVersion,
                languages: { inputLanguage: 'eng', outputLanguage: 'eng', englishVoiceId: 'af_heart' }
            });
            if (!submission.ok) throw new Error('Submission failed: ' + submission.status);
            for (let attempt = 0; attempt < 300; attempt++) {
                const response = await fetch(`${root}/sessions/${session.sessionId}/responses?cursor=0&includeProgress=true`);
                if (!response.ok) throw new Error('Response polling failed: ' + response.status);
                const feed = await response.json();
                for (const chunk of feed.responses || []) {
                    if (chunk.clientUtteranceId !== id || chunk.progress || chunk.isReconciliation || !chunk.text || chunks.has(chunk.sequence)) continue;
                    firstTextMs ??= Math.round(performance.now() - started);
                    chunks.set(chunk.sequence, chunk.text);
                    requests.set(chunk.sequence, (async () => {
                        const audio = await fetch(`${root}/sessions/${session.sessionId}/responses/${chunk.sequence}/audio?playbackId=${crypto.randomUUID()}`);
                        const bytes = (await audio.arrayBuffer()).byteLength;
                        return { sequence: chunk.sequence, status: audio.status, bytes,
                            sampleRate: audio.headers.get('X-SwiftAid-Sample-Rate'),
                            voice: audio.headers.get('X-SwiftAid-Voice-Id') };
                    })());
                }
                const final = feed.responses?.find(item => item.clientUtteranceId === id && item.isReconciliation);
                if (final) {
                    const audio = await Promise.all(requests.values());
                    const normalized = text => text.replace(/\s+/g, ' ').trim();
                    return { sessionId: session.sessionId, outcome: final.outcome, firstTextMs,
                        elapsedMs: Math.round(performance.now() - started), chunkCount: chunks.size, audio,
                        completeText: normalized([...chunks.values()].join(' ')) === normalized(final.text) };
                }
                await new Promise(resolve => setTimeout(resolve, 200));
            }
            throw new Error('No reconciled response within 60 seconds.');
        });
        console.log(JSON.stringify(result));
        assert.equal(result.outcome, 'Completed');
        assert(result.chunkCount >= 2, 'Exercise multiple streamed sentences, not only a final response.');
        assert(result.completeText, 'Streamed text must cover the complete validated response.');
        for (const audio of result.audio) {
            assert.equal(audio.status, 200);
            assert(audio.bytes > 1000 && audio.bytes % 2 === 0);
            assert.equal(audio.sampleRate, '24000');
            assert.equal(audio.voice, 'af_heart');
        }
        console.log('PASS: actual Gateway/Muse/TTS; every streamed sentence has complete PCM. HTTP audio validation, not audible-device acceptance.');
    } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
