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
        browser = await chromium.launch({ headless: true });
        const page = await browser.newPage();
        const errors = [];
        page.on('pageerror', error => errors.push(error.message));
        for (const width of [1280, 390]) {
            await page.setViewportSize({ width, height: 900 });
            await page.goto(base + '/ellai-tour-fixture');
            await page.waitForSelector('[data-fixture-ready=true]');
            await page.getByRole('button', { name: 'Start guidance' }).click();
            const popup = page.locator('.bit-bonboarding-popover');
            await popup.waitFor({ state: 'visible' });
            await popup.getByText('Choose the report subject.').waitFor();
            const spotlight = page.locator('.bit-bonboarding-spotlight');
            await spotlight.waitFor({ state: 'visible' });
            const targetBox = await page.getByRole('button', { name: 'Choose subject' }).boundingBox();
            const spotlightBox = await spotlight.boundingBox();
            assert(spotlightBox.x <= targetBox.x && spotlightBox.y <= targetBox.y &&
                spotlightBox.x + spotlightBox.width >= targetBox.x + targetBox.width &&
                spotlightBox.y + spotlightBox.height >= targetBox.y + targetBox.height,
                'The registered subject control must be highlighted');
            const next = popup.getByRole('button', { name: /finish|done|next/i });
            await next.click();
            assert.equal(await page.locator('#fixture-accepted').textContent(), 'false');
            assert(await popup.isVisible(), 'A displayed step must not prove its completion');
            await page.getByRole('button', { name: 'Choose subject' }).click();
            await page.waitForFunction(() => document.querySelector('#fixture-accepted')?.textContent === 'true');
            await next.click();
            await popup.waitFor({ state: 'hidden' });
            console.log(`PASS: compiled tour requires interaction before completion at ${width}px`);
        }
        await page.goto(base + '/ellai-report-fixture');
        await page.waitForSelector('[data-fixture-ready=true]');
        await page.getByRole('button', { name: 'Guide subject selection' }).click();
        const reportPopup = page.locator('.bit-bonboarding-popover');
        await reportPopup.getByText('Choose a subject and continue.').waitFor();
        const reportNext = reportPopup.getByRole('button', { name: /finish|done|next/i });
        await reportNext.click();
        assert(await reportPopup.isVisible(), 'The report subject is not complete before validation');
        await page.getByRole('button', { name: 'Continue to details' }).click();
        await page.locator('[data-ellai-control="report.narrative"]').waitFor();
        await page.getByRole('button', { name: 'Advance observed guidance' }).click();
        await page.waitForFunction(() => document.querySelector('#fixture-tour-status')?.textContent === 'Completed');
        assert.equal(await page.locator('#submission-count').textContent(), '0');
        console.log('PASS: compiled report tour advances after real stepper validation without submitting');
        await page.getByRole('combobox', { name: 'When did this happen?' }).click();
        await page.getByRole('option', { name: 'Date only' }).click();
        await page.getByRole('button', { name: 'Review report', exact: true }).click();
        await page.getByText('Describe what happened or add an observation.', { exact: true }).first().waitFor();
        await page.getByRole('button', { name: 'Guide report details' }).click();
        await page.locator('.bit-bonboarding-popover').waitFor({ state: 'visible' });
        const containsTarget = async anchor => {
            const target = await page.locator(`[data-bonboarding-anchor="${anchor}"]`).boundingBox();
            const spot = await page.locator('.bit-bonboarding-spotlight').boundingBox();
            return spot && target && spot.x <= target.x && spot.y <= target.y &&
                spot.x + spot.width >= target.x + target.width && spot.y + spot.height >= target.y + target.height;
        };
        await page.getByRole('button', { name: 'Refresh validation target' }).click();
        await page.waitForFunction(() => document.querySelector('[data-bonboarding-anchor="ellai-report-original-statement"]') !== null);
        assert(await containsTarget('ellai-report-original-statement'), 'First validation highlight must cover the missing narrative');
        await page.getByLabel('Describe what happened').fill('Synthetic observation');
        await page.getByLabel('Describe what happened').press('Tab');
        await page.evaluate(async () => { window.fixtureObservations = await import('/_content/SwiftAid.UI/js/ellai-ui-observations.js'); });
        await page.waitForFunction(() => window.fixtureObservations.collect().validationInputIds?.[0] === 'report.occurrence-date');
        await page.getByRole('button', { name: 'Refresh validation target' }).click();
        try { await page.waitForFunction(() => {
            const target = document.querySelector('[data-bonboarding-anchor="ellai-report-occurrence-date"]')?.getBoundingClientRect();
            const spot = document.querySelector('.bit-bonboarding-spotlight')?.getBoundingClientRect();
            return target && spot && spot.left <= target.left && spot.top <= target.top && spot.right >= target.right && spot.bottom >= target.bottom;
        }, null, { timeout: 5000 }); } catch (error) {
            throw new Error(`Highlight did not move: ${JSON.stringify({
                narrative: await page.locator('[data-bonboarding-anchor="ellai-report-original-statement"]').boundingBox(),
                date: await page.locator('[data-bonboarding-anchor="ellai-report-occurrence-date"]').boundingBox(),
                spotlight: await page.locator('.bit-bonboarding-spotlight').boundingBox()
            })}`, { cause: error });
        }
        assert(await containsTarget('ellai-report-occurrence-date'), 'Next validation highlight must cover the missing date');
        console.log('PASS: compiled tour retargets from narrative to date using real report validation');
        await page.goto(base + '/ellai-report-fixture');
        await page.waitForSelector('[data-fixture-ready=true]');
        await page.getByRole('button', { name: 'Continue to details' }).click();
        await page.getByLabel('Describe what happened').fill('Synthetic observation');
        for (const label of ['Region', 'Area or district', 'Locality']) {
            await page.getByLabel(label, { exact: true }).fill('x'.repeat(201));
            await page.getByLabel(label, { exact: true }).press('Tab');
        }
        await page.getByRole('button', { name: 'Review report', exact: true }).click();
        await page.getByRole('button', { name: 'Guide report details' }).click();
        await page.locator('.bit-bonboarding-popover').waitFor({ state: 'visible' });
        for (const [label, anchor] of [['Region', 'ellai-report-region'], ['Area or district', 'ellai-report-area'], ['Locality', 'ellai-report-locality']]) {
            await page.getByRole('button', { name: 'Refresh validation target' }).click();
            await page.waitForFunction(anchorName => {
                const target = document.querySelector(`[data-bonboarding-anchor="${anchorName}"]`)?.getBoundingClientRect();
                const spot = document.querySelector('.bit-bonboarding-spotlight')?.getBoundingClientRect();
                return target && spot && spot.left <= target.left && spot.top <= target.top && spot.right >= target.right && spot.bottom >= target.bottom;
            }, anchor);
            await page.getByLabel(label, { exact: true }).fill('Synthetic place');
            await page.getByLabel(label, { exact: true }).press('Tab');
        }
        assert.equal(await page.locator('#submission-count').textContent(), '0');
        console.log('PASS: oversized place fields are highlighted in order without submitting');
        await page.goto(base + '/ellai-report-fixture');
        await page.waitForSelector('[data-fixture-ready=true]');
        await page.getByRole('button', { name: 'Continue to details' }).click();
        await page.getByLabel('Describe what happened').fill('Synthetic description');
        const observations = page.getByLabel('Add other observations (optional)');
        await observations.fill(['x'.repeat(301), ...Array(20).fill('synthetic')].join('\n'));
        await observations.press('Tab');
        await page.getByRole('button', { name: 'Review report', exact: true }).click();
        await page.evaluate(async () => { window.fixtureObservations = await import('/_content/SwiftAid.UI/js/ellai-ui-observations.js'); });
        await page.waitForFunction(() => window.fixtureObservations.collect().validationInputIds?.join(',') ===
            'report.too-many-observations,report.observation-too-long');
        await page.getByRole('button', { name: 'Guide report details' }).click();
        await page.locator('.bit-bonboarding-popover').waitFor({ state: 'visible' });
        await page.getByRole('button', { name: 'Refresh validation target' }).click();
        await page.waitForFunction(() => {
            const target = document.querySelector('[data-bonboarding-anchor="ellai-report-other-observations"]')?.getBoundingClientRect();
            const spot = document.querySelector('.bit-bonboarding-spotlight')?.getBoundingClientRect();
            return target && spot && spot.left <= target.left && spot.top <= target.top && spot.right >= target.right && spot.bottom >= target.bottom;
        });
        await observations.fill('x'.repeat(301));
        await observations.press('Tab');
        await page.waitForFunction(() => window.fixtureObservations.collect().validationInputIds?.join(',') === 'report.observation-too-long');
        await observations.fill('x'.repeat(300));
        await observations.press('Tab');
        await page.waitForFunction(() => window.fixtureObservations.collect().validationInputIds?.length === 0);
        assert.equal(await page.locator('#submission-count').textContent(), '0');
        console.log('PASS: observation count and length errors identify and highlight the input in order');
        assert.deepEqual(errors, []);
        assert(!output.includes('Unhandled exception'), output);
    } finally {
        if (browser) await browser.close();
        host.kill();
    }
})().catch(error => { console.error(error); process.exitCode = 1; });
