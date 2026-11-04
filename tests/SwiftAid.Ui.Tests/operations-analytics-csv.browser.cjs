// Focused real-browser download check for the synthetic TestHost CSV route.
// This validates CSV cell safety only; it does not exercise other workflows.
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const { spawn } = require("node:child_process");
const { chromium } = require("playwright");

const hostDll = path.resolve("tests/SwiftAid.Ui.TestHost/bin/Debug/net10.0/SwiftAid.Ui.TestHost.dll");
const hostContentRoot = path.resolve("tests/SwiftAid.Ui.TestHost");
const routePath = "/operations-analytics-csv";
const artifactDirectory = path.join(__dirname, "artifacts", "operations-analytics-csv-20261028");
fs.mkdirSync(artifactDirectory, { recursive: true });

async function startHost(child) {
    let output = "";
    child.stdout.on("data", chunk => { output += chunk.toString(); });
    child.stderr.on("data", chunk => { output += chunk.toString(); });
    const deadline = Date.now() + 30000;

    while (Date.now() < deadline) {
        if (child.exitCode !== null) {
            throw new Error(`TestHost child PID ${child.pid} exited before readiness (${child.exitCode}).\n${output}`);
        }

        const listener = output.match(/Now listening on:\s*(http:\/\/127\.0\.0\.1:\d+)/);
        if (listener) {
            const baseUrl = listener[1];
            try {
                const response = await fetch(`${baseUrl}${routePath}`);
                if (response.ok) {
                    const markup = await response.text();
                    if (!markup.includes("Reports and analytics") || !markup.includes("Download service report as CSV")) {
                        throw new Error(`TestHost PID ${child.pid} did not serve the analytics CSV fixture at ${baseUrl}${routePath}`);
                    }
                    return { baseUrl, output, childPid: child.pid };
                }
            } catch (error) {
                if (error.message.includes("did not serve the analytics CSV fixture")) throw error;
            }
        }

        await new Promise(resolve => setTimeout(resolve, 200));
    }

    throw new Error(`Timed out waiting for TestHost child PID ${child.pid}.\n${output}`);
}

function parseCsv(csv) {
    const rows = [];
    let row = [];
    let cell = "";
    let insideQuotes = false;

    for (let index = 0; index < csv.length; index++) {
        const character = csv[index];
        if (insideQuotes) {
            if (character === '"') {
                if (csv[index + 1] === '"') {
                    cell += '"';
                    index++;
                } else {
                    insideQuotes = false;
                }
            } else {
                cell += character;
            }
            continue;
        }

        if (character === '"' && cell.length === 0) {
            insideQuotes = true;
        } else if (character === ",") {
            row.push(cell);
            cell = "";
        } else if (character === "\r" || character === "\n") {
            if (character === "\r" && csv[index + 1] === "\n") index++;
            row.push(cell);
            rows.push(row);
            row = [];
            cell = "";
        } else {
            cell += character;
        }
    }

    if (insideQuotes) throw new Error("Downloaded CSV ended inside a quoted cell");
    if (cell.length > 0 || row.length > 0) {
        row.push(cell);
        rows.push(row);
    }
    return rows;
}

function expectedMetricRows() {
    return [
        ["Whitespace-prefixed value", "' =1+1", "1", "items", "Stable"],
        ["Tab-prefixed value", "'\t+1+1", "2", "items", "Stable"],
        ["Control-prefixed value", "'\r\n-1+1", "3", "items", "Stable"],
        ["BOM-prefixed value", "'\uFEFF@SUM(A1:A2)", "4", "items", "Stable"],
        ["Routine activity", "Community services", "5", "items", "Increasing"],
        ["Completed visits", "Operations", "23", "visits", "Not reported"]
    ];
}

