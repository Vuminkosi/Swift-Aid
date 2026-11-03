const assert = require('node:assert/strict');
const path = require('node:path');
const { spawn } = require('node:child_process');
const { chromium } = require(process.env.SWIFTAID_PLAYWRIGHT_PACKAGE || 'playwright');

(async () => {
    const host = spawn('dotnet', [path.resolve('tests/SwiftAid.Ui.TestHost/bin/Debug/net10.0/SwiftAid.Ui.TestHost.dll'), '--urls', 'http://127.0.0.1:0'], {
        cwd: path.resolve('tests/SwiftAid.Ui.TestHost'), windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'],
        env: { ...process.env, ASPNETCORE_ENVIRONMENT: 'Development' }
    });
    let output = '', browser;
    host.stdout.on('data', data => { output += data; });
    host.stderr.on('data', data => { output += data; });
    try {
        const deadline = Date.now() + 30000;
        while (!/Now listening on:\s*(http:\/\/127\.0\.0\.1:\d+)/.test(output)) {
            assert(host.exitCode === null && Date.now() < deadline, output);
            await new Promise(resolve => setTimeout(resolve, 100));
        }
        const base = output.match(/Now listening on:\s*(http:\/\/127\.0\.0\.1:\d+)/)[1];
        browser = await chromium.launch({ headless: true, ...(process.env.SWIFTAID_CHROMIUM_PATH ? { executablePath: process.env.SWIFTAID_CHROMIUM_PATH } : {}) });
        const page = await browser.newPage();
        const errors = [];
        page.on('pageerror', error => errors.push(error.message));
        for (const width of [1280, 390]) {
            await page.setViewportSize({ width, height: 900 });
            await page.emulateMedia({ colorScheme: width === 390 ? 'dark' : 'light' });
            await page.goto(base + '/ellai-report-fixture');
            await page.waitForSelector('[data-fixture-ready=true]');
            await page.evaluate(async () => { window.fixtureObservations = await import('/_content/SwiftAid.UI/js/ellai-ui-observations.js'); });
            const collect = () => page.evaluate(() => window.fixtureObservations.collect());
            const accepted = async id => page.waitForFunction(id => {
                const report = window.fixtureObservations.collect();
                return report.completedControlIds?.includes(id);
            }, id);
            const subject = page.locator('[data-ellai-control="report.subject"]');
            assert.equal(await subject.getByRole('radio').count(), 2, 'Subject guidance must highlight the actual choices, not the heading');
            await page.getByRole('radio', { name: 'Animal health — an animal or group of animals' }).check();
            await page.waitForFunction(() => window.fixtureObservations.collect().validationInputIds?.join(',') === 'report.animal-scope');
            assert.equal(await page.locator('[data-bonboarding-anchor="ellai-report-animal-scope"]').getByRole('radio').count(), 4);
            await page.getByRole('radio', { name: 'Herd' }).check();
            await page.waitForFunction(() => window.fixtureObservations.collect().validationInputIds?.length === 0);
            await page.getByRole('radio', { name: 'Human health — a person or people' }).check();
            await page.getByRole('button', { name: 'Continue to details', exact: true }).click();
            await accepted('report.subject');
            assert.equal(await page.locator('[data-ellai-control="report.narrative"]').getByRole('textbox').count(), 1,
                'Narrative guidance must highlight its editable control');
            await page.getByRole('button', { name: 'Review report', exact: true }).click();
            await page.getByText('Describe what happened or add an observation.', { exact: true }).first().waitFor();
            assert(!(await collect()).completedControlIds?.includes('report.narrative'));
            await page.waitForFunction(() => window.fixtureObservations.collect().validationInputIds?.[0] === 'report.original-statement');
            const targetedIssues = await page.evaluate(() => window.fixtureObservations.collect(['report.narrative']).validationInputIds);
            assert.deepEqual(targetedIssues, ['report.original-statement']);
            const unrelatedIssues = await page.evaluate(() => window.fixtureObservations.collect(['cases.filter']).validationInputIds);
            assert.deepEqual(unrelatedIssues, []);
            assert.equal(await page.locator('[data-bonboarding-anchor="ellai-report-original-statement"]').getByRole('textbox').count(), 1);
            await page.getByRole('combobox', { name: 'When did this happen?' }).click();
            await page.getByRole('option', { name: 'Date only' }).click();
            await page.getByRole('button', { name: 'Review report', exact: true }).click();
            await page.waitForFunction(() => window.fixtureObservations.collect().validationInputIds?.join(',') ===
                'report.original-statement,report.occurrence-date');

            await page.getByLabel('Describe what happened').fill('SYNTHETIC-PRIVATE observed concern for fixture only.');
            await page.getByLabel('Describe what happened').press('Tab');
            await page.waitForFunction(() => window.fixtureObservations.collect().validationInputIds?.[0] === 'report.occurrence-date');
            await page.getByLabel('Reported date').fill('2026-10-01');
            await page.getByLabel('Reported date').press('Tab');
            await page.waitForFunction(() => window.fixtureObservations.collect().validationInputIds?.length === 0, null, { timeout: 5000 });
            await page.getByRole('combobox', { name: 'When did this happen?' }).click();
            await page.getByRole('option', { name: 'Exact time' }).click();
            await page.waitForFunction(() => window.fixtureObservations.collect().validationInputIds?.join(',') ===
                'report.occurrence-time,report.utc-offset');
            await page.getByLabel('Reported time').fill('07:30');
            await page.getByLabel('Reported time').press('Tab');
            await page.waitForFunction(() => window.fixtureObservations.collect().validationInputIds?.join(',') === 'report.utc-offset');
            await page.getByLabel('Time difference from UTC').fill('+02:00');
            await page.getByLabel('Time difference from UTC').press('Tab');
            await page.waitForFunction(() => window.fixtureObservations.collect().validationInputIds?.length === 0);
            await page.getByRole('button', { name: 'Save draft', exact: true }).click();
            await page.getByRole('button', { name: 'Finish fixture storage', exact: true }).waitFor();
            assert(!(await collect()).completedControlIds?.includes('report.save'));
            await page.getByRole('button', { name: 'Fail fixture storage', exact: true }).click();
            await page.getByRole('button', { name: 'Save draft', exact: true }).waitFor();
            assert(!(await collect()).completedControlIds?.includes('report.save'));
            await page.getByRole('button', { name: 'Save draft', exact: true }).click();
            await page.getByRole('button', { name: 'Finish without storage confirmation', exact: true }).click();
            await page.getByText('We could not confirm whether your draft was saved. Your entries remain on this screen. Check saved reports before trying again.', { exact: true }).waitFor();
            assert(!(await collect()).completedControlIds?.includes('report.save'));
            assert((await page.getByLabel('Describe what happened').inputValue()).includes('SYNTHETIC-PRIVATE'));
            await page.getByRole('button', { name: 'Save draft', exact: true }).click();
            await page.getByRole('button', { name: 'Finish fixture storage', exact: true }).click();
            await accepted('report.save');
            assert((await page.evaluate(() => localStorage.getItem('swiftaid.synthetic-draft-fixture')))?.includes('SYNTHETIC-PRIVATE'));
            await page.getByLabel('Describe what happened').fill('SYNTHETIC-PRIVATE newer unsaved observation.');
            await page.getByLabel('Describe what happened').press('Tab');
            await page.waitForFunction(() => !window.fixtureObservations.collect().completedControlIds?.includes('report.save'));
            await page.getByRole('button', { name: 'Review report', exact: true }).click();
            await accepted('report.narrative');
            await accepted('report.time-location');
            const send = page.getByRole('button', { name: 'Send report', exact: true });
            assert(await send.isDisabled());
            await page.waitForFunction(() => window.fixtureObservations.collect().validationInputIds?.join(',') ===
                'report.review-confirmed,report.country-confirmed,report.processing-confirmed');
            await page.getByRole('checkbox', { name: 'I confirm the selected event country.', exact: true }).check();
            await page.waitForFunction(() => window.fixtureObservations.collect().validationInputIds?.join(',') ===
                'report.review-confirmed,report.processing-confirmed');
            await page.getByRole('checkbox', { name: 'I reviewed the report-handling information.', exact: true }).check();
            await page.waitForFunction(() => window.fixtureObservations.collect().validationInputIds?.join(',') === 'report.review-confirmed');
            assert(!(await collect()).completedControlIds?.includes('report.confirmations'));
            await page.getByRole('checkbox', { name: 'I reviewed the original statement and the known or unknown details above.', exact: true }).check();
            await accepted('report.confirmations');
            await page.waitForFunction(() => window.fixtureObservations.collect().validationInputIds?.length === 0);
            assert.equal((await collect()).controlStates.find(c => c.controlId === 'report.confirmations')?.state, 4,
                'The visible confirmation control must be Ready, not the hidden native checkbox');
            await page.getByRole('checkbox', { name: 'I reviewed the report-handling information.', exact: true }).uncheck();
            await page.waitForFunction(() => !window.fixtureObservations.collect().completedControlIds?.includes('report.confirmations'));
            assert(await send.isDisabled());
            await page.getByRole('checkbox', { name: 'I reviewed the report-handling information.', exact: true }).check();
            await page.getByRole('checkbox', { name: 'I reviewed the original statement and the known or unknown details above.', exact: true }).check();
            await accepted('report.confirmations');

            assert.equal(await page.locator('#submission-count').textContent(), '0');
            assert(!(await collect()).completedControlIds?.includes('report.submit'));
            await send.click();
            await page.getByRole('heading', { name: 'Your report receipt' }).waitFor();
            await page.waitForFunction(() => window.fixtureObservations.collect().controlStates.some(c => c.controlId === 'report.receipt' && c.state === 4));
            const report = await collect();
            assert.equal(await page.locator('#submission-count').textContent(), '1');
            assert(!report.completedControlIds?.includes('report.receipt'));
            assert(!report.completedControlIds?.includes('report.submit'));
            assert(!JSON.stringify(report).includes('SYNTHETIC-PRIVATE'));
            console.log(`PASS: report form and semantic observations at ${width}px`);
        }
        await page.goto(base + '/ellai-report-fixture');
        await page.waitForSelector('[data-fixture-ready=true]');
        await page.getByRole('button', { name: 'Continue to details', exact: true }).click();
        await page.getByLabel('Describe what happened').fill('Synthetic offline draft');
        await page.getByLabel('Describe what happened').press('Tab');
        await page.getByRole('button', { name: 'Toggle country availability' }).click();
        await page.getByRole('button', { name: 'Save draft', exact: true }).click();
        await page.getByRole('button', { name: 'Finish fixture storage', exact: true }).click();
        await page.waitForFunction(() => {
            const saved = JSON.parse(localStorage.getItem('swiftaid.synthetic-draft-fixture') || 'null');
            return saved?.Location?.CountryCode === 'SZ' && saved?.Confirmations?.OccurrenceCountryConfirmed === false;
        });
        assert.equal(await page.locator('#submission-count').textContent(), '0');
        console.log('PASS: draft preserves an unverified country while the catalogue is unavailable');
        assert.deepEqual(errors, []);
        assert(!output.includes('Unhandled exception'), output);
        console.log('PASS: real shared report components in Chromium; synthetic receipt callback only, no Gateway/provider/database/device verification.');
    } finally {
        if (browser) await browser.close();
        host.kill();
    }
})().catch(error => { console.error(error); process.exitCode = 1; });
