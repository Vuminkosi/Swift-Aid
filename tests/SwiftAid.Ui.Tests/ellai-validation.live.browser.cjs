// Opt-in synthetic journey against an isolated Gateway with real reasoning and speech.
if (process.env.SWIFTAID_RUN_LIVE_GUIDANCE !== '1') throw new Error('Set SWIFTAID_RUN_LIVE_GUIDANCE=1 to run this probe.');
const assert = require('node:assert/strict');
const { chromium } = require(process.env.SWIFTAID_PLAYWRIGHT_PACKAGE);

(async () => {
    const browser = await chromium.launch({ headless: true, executablePath: process.env.SWIFTAID_CHROMIUM_PATH });
    const page = await browser.newPage({ viewport: { width: 1280, height: 900 } });
    const narration = [], audio = [], errors = [], audioCaptureErrors = [], plans = [];
    let latest;
    await page.exposeFunction('__recordSyntheticAudio', item => audio.push(item));
    await page.exposeFunction('__recordSyntheticAudioError', message => audioCaptureErrors.push(message));
    await page.addInitScript(() => {
        const originalFetch = window.fetch.bind(window);
        window.fetch = async (...args) => {
            const response = await originalFetch(...args);
            const match = new URL(response.url).pathname.match(/\/responses\/(\d+)\/audio$/);
            if (match && response.status === 200) {
                response.clone().arrayBuffer().then(data => window.__recordSyntheticAudio({
                    sequence: Number(match[1]), status: response.status, bytes: data.byteLength
                })).catch(error => window.__recordSyntheticAudioError(error.name));
            }
            return response;
        };
    });
    page.on('pageerror', error => errors.push(error.message));
    page.on('response', async response => {
        const path = new URL(response.url()).pathname;
        try {
            if (path.endsWith('/responses') && response.ok()) latest = await response.json();
            if (path.endsWith('/guidance/plan') && response.ok()) plans.push(await response.json());
            if (path.endsWith('/guidance/narration') && response.ok()) {
                const request = response.request().postDataJSON();
                narration.push({ inputId: request.validationInputId, response: await response.json() });
            }
        } catch (error) { errors.push(error.message); }
    });
    const waitFor = async (predicate, message, timeout = 120000) => {
        const end = Date.now() + timeout;
        while (Date.now() < end) {
            if (predicate()) return;
            await page.waitForTimeout(250);
        }
        throw new Error(message);
    };
    try {
        await page.goto(process.env.SWIFTAID_BASE_URL || 'http://localhost:8080');
        await page.getByRole('button', { name: 'Continue with essential cookies', exact: true }).click();
        await page.locator('.ellai-launcher-menu__button').click();
        await page.getByRole('menuitem', { name: 'Ask', exact: true }).click();
        await page.getByRole('textbox', { name: 'Message Ellai' }).fill('Show me how to complete and submit a report. Guide me through the actual controls.');
        await page.getByRole('button', { name: 'Send to Ellai', exact: true }).click();
        await waitFor(() => ['Completed', 'TerminalFailure', 'Cancelled'].includes(latest?.conversation?.at(-1)?.outcome), 'No terminal conversation outcome');
        assert.equal(latest.conversation.at(-1).outcome, 'Completed');
        await waitFor(() => plans.some(plan => plan.status === 'Ready'), 'No ready guidance plan');
        const popup = page.locator('.bit-bonboarding-popover');
        await popup.waitFor({ state: 'visible' });
        await page.getByRole('button', { name: 'Listen to Ellai', exact: true }).click();
        if (plans.at(-1).plan.steps[0].controlId === 'report.entry')
            await popup.getByRole('button', { name: 'Next', exact: true }).click();
        await page.getByText('Human health', { exact: true }).click();
        await page.getByRole('button', { name: 'Continue to details', exact: true }).click();
        await page.getByRole('combobox', { name: 'When did this happen?' }).click();
        await page.getByRole('option', { name: 'Date only' }).click();
        await page.getByRole('button', { name: 'Review report', exact: true }).click();

        const verifyIssue = async (inputId, anchor, expectedText) => {
            await waitFor(() => narration.some(item => item.inputId === inputId && item.response.text?.includes(expectedText)),
                `Missing narration for ${inputId}`, 90000);
            const response = narration.find(item => item.inputId === inputId && item.response.text?.includes(expectedText)).response;
            await page.waitForFunction(anchorName => {
                const target = document.querySelector(`[data-bonboarding-anchor="${anchorName}"]`)?.getBoundingClientRect();
                const spot = document.querySelector('.bit-bonboarding-spotlight')?.getBoundingClientRect();
                return target && spot && spot.left <= target.left && spot.top <= target.top && spot.right >= target.right && spot.bottom >= target.bottom;
            }, anchor, { timeout: 15000 });
            await waitFor(() => audio.some(item => item.sequence === response.sequence && item.bytes > 0),
                `No audio delivered for ${inputId}`, 90000);
        };
        await verifyIssue('report.original-statement', 'ellai-report-original-statement', 'Describe what happened');
        assert(!narration.some(item => item.inputId === 'report.occurrence-date'), 'Date must not be narrated before the first issue is corrected');
        await page.getByLabel('Describe what happened').fill('Synthetic observation');
        await page.getByLabel('Describe what happened').press('Tab');
        await verifyIssue('report.occurrence-date', 'ellai-report-occurrence-date', 'reported date');
        assert.deepEqual(errors, []);
        console.log('PASS: live guidance highlighted and delivered spoken validation for narrative then date; no report was submitted.');
    } catch (error) {
        console.error('Synthetic validation diagnostics:', JSON.stringify({
            narration: narration.map(item => ({ inputId: item.inputId, sequence: item.response.sequence })), audio, audioCaptureErrors, errors
        }));
        throw error;
    } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
