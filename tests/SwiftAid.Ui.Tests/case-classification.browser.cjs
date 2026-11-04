const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { spawn } = require('node:child_process');
const { chromium } = require(process.env.SWIFTAID_PLAYWRIGHT_PACKAGE || 'playwright');

async function main() {
    const host = spawn('dotnet', [path.resolve('tests/SwiftAid.Ui.TestHost/bin/Debug/net10.0/SwiftAid.Ui.TestHost.dll'), '--urls', 'http://127.0.0.1:0'], {
        cwd: path.resolve('tests/SwiftAid.Ui.TestHost'), windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'],
        env: { ...process.env, ASPNETCORE_ENVIRONMENT: 'Development' }
    });
    let output = '', browser;
    host.stdout.on('data', d => { output += d; }); host.stderr.on('data', d => { output += d; });
    try {
        const deadline = Date.now() + 30000;
        while (!output.match(/Now listening on:\s*(http:\/\/127\.0\.0\.1:\d+)/)) {
            assert(host.exitCode === null && Date.now() < deadline, output);
            await new Promise(r => setTimeout(r, 100));
        }
        const base = output.match(/Now listening on:\s*(http:\/\/127\.0\.0\.1:\d+)/)[1];
        browser = await chromium.launch({ headless: true, ...(process.env.SWIFTAID_CHROMIUM_PATH ? { executablePath: process.env.SWIFTAID_CHROMIUM_PATH } : {}) });
        const page = await browser.newPage();
        const errors = [];
        page.on('pageerror', e => errors.push(e.message));
        const artifacts = path.join(__dirname, 'artifacts', 'case-classification'); fs.mkdirSync(artifacts, { recursive: true });
        for (const width of [1280, 390]) {
            await page.setViewportSize({ width, height: 900 });
            await page.emulateMedia({ colorScheme: width === 390 ? 'dark' : 'light' });
            await page.goto(base + '/case-classification-fixture');
            await page.getByText('Respiratory symptoms', { exact: true }).waitFor();
            assert(await page.getByText('The location is approximate or unknown.', { exact: true }).isVisible());
            assert(!(await page.locator('body').innerText()).includes('internal explanation'));
            await page.getByRole('button', { name: 'Refresh report analysis' }).click();
            await page.getByText('Respiratory symptoms', { exact: true }).waitFor();
            await page.getByRole('button', { name: 'Refresh report analysis' }).focus();
            await page.keyboard.press('Enter');
            await page.getByText('Respiratory symptoms', { exact: true }).waitFor();
            assert(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1));
            await page.screenshot({ path: path.join(artifacts, `analysis-${width}.png`), fullPage: true });
            for (const [mode, expected] of [
                ['pending', 'Analyzing…'], ['abstained', 'This report’s language is not supported for analysis yet.'],
                ['failed', 'Analysis failed'], ['empty', 'No analysis is available for this report yet.'],
                ['denied', 'Analysis is not available for this case.'], ['unavailable', 'We couldn’t load the analysis. Check your connection and try Refresh.']
            ]) {
                await page.getByRole('button', { name: mode, exact: true }).click();
                await page.getByText(expected, { exact: true }).waitFor();
                assert.equal(await page.getByText('Respiratory symptoms', { exact: true }).count(), 0);
            }
            await page.getByRole('button', { name: 'slow', exact: true }).click();
            await page.getByText('Loading analysis…', { exact: true }).waitFor();
            await page.getByRole('button', { name: 'denied', exact: true }).click();
            await page.getByText('Analysis is not available for this case.', { exact: true }).waitFor();
            await page.waitForTimeout(1500); // Exceeds the deliberately delayed stale response in this fixture.
            assert.equal(await page.getByText('Respiratory symptoms', { exact: true }).count(), 0);
        }
        assert.deepEqual(errors, []);
        console.log(JSON.stringify({ passed: true, widths: [1280, 390], colorSchemes: ['light', 'dark'], keyboardRefresh: true, states: 7, staleResponseRejected: true, pageErrors: errors }));
    } finally {
        if (browser) await browser.close();
        host.kill();
    }
}
main().catch(e => { console.error(e); process.exitCode = 1; });
