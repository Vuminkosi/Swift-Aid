const assert = require('node:assert/strict');
const { chromium } = require(process.env.SWIFTAID_PLAYWRIGHT_PACKAGE || 'playwright');

(async () => {
    const browser = await chromium.launch({ headless: true });
    try {
        const context = await browser.newContext();
        const page = await context.newPage();
        const errors = [];
        page.on('pageerror', e => errors.push(e.message));
        await page.goto(process.env.SWIFTAID_TEST_GATEWAY || 'http://localhost:8080');
        const result = await page.evaluate(async () => {
            const root = '/api/v1/ellai/guest';
            const token = async () => (await (await fetch(root + '/antiforgery')).json()).requestToken;
            const post = async (url, body, csrf = true) => fetch(root + url, {
                method: 'POST', headers: { 'Content-Type': 'application/json', ...(csrf ? { 'X-CSRF-TOKEN': await token() } : {}) },
                body: JSON.stringify(body)
            });
            const started = await post('/sessions', { mode: 0, clientRequestId: crypto.randomUUID() });
            if (!started.ok) return { startStatus: started.status };
            const session = await started.json();
            const module = await import('/_content/SwiftAid.UI/js/ellai-ui-observations.js');
            const report = { ...(module.collect()), sessionId: session.sessionId };
            const accepted = await post(`/sessions/${session.sessionId}/ui-observations`, report);
            const rejected = await post(`/sessions/${session.sessionId}/ui-observations`, report, false);
            const invalid = await post(`/sessions/${session.sessionId}/ui-observations`, { ...report, controlStates: [{ controlId: 'not-mounted', state: 4 }] });
            const cancelled = await post(`/sessions/${session.sessionId}/cancel`, { expectedSessionVersion: session.sessionVersion });
            return { startStatus: started.status, accepted: accepted.status, noCsrf: rejected.status, invalid: invalid.status,
                cancelled: cancelled.status, sessionId: session.sessionId };
        });
        assert.equal(result.startStatus, 200);
        assert.equal(result.accepted, 200);
        assert.equal(result.noCsrf, 400);
        assert.equal(result.invalid, 400);
        assert.equal(result.cancelled, 200);
        const other = await browser.newContext();
        const denied = await other.request.post((process.env.SWIFTAID_TEST_GATEWAY || 'http://localhost:8080') +
            `/api/v1/ellai/guest/sessions/${result.sessionId}/ui-observations`, { data: {} });
        assert(denied.status() === 400 || denied.status() === 401);
        assert.deepEqual(errors, []);
        console.log('PASS: deployed Gateway guest session, shipped JS module, empty observation, invalid state and CSRF rejection, cancellation; no provider inference requested.');
    } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