async function main() {
    const host = spawn("dotnet", [hostDll, "--urls", "http://127.0.0.1:0"], {
        cwd: hostContentRoot,
        stdio: ["ignore", "pipe", "pipe"],
        windowsHide: true,
        env: { ...process.env, ASPNETCORE_ENVIRONMENT: "Development" }
    });

    let browser;
    try {
        const hostIdentity = await startHost(host);
        const { baseUrl } = hostIdentity;
        browser = await chromium.launch({ channel: "chrome", headless: true });
        const context = await browser.newContext({ acceptDownloads: true, viewport: { width: 1280, height: 900 } });
        const page = await context.newPage();
        const pageErrors = [];
        const consoleErrors = [];
        const externalRequests = [];
        const apiRequests = [];
        page.on("pageerror", error => pageErrors.push(error.message));
        page.on("console", message => {
            if (message.type() === "error") consoleErrors.push({ text: message.text(), location: message.location() });
        });
        page.on("request", request => {
            const url = request.url();
            if (/^https?:\/\/(?!127\.0\.0\.1)/i.test(url)) externalRequests.push(url);
            if (/\/api(?:\/|\?|$)/i.test(new URL(url).pathname)) apiRequests.push(url);
        });

        await page.goto(`${baseUrl}${routePath}`, { waitUntil: "networkidle" });
        await page.getByRole("heading", { name: "Reports and analytics" }).waitFor({ state: "visible" });
        const downloadLink = page.getByRole("link", { name: "Download service report as CSV" });
        const linkHref = await downloadLink.getAttribute("href");
        assert.match(linkHref, /^data:text\/csv;charset=utf-8,/i, "Download must be an ordinary local CSV data link");
        const pageScreenshot = path.join(artifactDirectory, "operations-analytics-csv.png");
        await page.screenshot({ path: pageScreenshot, fullPage: true });

        const [download] = await Promise.all([
            page.waitForEvent("download"),
            downloadLink.click()
        ]);
        assert.equal(download.suggestedFilename(), "swiftaid-service-report.csv");
        const downloadedCsvPath = path.join(artifactDirectory, download.suggestedFilename());
        await download.saveAs(downloadedCsvPath);
        const downloadedCsv = fs.readFileSync(downloadedCsvPath, "utf8");
        const rows = parseCsv(downloadedCsv);
        assert.deepEqual(rows[0], ["SwiftAid service report"]);
        assert.deepEqual(rows[1], ["Period", "the last 30 days"]);
        assert.deepEqual(rows[2], ["Updated at UTC", "Not provided"]);
        assert.deepEqual(rows[3], ["Measure", "Category", "Value", "Unit", "Trend"]);
        assert.deepEqual(rows.slice(4), expectedMetricRows(),
            "CSV metric labels, formula-safe categories, values, units, and trends must match the fixture exactly");

        const dangerousCells = rows.slice(4, 8).map(row => row[1]);
        assert.deepEqual(dangerousCells, [
            "' =1+1",
            "'\t+1+1",
            "'\r\n-1+1",
            "'\uFEFF@SUM(A1:A2)"
        ], "Leading whitespace, tab, CRLF, or BOM before =,+,-,@ must be apostrophe-prefixed");
        assert.deepEqual(rows.slice(8).map(row => row[1]), ["Community services", "Operations"],
            "Benign category cells must remain unchanged");
        assert.deepEqual(pageErrors, [], `Browser page errors: ${JSON.stringify(pageErrors)}`);
        assert.deepEqual(externalRequests, [], `Unexpected external requests: ${JSON.stringify(externalRequests)}`);
        assert.deepEqual(apiRequests, [], `Unexpected API requests: ${JSON.stringify(apiRequests)}`);

        const evidence = {
            route: `${baseUrl}${routePath}`,
            hostPid: hostIdentity.childPid,
            downloadFilename: download.suggestedFilename(),
            downloadedCsvPath,
            parsedRows: rows,
            dangerousCells,
            benignCells: rows.slice(8).map(row => row[1]),
            pageErrors,
            consoleErrors,
            externalRequests,
            apiRequests,
            screenshots: [pageScreenshot]
        };
        const evidencePath = path.join(artifactDirectory, "operations-analytics-csv-evidence.json");
        fs.writeFileSync(evidencePath, JSON.stringify(evidence, null, 2));
        console.log(JSON.stringify({ result: "PASS", evidencePath, evidence }, null, 2));
        await context.close();
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
