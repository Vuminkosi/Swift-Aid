// Small web end-to-end for linked sign-in methods (D04/W-ACC-01).
// Spawns the real Gateway (Release) against the dev compose database and
// verifies the fail-closed signed-out surface: 401 identities list with
// no-store, provider registry reachable, and the /sign-in-methods page
// rendering its signed-out prompt without page errors. Signed-in link,
// reauthentication and unlink flows need interactive provider consent and
// stay NOT VERIFIED here.
const assert = require("node:assert/strict");
const path = require("node:path");
const { spawn } = require("node:child_process");
const { chromium } = (() => {
    try {
        return require("playwright");
    } catch {
        return require("playwright-core");
    }
})();

const gatewayDll = path.resolve("src/SwiftAid.Gateway/bin/Release/net10.0/SwiftAid.Gateway.dll");
const gatewayContentRoot = path.resolve("src/SwiftAid.Gateway");

async function startHost(host) {
    let output = "";
    host.stdout.on("data", chunk => { output += chunk.toString(); });
    host.stderr.on("data", chunk => { output += chunk.toString(); });
    const deadline = Date.now() + 90000;
    let baseUrl;

    while (Date.now() < deadline) {
        if (host.exitCode !== null) throw new Error(`Gateway child PID ${host.pid} exited before readiness (${host.exitCode}). ${output}`);
        const listener = output.match(/Now listening on:\s*(http:\/\/127\.0\.0\.1:\d+)/);
        if (listener) {
            baseUrl = listener[1];
            try {
                const response = await fetch(`${baseUrl}/auth/providers`);
                if (response.ok) return { baseUrl, output, childPid: host.pid };
            } catch {
                // Kestrel may need a moment after announcing its listener.
            }
        }
        await new Promise(resolve => setTimeout(resolve, 500));
    }

    throw new Error(`Timed out waiting for Gateway child PID ${host.pid}. ${output}`);
}

async function main() {
    const host = spawn("dotnet", [gatewayDll, "--urls", "http://127.0.0.1:0"], {
        cwd: gatewayContentRoot,
        stdio: ["ignore", "pipe", "pipe"],
        windowsHide: true,
        env: { ...process.env, ASPNETCORE_ENVIRONMENT: "Development" }
    });

    let browser;
    try {
        const { baseUrl } = await startHost(host);

        const identities = await fetch(`${baseUrl}/auth/identities`, {
            headers: { Accept: "application/json" }
        });
        assert.equal(identities.status, 401, "signed-out identities list must fail closed with 401");
        const cacheControl = identities.headers.get("cache-control") || "";
        assert.ok(cacheControl.includes("no-store"), `identities 401 must carry no-store (got: ${cacheControl})`);

        const providers = await fetch(`${baseUrl}/auth/providers`, {
            headers: { Accept: "application/json" }
        });
        assert.equal(providers.status, 200, "provider registry must be reachable");
        const providerList = await providers.json();
        assert.ok(Array.isArray(providerList), "provider registry must be a JSON array");

        browser = await chromium.launch({ channel: "chrome", headless: true });
        const context = await browser.newContext({ viewport: { width: 1280, height: 900 } });
        const page = await context.newPage();
        const pageErrors = [];
        page.on("pageerror", error => pageErrors.push(error.message));

        await page.goto(`${baseUrl}/sign-in-methods`, { waitUntil: "domcontentloaded" });
        await page.getByRole("heading", { name: "Sign-in methods" }).waitFor({ timeout: 15000 });
        await page.getByRole("link", { name: "Sign in" }).first().waitFor({ timeout: 5000 });
        assert.deepEqual(pageErrors, [], `Page errors: ${JSON.stringify(pageErrors)}`);
        await context.close();

        console.log(JSON.stringify({ result: "PASS", baseUrl, providerCount: providerList.length }));
    } finally {
        if (browser) await browser.close();
        if (host.exitCode === null) {
            host.kill();
            await new Promise(resolve => host.once("exit", resolve));
        }
    }
}

main().catch(error => {
    console.error(error.stack || error);
    process.exitCode = 1;
});
