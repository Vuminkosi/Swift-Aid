// Opt-in synthetic journey against an isolated Gateway with real reasoning and speech.
if (process.env.SWIFTAID_RUN_LIVE_GUIDANCE !== '1') throw new Error('Set SWIFTAID_RUN_LIVE_GUIDANCE=1 to run this probe.');
const assert = require('node:assert/strict');
const { chromium } = require(process.env.SWIFTAID_PLAYWRIGHT_PACKAGE);

(async () => {
    const browser = await chromium.launch({ headless: true, executablePath: process.env.SWIFTAID_CHROMIUM_PATH });
    const page = await browser.newPage({ ignoreHTTPSErrors: process.env.SWIFTAID_ALLOW_LOCAL_TEST_CERT === '1', viewport: { width: Number(process.env.SWIFTAID_VIEWPORT_WIDTH || 1280), height: Number(process.env.SWIFTAID_VIEWPORT_HEIGHT || 900) } });
    const narration = [], audio = [], errors = [], responseCaptureErrors = [], audioCaptureErrors = [], plans = [];
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
        } catch (error) { responseCaptureErrors.push({ path, message: error.message }); }
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
        await page.getByRole('button', { name: 'Turn off Ellai voice', exact: true }).waitFor({ state: 'visible' });
        console.log('PLAN', JSON.stringify(plans.at(-1).plan.steps.map(s => ({key:s.stepKey, control:s.controlId}))));
        const seen = new Set();
        for (let turn = 0; turn < 40; turn++) {
            await page.waitForTimeout(1200);
            const instruction = await popup.innerText();
            const highlighted = await page.evaluate(() => {
                const spot = document.querySelector('.bit-bonboarding-spotlight')?.getBoundingClientRect();
                if (!spot) return [];
                return [...document.querySelectorAll('[data-bonboarding-anchor]')].filter(el => {
                    const r = el.getBoundingClientRect();
                    return r.width > 0 && r.height > 0 && Math.abs(spot.left-r.left)<25 && Math.abs(spot.top-r.top)<25 && Math.abs(spot.right-r.right)<25 && Math.abs(spot.bottom-r.bottom)<25;
                }).map(el => el.getAttribute('data-bonboarding-anchor'));
            });
            console.log('STEP', turn, JSON.stringify({highlighted, instruction}));
            for (const anchor of highlighted) {
                seen.add(anchor);
                if (process.env.SWIFTAID_GUIDANCE_SCREENSHOT_PREFIX && anchor.startsWith('ellai-report-'))
                    await page.screenshot({path:process.env.SWIFTAID_GUIDANCE_SCREENSHOT_PREFIX+'-'+anchor+'.png'});
            }
            if (highlighted.includes('ellai-report-subject-kind')) await page.getByText('Human health', { exact:true }).click();
            if (highlighted.includes('ellai-report-continue-details')) {
                await page.getByRole('button', {name:'Continue to details', exact:true}).click();
                await popup.waitFor({state:'visible', timeout:90000});
                continue;
            }
            if (highlighted.includes('ellai-report-original-statement')) await page.getByLabel('Describe what happened').fill('Synthetic tour observation only.');
            if (highlighted.includes('ellai-report-age-years')) await page.getByLabel('Age in years (optional)').fill('35');
            if (highlighted.includes('ellai-report-onset-precision')) {
                await page.getByRole('combobox', {name:'When did symptoms or signs start?', exact:true}).click();
                await page.getByRole('option', {name:'Date only', exact:true}).click();
            }
            if (highlighted.includes('ellai-report-onset-date')) {
                await page.getByLabel('Reported onset date', {exact:true}).click();
                await page.locator('.mud-picker-calendar:visible').locator('button.mud-picker-calendar-day:not(.mud-adjacent-month)').filter({hasText:/^1$/}).click();
                await page.keyboard.press('Escape');
                await page.locator('[data-bonboarding-anchor="ellai-report-onset-date"] input').press('Tab');
            }
            if (highlighted.includes('ellai-report-time-precision')) {
                await page.getByRole('combobox', {name:'When did this happen?', exact:true}).click();
                await page.getByRole('option', {name:'Date only', exact:true}).click();
            }
            if (highlighted.includes('ellai-report-occurrence-date')) {
                await page.getByLabel('Reported date', {exact:true}).click();
                await page.locator('.mud-picker-calendar:visible').locator('button.mud-picker-calendar-day:not(.mud-adjacent-month)').filter({hasText:/^2$/}).click();
                await page.keyboard.press('Escape');
                await page.locator('[data-bonboarding-anchor="ellai-report-occurrence-date"] input').press('Tab');
            }
            if (highlighted.includes('ellai-report-place')) {
                await page.getByRole('combobox', {name:'Country where the event happened', exact:true}).click();
                console.log('COUNTRIES', await page.getByRole('option').allTextContents());
                await page.getByRole('option', {name:'Eswatini', exact:true}).click();
            }
            if (highlighted.includes('ellai-report-place-locality')) break;
            await popup.getByRole('button', { name:'Next', exact:true }).click();
        }
        for (const field of ['age-years','core-symptoms','signs-of-dehydration','additional-symptoms-and-signs','onset-date','occurrence-date','place','place-country','place-region','place-locality'])
            assert(seen.has('ellai-report-'+field), 'Missing input highlight: '+field);
        assert.deepEqual(errors, []);
        if (responseCaptureErrors.length) console.log('Response capture diagnostics:', JSON.stringify(responseCaptureErrors));
        console.log('PASS: requested input highlights reached through live tour Next actions.');
    } catch (error) {
        console.error(error);
        await page.screenshot({path:'artifacts/input-tour-failure.png'});
        console.error('LIVE FAILURE', JSON.stringify({plans:plans.map(p=>({status:p.status,code:p.diagnosticCode})),errors}));
        throw error;
    } finally { await Promise.race([browser.close(), new Promise(resolve => setTimeout(resolve, 3000))]); }
})().then(() => process.exit(0)).catch(error => {console.error(error); process.exit(1);});
