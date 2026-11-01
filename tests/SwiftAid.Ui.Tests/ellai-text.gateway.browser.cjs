const assert = require('node:assert/strict');
const { chromium } = require(process.env.SWIFTAID_PLAYWRIGHT_PACKAGE || 'playwright');

if (process.env.SWIFTAID_RUN_LIVE_PROVIDER !== '1') {
    console.log('SKIP: set SWIFTAID_RUN_LIVE_PROVIDER=1 to authorize synthetic provider inference.');
    process.exit(0);
}

(async () => {
    const browser = await chromium.launch({ headless: true });
    try {
        const page = await browser.newPage();
        await page.goto(process.env.SWIFTAID_TEST_GATEWAY || 'http://localhost:8080');
        const result = await page.evaluate(async outputLanguage => {
            const root = '/api/v1/ellai/guest';
            const post = async (url, body) => {
                const token = (await (await fetch(root + '/antiforgery')).json()).requestToken;
                return fetch(root + url, { method: 'POST', headers: { 'Content-Type': 'application/json', 'X-CSRF-TOKEN': token }, body: JSON.stringify(body) });
            };
            const start = await post('/sessions', { mode: 0, clientRequestId: crypto.randomUUID() });
            if (!start.ok) throw new Error('Session start failed: ' + start.status);
            const session = await start.json();
            const id = crypto.randomUUID();
            const started = performance.now();
            let firstTextMs = null;
            try {
                const response = await post(`/sessions/${session.sessionId}/utterances`, {
                    clientUtteranceId: id, sessionId: session.sessionId, expectedSessionVersion: session.sessionVersion,
                    finalizedText: 'What does an intake receipt mean?', transcriptRef: null, inputSource: 'typed', clientEpoch: session.sessionVersion,
                    languages: { inputLanguage: 'eng', outputLanguage }
                });
                if (!response.ok) throw new Error('Submission failed: ' + response.status);
                const admissionMs = Math.round(performance.now() - started);
                for (let i = 0; i < 90; i++) {
                    const feed = await (await fetch(`${root}/sessions/${session.sessionId}/responses?cursor=0&includeProgress=true`)).json();
                    if (firstTextMs === null && feed.responses?.some(r => r.clientUtteranceId === id && r.text && !r.progress))
                        firstTextMs = Math.round(performance.now() - started);
                    const turn = feed.conversation?.find(t => t.clientUtteranceId === id && t.completedAtUtc);
                    if (turn) return { sessionId: session.sessionId, utteranceId: id, admissionMs, firstTextMs,
                        completedMs: Math.round(performance.now() - started), outcome: turn.outcome, responseLength: turn.assistantText?.length ?? 0 };
                    await new Promise(r => setTimeout(r, 2000));
                }
                throw new Error('No durable terminal outcome within 180 seconds.');
            } finally {
                const current = await (await fetch(`${root}/sessions/${session.sessionId}`)).json();
                await post(`/sessions/${session.sessionId}/cancel`, { expectedSessionVersion: current.sessionVersion });
            }
        }, process.env.SWIFTAID_TEST_OUTPUT_LANGUAGE || 'eng');
        console.log(JSON.stringify(result));
        assert.equal(result.outcome, 'Completed');
        assert(result.responseLength > 0);
        await page.reload();
        const restored = await page.evaluate(async ({ sessionId, utteranceId }) => {
            const feed = await (await fetch(`/api/v1/ellai/guest/sessions/${sessionId}/responses?cursor=0`)).json();
            return feed.conversation?.find(t => t.clientUtteranceId === utteranceId);
        }, result);
        assert.equal(restored?.outcome, 'Completed');
        assert.equal(restored.assistantText.length, result.responseLength);
        console.log('PASS: actual Gateway/provider durable text outcome and owner-cookie recovery after browser reload. HTTP feed validation, not rendered composer acceptance.');
    } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
