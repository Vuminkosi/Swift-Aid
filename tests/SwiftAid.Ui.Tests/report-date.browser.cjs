const assert = require('node:assert/strict');
const path = require('node:path');
const { spawn } = require('node:child_process');
const { chromium } = require(process.env.SWIFTAID_PLAYWRIGHT_PACKAGE || 'playwright');

async function main() {
    const host = spawn('dotnet', [path.resolve('tests/SwiftAid.Ui.TestHost/bin/Debug/net10.0/SwiftAid.Ui.TestHost.dll'), '--urls', 'http://127.0.0.1:0'], {
        cwd: path.resolve('tests/SwiftAid.Ui.TestHost'), windowsHide: true,
        env: { ...process.env, ASPNETCORE_ENVIRONMENT: 'Development' }, stdio: ['ignore', 'pipe', 'pipe']
    });
    let output = '';
    host.stdout.on('data', data => output += data.toString());
    host.stderr.on('data', data => output += data.toString());
    let browser;
    try {
        const end = Date.now() + 30000;
        while (!/Now listening on:\s*(http:\/\/127\.0\.0\.1:\d+)/.test(output)) {
            if (host.exitCode !== null || Date.now() > end) throw new Error(`Fixture did not start: ${output}`);
            await new Promise(resolve => setTimeout(resolve, 100));
        }
        const baseUrl = output.match(/Now listening on:\s*(http:\/\/127\.0\.0\.1:\d+)/)[1];
        browser = await chromium.launch({ channel: 'chrome', headless: true });
        const page = await browser.newPage({ viewport: { width: 390, height: 844 } });
        await page.goto(baseUrl);
        await page.getByRole('heading', { name: 'Shared component verification fixture' }).waitFor();
        const report = page.getByTestId('guided-report-fixture');
        await page.waitForFunction(() => [...document.querySelectorAll('[data-testid="guided-report-fixture"] button')]
            .some(button => [...button.attributes].some(attribute => attribute.name.startsWith('_bl_'))));
        await report.getByRole('radio', { name: 'Human health — a person or people' }).check();
        await report.getByRole('radio', { name: 'Female' }).check();
        await report.getByRole('radio', { name: 'Yes' }).check();
        await report.getByRole('button', { name: 'Continue to details' }).click();
        await report.getByLabel('Describe what happened').fill('Synthetic reported concern');
        await report.getByRole('combobox', { name: 'Country where the event happened' }).click();
        await page.getByRole('option', { name: 'Zimbabwe' }).click();
        await report.getByRole('combobox', { name: 'When did this happen?' }).click();
        await page.getByRole('option', { name: 'Date only' }).click();
        const date = report.locator('input[type=date]');
        await date.fill('2026-10-30');
        await date.blur();
        await report.getByRole('button', { name: 'Review report' }).click();
        if (await report.locator('.guided-report__validation').innerText())
            console.log('Validation:', await report.locator('.guided-report__validation').innerText());
        await report.getByRole('heading', { name: 'Review your report' }).waitFor({ timeout: 5000 });
        assert.match(await report.locator('.guided-report__summary').innerText(), /10\/30\/2026|30\/10\/2026|2026-10-30/);
        assert.match(await report.locator('.guided-report__summary').innerText(), /Gender\s+Female[\s\S]*Death related\s+Yes/);
        await report.getByRole('checkbox', { name: /I reviewed the original statement/ }).check();
        await report.getByRole('checkbox', { name: 'I confirm the selected event country.' }).check();
        await report.getByRole('checkbox', { name: /I have reviewed the report-handling information/ }).check();
        await report.getByRole('button', { name: 'Submit report' }).click();
        await page.getByTestId('report-callback-status').getByText('date: 2026-10-30', { exact: false }).waitFor();
        assert.match(await page.getByTestId('report-callback-status').innerText(), /gender: Female \| death related: True/);
        const animalPage = await browser.newPage({ viewport: { width: 1280, height: 800 } });
        await animalPage.goto(baseUrl);
        await animalPage.waitForFunction(() => [...document.querySelectorAll('[data-testid="guided-report-fixture"] button')]
            .some(button => [...button.attributes].some(attribute => attribute.name.startsWith('_bl_'))));
        const animal = animalPage.getByTestId('guided-report-fixture');
        await animal.getByRole('radio', { name: 'Animal health — an animal or group of animals' }).check();
        await animal.getByRole('radio', { name: 'Herd', exact: true }).check();
        await animal.getByRole('radio', { name: 'No', exact: true }).check();
        assert.equal(await animal.getByRole('radio', { name: 'Female' }).count(), 0);
        await animal.getByRole('button', { name: 'Continue to details' }).click();
        await animal.getByLabel('Describe what happened').fill('Synthetic animal concern');
        await animal.getByRole('combobox', { name: 'When did this happen?' }).click();
        await animalPage.getByRole('option', { name: 'Approximate date' }).click();
        await animal.locator('input[type=date]').fill('2026-10-29');
        await animal.locator('input[type=date]').blur();
        await animal.getByRole('button', { name: 'Review report' }).click();
        await animal.getByRole('heading', { name: 'Review your report' }).waitFor({ timeout: 5000 });
        assert.match(await animal.locator('.guided-report__summary').innerText(), /Death related\s+No/);
        console.log('PASS: human date and choices reach submit callback; animal approximate date and death choice reach review (synthetic component fixture).');
    } finally {
        if (browser) await browser.close();
        host.kill();
    }
}
main().catch(error => { console.error(error); process.exitCode = 1; });
