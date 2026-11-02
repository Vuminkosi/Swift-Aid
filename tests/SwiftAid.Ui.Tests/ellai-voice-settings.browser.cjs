const assert = require('node:assert/strict');
const { chromium } = require(process.env.SWIFTAID_PLAYWRIGHT_PACKAGE || 'playwright');

(async () => {
    const browser = await chromium.launch({ headless: true,
        ...(process.env.SWIFTAID_CHROMIUM_PATH ? { executablePath: process.env.SWIFTAID_CHROMIUM_PATH } : {}) });
    try {
        const page = await browser.newPage();
        const errors = [];
        page.on('pageerror', error => errors.push(error.message));
        const base = process.env.SWIFTAID_TEST_BASE_URL || 'http://localhost:8080';
        for (const width of [1280, 390]) {
            await page.setViewportSize({ width, height: 900 });
            await page.goto(base + '/ellai/about');
            const selector = page.locator('div[role="combobox"]').filter({ hasText: /Heart|Michael/ });
            await selector.waitFor();
            await selector.click();
            await page.getByRole('option', { name: 'Michael', exact: true }).click();
            await page.waitForFunction(() => JSON.parse(localStorage.getItem('swiftaid.ellai.languages.v1'))?.englishVoiceId === 'am_michael');
            await page.reload();
            await page.waitForFunction(() => [...document.querySelectorAll('div[role="combobox"]')].some(input => input.textContent === 'Michael'));
            assert(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1));
            await page.screenshot({ path: `.codex/ellai-voice-settings-${width}.png`, fullPage: true });
            await page.locator('div[role="combobox"]').filter({ hasText: /Heart|Michael/ }).click();
            await page.getByRole('option', { name: 'Heart', exact: true }).click();
            await page.waitForFunction(() => JSON.parse(localStorage.getItem('swiftaid.ellai.languages.v1'))?.englishVoiceId === 'af_heart');
        }
        assert.deepEqual(errors, []);
        console.log('PASS: deployed About settings; Heart/Michael selection and reload persistence at desktop/mobile viewport widths. Not a mobile-device test.');
    } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
