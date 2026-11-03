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
        const result = await page.evaluate(async () => {
            const root = '/api/v1/ellai/guest';
            const post = async (url, body) => {
                const token = (await (await fetch(root + '/antiforgery')).json()).requestToken;
                return fetch(root + url, { method: 'POST', headers: { 'Content-Type': 'application/json', 'X-CSRF-TOKEN': token }, body: JSON.stringify(body) });
            };
            const started = await post('/sessions', { mode: 0, clientRequestId: crypto.randomUUID() });
            if (!started.ok) throw new Error('Session start failed: ' + started.status);
            const session = await started.json();
            const id = crypto.randomUUID();
            const submit = await post(`/sessions/${session.sessionId}/utterances`, {
                clientUtteranceId: id, sessionId: session.sessionId, expectedSessionVersion: session.sessionVersion,
                finalizedText: 'Explain in several paragraphs how SwiftAid handles an intake receipt.', transcriptRef: null,
                inputSource: 'typed', clientEpoch: session.sessionVersion
            });
            if (!submit.ok) throw new Error('Submission failed: ' + submit.status);
            let sawProcessing = false;
            for (let i = 0; i < 100; i++) {
                const feed = await (await fetch(`${root}/sessions/${session.sessionId}/responses?cursor=0&includeProgress=true`)).json();
                const turn = feed.conversation?.find(item => item.clientUtteranceId === id);
                if (turn?.outcome === 'Processing') { sawProcessing = true; break; }
                if (turn?.completedAtUtc) throw new Error('Turn completed before cancellation could be exercised.');
                await new Promise(resolve => setTimeout(resolve, 100));
            }
            if (!sawProcessing) throw new Error('Turn did not reach processing.');
            await new Promise(resolve => setTimeout(resolve, 1500));
            const current = await (await fetch(`${root}/sessions/${session.sessionId}`)).json();
            const cancelled = await post(`/sessions/${session.sessionId}/cancel`, { expectedSessionVersion: current.sessionVersion });
            if (!cancelled.ok) throw new Error('Cancellation failed: ' + cancelled.status);
            for (let i = 0; i < 120; i++) {
                const feed = await (await fetch(`${root}/sessions/${session.sessionId}/responses?cursor=0&includeProgress=true`)).json();
                const turn = feed.conversation?.find(item => item.clientUtteranceId === id);
                if (turn?.completedAtUtc) return { outcome: turn.outcome, answer: turn.assistantText,
                    answerChunks: feed.responses?.filter(item => item.clientUtteranceId === id && !item.progress && item.text).length ?? 0 };
                await new Promise(resolve => setTimeout(resolve, 500));
            }
            throw new Error('Cancelled turn did not reach a terminal outcome.');
        });
        assert.equal(result.outcome, 'Cancelled');
        assert.ok(!result.answer);
        assert.equal(result.answerChunks, 0);
        console.log('PASS: live Gateway turn cancelled after processing began; no answer was committed or published.');
    } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
