// Real Gateway/provider acceptance using explicitly seeded synthetic demo records.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const { chromium } = require(process.env.SWIFTAID_PLAYWRIGHT_PACKAGE || 'playwright');
if (process.env.SWIFTAID_RUN_LIVE_PROVIDER !== '1') throw Error('Set SWIFTAID_RUN_LIVE_PROVIDER=1.');

(async () => {
    const fixtures = JSON.parse(fs.readFileSync(process.env.SWIFTAID_CASE_FIXTURE_RECEIPTS, 'utf8'));
    const latestManzini = fixtures.filter(item => item.city === 'Manzini').at(-1).caseId;
    const browser = await chromium.launch({ headless: true, executablePath: process.env.SWIFTAID_CHROMIUM_PATH });
    const checks = [
        ['count', 'How many cases were reported in Manzini region, Eswatini, for the past week?', /2 reported case records/],
        ['categories', 'What are the common cases in Mbabane, Eswatini, across all recorded dates?', /Cough|Headache/],
        ['symptoms', 'What are the most common reported symptoms in Manzini region, Eswatini, across all recorded dates?', /"Cough": 2 case/],
        ['classified', 'How many cases are classified in Mbabane, Eswatini, across all recorded dates?', /Classified: 0; unclassified: 1/],
        ['latest', 'Open the last reported case in Manzini region, Eswatini.', null],
        ['explain', 'Explain this case to me.', /case|report|classif/i],
        ['siphofaneni', 'How many cases are in siphofaneni', /0 reported case records/],
        ['last', 'Show me the last reported case', /case|report/i]
    ];
    try {
        for (const [name, question, expected] of checks) {
            if (process.env.SWIFTAID_CASE_CHECK && process.env.SWIFTAID_CASE_CHECK !== name) continue;
            const context = await browser.newContext({ storageState: process.env.SWIFTAID_AUTH_STATE,
                viewport: { width: Number(process.env.SWIFTAID_VIEWPORT_WIDTH || 1280), height: 900 } });
            const page = await context.newPage();
            let turn;
            const errors = [], audio = [];
            page.on('pageerror', error => errors.push(error.message));
            page.on('response', async response => {
                try {
                    if (new URL(response.url()).pathname.endsWith('/responses') && response.ok()) {
                        const feed = await response.json();
                        turn = feed.conversation?.find(item => item.userText === question && item.completedAtUtc) || turn;
                    }
                    if (/\/responses\/\d+\/audio$/.test(new URL(response.url()).pathname) && response.ok())
                        audio.push((await response.body()).length);
                } catch { /* Navigation may dispose a completed response body. */ }
            });
            try {
                await page.goto((process.env.SWIFTAID_TEST_GATEWAY || 'http://localhost:8080') +
                    (name === 'explain' ? `/case/${latestManzini}` : '/dashboard'));
                const cookies = page.getByRole('button', { name: 'Continue with essential cookies', exact: true });
                if (await cookies.isVisible()) await cookies.click();
                const ask = page.getByRole('menuitem', { name: 'Ask', exact: true });
                // Server prerender can be replaced during the first interaction.
                // Wait for the interactive menu's open state, never force a click.
                for (let attempt = 0; attempt < 3; attempt++) {
                    await page.locator('.ellai-launcher-menu__button').click();
                    try {
                        await page.waitForFunction(() => {
                            const item = [...document.querySelectorAll('[role=menuitem]')].find(element => element.textContent.trim() === 'Ask');
                            return item && getComputedStyle(item).pointerEvents !== 'none';
                        }, null, { timeout: 3000 });
                        break;
                    } catch { if (attempt === 2) throw Error('Ellai actions did not become interactive.'); }
                }
                await ask.click();
                await page.getByRole('button', { name: 'Turn off Ellai voice', exact: true }).waitFor();
                await page.getByRole('textbox', { name: 'Message Ellai' }).fill(question);
                const submittedAt = Date.now();
                await page.getByRole('button', { name: 'Send to Ellai', exact: true }).click();
                const deadline = Date.now() + 150000;
                while (!turn && Date.now() < deadline) {
                    // Blazor Server invokes the client in-process, so its feed need
                    // not appear in browser network events. Read the owned feed.
                    const sessions = await (await context.request.get('http://localhost:8080/api/v1/ellai/sessions')).json();
                    for (const session of sessions.sessions.slice(0, 3)) {
                        const feed = await (await context.request.get(`http://localhost:8080/api/v1/ellai/sessions/${session.sessionId}/responses`)).json();
                        turn = feed.conversation?.find(item => item.userText === question &&
                            Date.parse(item.completedAtUtc) >= submittedAt - 5000) || turn;
                        if (turn) break;
                    }
                    if (!turn) await page.waitForTimeout(1000);
                }
                assert(turn, name + ': no terminal response');
                console.log(JSON.stringify({ check: name, outcome: turn.outcome, text: turn.assistantText, proposals: turn.uiProposals }));
                assert.equal(turn.outcome, 'Completed', name);
                if (expected) {
                    assert.match(turn.assistantText, expected);
                    await page.waitForFunction(pattern => {
                        const response = document.querySelector('.ellai-composer__response');
                        return response && new RegExp(pattern, 'i').test(response.textContent);
                    }, expected.source, { timeout: 15000 });
                }
                else await page.waitForURL(url => url.pathname.toLowerCase().includes(latestManzini), { timeout: 20000 });
                assert.deepEqual(errors, []);
                await page.screenshot({ path: `artifacts/case-reporting-${name}-${process.env.SWIFTAID_VIEWPORT_WIDTH || 1280}.png`, fullPage: true });
                console.log('PASS', name, 'voice-default-on', 'audio-bytes', audio.reduce((sum, size) => sum + size, 0));
            } catch (error) {
                await page.screenshot({ path: `artifacts/case-reporting-${name}-failure.png`, fullPage: true });
                console.error('Rendered page', page.url(), (await page.locator('body').innerText()).slice(-2000));
                throw error;
            } finally { await context.close(); }
        }
    } finally { await Promise.race([browser.close(), new Promise(resolve => setTimeout(resolve, 3000))]); }
})().then(() => process.exit(0), error => { console.error(error); process.exit(1); });
