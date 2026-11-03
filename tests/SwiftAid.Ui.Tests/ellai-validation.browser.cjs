const assert = require("node:assert/strict");
const path = require("node:path");
const { spawn } = require("node:child_process");
const { chromium } = require("playwright");

async function run() {
    const host = spawn("dotnet", [path.resolve("tests/SwiftAid.Ui.TestHost/bin/Debug/net10.0/SwiftAid.Ui.TestHost.dll"),
        "--urls", "http://127.0.0.1:0"], {
        cwd: path.resolve("tests/SwiftAid.Ui.TestHost"), windowsHide: true, stdio: ["ignore", "pipe", "pipe"],
        env: { ...process.env, ASPNETCORE_ENVIRONMENT: "Development" }
    });
    let output = "";
    host.stdout.on("data", chunk => output += chunk.toString());
    host.stderr.on("data", chunk => output += chunk.toString());
    let browser;
    try {
        let baseUrl;
        const deadline = Date.now() + 30000;
        while (!baseUrl && Date.now() < deadline) {
            if (host.exitCode !== null) throw new Error(`Fixture exited: ${output}`);
            baseUrl = output.match(/Now listening on:\s*(http:\/\/127\.0\.0\.1:\d+)/)?.[1];
            if (!baseUrl) await new Promise(resolve => setTimeout(resolve, 100));
        }
        if (!baseUrl) throw new Error(`Fixture did not start: ${output}`);
        browser = await chromium.launch({ headless: true,
            ...(process.env.SWIFTAID_CHROMIUM_PATH ? { executablePath: process.env.SWIFTAID_CHROMIUM_PATH } : {}) });
        for (const [width, colorScheme] of [[1280, "light"], [390, "dark"]]) {
            const page = await browser.newPage({ viewport: { width, height: 850 }, colorScheme });
            page.setDefaultTimeout(10000);
            const failures = [];
            page.on("pageerror", error => failures.push(error.message));
            page.on("response", response => {
                if (response.status() >= 400 && new URL(response.url()).pathname !== "/favicon.ico")
                    failures.push(`${response.status()} ${response.url()}`);
            });
            await page.goto(baseUrl, { waitUntil: "networkidle" });
            await page.getByTestId("toggle-loading").click();
            await page.getByTestId("toggle-loading").getByText("Hide loading status").waitFor();
            await page.getByTestId("toggle-loading").click();
            await page.getByTestId("toggle-loading").getByText("Show loading status").waitFor();
            await page.waitForFunction(expected => document.documentElement.dataset.colorMode === expected, colorScheme);
            const report = page.getByTestId("guided-report-fixture");
            await report.getByRole("radio", { name: "Human health — a person or people" }).check();
            await report.getByRole("button", { name: "Continue to details" }).click();
            await report.getByRole("combobox", { name: "When did this happen?" }).click();
            await page.getByRole("option", { name: "Date only" }).click();
            await report.getByRole("button", { name: "Review report" }).click();
            const narrative = report.locator("[data-ellai-control='report.narrative']");
            await narrative.locator(".mud-input-root").waitFor({ state: "visible" });
            await page.waitForFunction(() => document.querySelector("[data-ellai-control='report.narrative']")?.classList.contains("guided-report__current-validation"));
            assert.equal(await narrative.evaluate(element => document.activeElement === element), true);
            assert.match(await report.getByRole("alert").innerText(), /Describe what happened or add an observation/);
            await report.getByLabel("Describe what happened").fill("Synthetic observation");
            await report.getByLabel("Describe what happened").press("Tab");
            const dateTarget = report.locator(".guided-report__current-validation");
            await page.waitForFunction(() => document.querySelector(".guided-report__current-validation input[type=date]") !== null);
            assert.equal(await dateTarget.count(), 1);
            assert.match(await report.getByRole("alert").innerText(), /reported date/i);
            assert.equal(await dateTarget.evaluate(element => document.activeElement === element), true);
            assert.match(await page.getByTestId("report-callback-status").innerText(), /Synthetic step callback/);
            assert.deepEqual(failures, []);
            await page.close();
        }
        process.stdout.write("PASS: guided validation highlights and focuses one field at a time at 1280px/light and 390px/dark.\n");
    } catch (error) {
        process.stderr.write(`Fixture host output:\n${output}\n`);
        throw error;
    } finally {
        if (browser) await browser.close();
        host.kill();
    }
}

run().catch(error => { console.error(error); process.exitCode = 1; });
