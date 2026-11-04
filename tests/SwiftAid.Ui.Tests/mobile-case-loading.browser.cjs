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
        const page = await browser.newPage({ viewport: { width: 390, height: 844 } });
        page.setDefaultTimeout(8000);
        await page.goto(base + '/case-loading-fixture');
        await page.getByText('Synthetic case', { exact: true }).waitFor();
        assert.equal(await page.getByText('Synthetic delayed preview', { exact: true }).count(), 0);
        await page.getByRole('button', { name: 'Release preview' }).click();
        await page.getByText('Synthetic delayed preview', { exact: true }).waitFor();
        await page.getByRole('button', { name: 'Revoke access' }).click();
        await page.getByText('Case access is unavailable', { exact: true }).waitFor();
        assert.equal(await page.getByText('Synthetic case', { exact: true }).count(), 0);
        await page.goto(base + '/case-loading-fixture');
        await page.getByText('Synthetic case', { exact: true }).waitFor();
        await page.getByRole('button', { name: 'Revoke access' }).click();
        await page.getByText('Case access is unavailable', { exact: true }).waitFor();
        await page.getByRole('button', { name: 'Release preview' }).click();
        await page.waitForTimeout(200);
        assert.equal(await page.getByText('Synthetic case', { exact: true }).count(), 0);
        await page.goto(base + '/case-detail-loading-fixture');
        await page.getByText('Synthetic first report', { exact: true }).waitFor();
        await page.getByRole('button', { name: 'Open second case' }).click();
        await page.getByText('Reported observations could not be loaded.', { exact: true }).first().waitFor();
        assert.equal(await page.getByText('Synthetic first report', { exact: true }).count(), 0);
        await page.getByRole('button', { name: 'Release observations' }).click();
        assert.equal(await page.getByText('Synthetic first report', { exact: true }).count(), 0);
        await page.goto(base + '/guest-report-loading-fixture?view=tracking');
        await page.getByRole('heading', { name: 'Waiting to send', exact: true }).waitFor();
        await page.getByRole('button', { name: 'Release policy' }).click();
        await page.getByRole('heading', { name: 'Waiting to send', exact: true }).waitFor();
        await page.goto(base + '/surveillance-loading-fixture');
        await page.getByText('Synthetic region', { exact: true }).first().waitFor();
        await page.getByRole('button', { name: 'Fail signal details' }).click();
        await page.getByText('Signal details could not be loaded.', { exact: true }).waitFor();
        await page.getByText('Synthetic region', { exact: true }).first().waitFor();
        await page.reload();
        await page.getByText('Synthetic region', { exact: true }).first().waitFor();
        await page.getByRole('button', { name: 'Deny signal details' }).click();
        await page.getByText('You do not have access to this information', { exact: true }).waitFor();
        assert.equal(await page.getByText('Synthetic region', { exact: true }).count(), 0);
        await page.goto(base + '/assignments-fixture');
        await page.getByText('First synthetic assignment', { exact: true }).first().waitFor();
        await page.getByText('Second synthetic assignment', { exact: true }).first().waitFor();
        for (const width of [320, 390, 768]) {
            await page.setViewportSize({ width, height: 844 });
            assert.equal(await page.locator('table:visible').count(), 0);
            assert(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1), `assignments overflow at ${width}px`);
        }
        await page.getByRole('button', { name: 'Next', exact: true }).click();
        await page.getByText('First assignment 13', { exact: true }).first().waitFor();
        assert.equal(await page.getByText('First synthetic assignment', { exact: true }).count(), 0);
        for (const pageNumber of [3, 4, 5]) {
            await page.getByRole('button', { name: 'Next', exact: true }).click();
            await page.getByText(`Page ${pageNumber}`, { exact: true }).waitFor();
        }
        await page.getByText('First assignment 104', { exact: true }).first().waitFor();
        for (const pageNumber of [4, 3, 2]) {
            await page.getByRole('button', { name: 'Previous', exact: true }).click();
            await page.getByText(`Page ${pageNumber}`, { exact: true }).waitFor();
        }
        await page.getByRole('button', { name: 'Previous', exact: true }).click();
        await page.getByText('First synthetic assignment', { exact: true }).first().waitFor();
        await page.getByRole('button', { name: 'Revoke queue access' }).click();
        await page.getByRole('button', { name: 'Refresh work queue' }).click();
        await page.getByText('You do not have permission to view assigned work.', { exact: true }).first().waitFor();
        assert.equal(await page.getByText('First synthetic assignment', { exact: true }).count(), 0);
        await page.goto(base + '/assistance-fixture');
        await page.getByText('First synthetic assistance', { exact: true }).first().waitFor();
        await page.getByText('Second synthetic assistance', { exact: true }).first().waitFor();
        await page.getByText('This view checks a limited monitoring queue, so some work may not be shown.', { exact: true }).waitFor();
        for (const pageNumber of [2, 3, 4, 5]) {
            await page.getByRole('button', { name: 'Next', exact: true }).click();
            await page.getByText(`Page ${pageNumber}`, { exact: true }).waitFor();
        }
        await page.getByText('First assistance 104', { exact: true }).first().waitFor();
        assert.equal(await page.locator('table:visible').count(), 0);
        await page.getByRole('button', { name: 'Revoke queue access' }).click();
        await page.getByRole('button', { name: 'Refresh work queue' }).click();
        await page.getByText('You do not have permission to view assistance work.', { exact: true }).first().waitFor();
        for (const [route, noun] of [['follow-ups', 'follow-up'], ['escalations', 'escalation']]) {
            await page.goto(`${base}/${route}-fixture`);
            await page.getByText(`First synthetic ${noun}`, { exact: true }).first().waitFor();
            await page.getByText(`Second synthetic ${noun}`, { exact: true }).first().waitFor();
            if (route === 'follow-ups') assert.equal(await page.getByText('First follow-up 1', { exact: true }).count(), 0);
            for (const width of [320, 390, 768]) {
                await page.setViewportSize({ width, height: 844 });
                assert.equal(await page.locator('table:visible').count(), 0);
                assert(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1), `${route} overflow at ${width}px`);
            }
            for (const pageNumber of [2, 3, 4, 5]) {
                await page.getByRole('button', { name: 'Next', exact: true }).click();
                await page.getByText(`Page ${pageNumber}`, { exact: true }).waitFor();
            }
            await page.getByText(`First ${noun} 104`, { exact: true }).first().waitFor();
            await page.getByRole('button', { name: 'Revoke queue access' }).click();
            await page.getByRole('button', { name: 'Refresh work queue' }).click();
            await page.getByText(route === 'follow-ups' ? 'You do not have permission to view assigned follow-ups.' : 'You do not have permission to view open work.', { exact: true }).first().waitFor();
        }
        await page.goto(base + '/investigations-fixture');
        await page.getByText('Investigation list unavailable', { exact: true }).first().waitFor();
        for (const width of [320, 390, 768]) {
            await page.setViewportSize({ width, height: 844 });
            assert.equal(await page.locator('table:visible').count(), 0);
            assert(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1), `investigations overflow at ${width}px`);
        }
        await page.goto(base + '/monitoring-fixture');
        const assignedQueue = page.locator('[aria-labelledby="monitoring-assigned-heading"]');
        const openQueue = page.locator('[aria-labelledby="monitoring-open-heading"]');
        await assignedQueue.getByText('Monitoring item 0', { exact: true }).waitFor();
        await openQueue.getByText('Monitoring item 1', { exact: true }).waitFor();
        assert.equal(await assignedQueue.getByText('Monitoring item 1', { exact: true }).count(), 0);
        assert.equal(await page.getByText('private-actor-id', { exact: true }).count(), 0);
        await openQueue.getByRole('button', { name: 'Next', exact: true }).click();
        await openQueue.getByText('Monitoring item 25', { exact: true }).waitFor();
        for (const width of [320, 390, 768]) {
            await page.setViewportSize({ width, height: 844 });
            assert.equal(await page.locator('table:visible').count(), 0);
            assert(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1), `monitoring overflow at ${width}px`);
        }
        await page.setViewportSize({ width: 390, height: 844 });
        await page.screenshot({ path: path.resolve('.codex/case-list-browser/mobile-monitoring.png'), fullPage: true });
        await page.goto(base + '/analytics-fixture');
        await page.getByText('Synthetic readable cases', { exact: true }).first().waitFor();
        await page.getByText('Showing totals for the most recent readable cases.', { exact: true }).waitFor();
        await page.getByRole('combobox', { name: 'Sector', exact: true }).click();
        await page.getByRole('option', { name: 'Human', exact: true }).click();
        await page.getByText('Synthetic human cases', { exact: true }).first().waitFor();
        await page.getByRole('combobox', { name: 'Sector', exact: true }).click();
        await page.getByRole('option', { name: 'Animal', exact: true }).click();
        await page.getByText('Synthetic animal cases', { exact: true }).first().waitFor();
        for (const width of [320, 390, 768, 1024]) {
            await page.setViewportSize({ width, height: 844 });
            assert.equal(await page.locator('table:visible').count(), 0);
            const overflow = await page.evaluate(() => ({ width: document.documentElement.scrollWidth, viewport: innerWidth,
                elements: [...document.querySelectorAll('body *')].filter(e => e.getBoundingClientRect().right > innerWidth + 1)
                    .slice(0, 5).map(e => `${e.tagName}.${e.className}`) }));
            assert(overflow.width <= overflow.viewport + 1, `analytics overflow at ${width}px: ${JSON.stringify(overflow)}`);
        }
        assert.equal(await page.getByRole('link', { name: 'Download service report as CSV' }).count(), 0);
        for (const width of [320, 390, 768]) {
            await page.setViewportSize({ width, height: 844 });
            await page.goto(base + '/terms');
            await page.getByRole('heading', { name: 'Terms', exact: true }).waitFor();
            await page.getByRole('heading', { name: 'Requesting data deletion', exact: true }).waitFor();
            assert(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1), `terms overflow at ${width}px`);
        }
        await page.goto(base + '/security-loading-fixture');
        await page.getByText('Your device session could not be loaded.', { exact: true }).waitFor();
        await page.getByRole('button', { name: 'Try again', exact: true }).click();
        await page.getByText('This device is signed in.', { exact: true }).waitFor();
        assert.equal(await page.getByText('Example provider', { exact: true }).count(), 0);
        await page.getByText('Linked sign-in methods and remote sessions cannot be managed here yet.', { exact: true }).waitFor();
        await page.getByRole('button', { name: 'Release profile' }).click();
        await page.getByText('This device is signed in.', { exact: true }).waitFor();
        await page.route('**/_content/SwiftAid.UI/js/ellai-ui-observations.js', route => route.abort());
        await page.goto(base + '/public-session-loading-fixture');
        await page.getByText('No upcoming sessions found', { exact: true }).waitFor();
        for (let attempt = 0; attempt < 20; attempt++) {
            await page.getByRole('button', { name: 'Refresh request counts' }).click();
            if (Number(await page.locator('#public-query-calls').textContent()) > 0) break;
            await page.waitForTimeout(100);
        }
        assert.equal(await page.locator('#public-list-calls').textContent(), '0');
        assert(Number(await page.locator('#public-query-calls').textContent()) > 0);
        await page.unroute('**/_content/SwiftAid.UI/js/ellai-ui-observations.js');
        for (const viewport of [{ width: 320, height: 568 }, { width: 390, height: 844 }, { width: 667, height: 375 }]) {
            await page.setViewportSize(viewport);
            await page.goto(base + '/welcome-fixture');
            await page.getByRole('button', { name: 'Continue as guest' }).waitFor();
            const size = await page.locator('.mobile-welcome__carousel').evaluate(element => element.getBoundingClientRect().height);
            assert(size >= 220 && size <= 340, `unexpected welcome carousel height ${size}`);
            assert(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1), `welcome overflow at ${viewport.width}px`);
            await page.getByRole('button', { name: 'Continue as guest' }).scrollIntoViewIfNeeded();
            assert(await page.getByRole('button', { name: 'Continue as guest' }).isVisible());
        }
        await page.getByRole('button', { name: 'Continue as guest' }).click();
        await page.waitForURL('**/report');
        assert(!output.includes('Unhandled exception'), output.split('\n').filter(line => /Unhandled exception|Exception:| at SwiftAid/.test(line)).join('\n'));
        console.log('PASS: local history, case and surveillance views handle pending requests, failures and revoked access.');
    } catch (error) {
        console.error(output.split('\n').filter(line => /Unhandled exception|Exception:| at SwiftAid/.test(line)).join('\n'));
        throw error;
    } finally {
        if (browser) await browser.close();
        host.kill();
    }
})().catch(error => { console.error(error); process.exitCode = 1; });
