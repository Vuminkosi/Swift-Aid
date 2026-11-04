// Focused real-browser checks for the production guest-storage IndexedDB module.
// This harness exercises storage APIs directly; it does not render a product or demo surface.
const assert = require("node:assert/strict");
const fs = require("node:fs");
const http = require("node:http");
const path = require("node:path");
const { chromium } = require("playwright");

const modulePath = path.resolve("src/SwiftAid.Browser/wwwroot/js/guest-storage.js");
const databaseName = "swiftaid-guest-v1";
const keyId = "guest-content-key-v1";
const submissionA = "10000000-0000-4000-8000-000000000001";
const submissionB = "10000000-0000-4000-8000-000000000002";
const attachmentId = "20000000-0000-4000-8000-000000000001";

function createServer() {
    return http.createServer((request, response) => {
        if (request.url === "/guest-storage.js") {
            response.writeHead(200, { "content-type": "text/javascript; charset=utf-8", "cache-control": "no-store" });
            response.end(fs.readFileSync(modulePath));
            return;
        }
        if (request.url === "/") {
            response.writeHead(200, { "content-type": "text/html; charset=utf-8" });
            response.end("<!doctype html><html><head><meta charset=\"utf-8\"></head><body>Storage test harness</body></html>");
            return;
        }
        response.writeHead(404);
        response.end();
    });
}

async function listen(server) {
    await new Promise((resolve, reject) => {
        server.once("error", reject);
        server.listen(0, "127.0.0.1", resolve);
    });
    return `http://127.0.0.1:${server.address().port}`;
}

async function createPage(browser, baseUrl) {
    const context = await browser.newContext();
    const page = await context.newPage();
    await page.goto(baseUrl);
    const availability = await page.evaluate(() => ({
        secure: globalThis.isSecureContext,
        indexedDb: typeof indexedDB !== "undefined",
        subtleCrypto: !!globalThis.crypto?.subtle
    }));
    assert.deepEqual(availability, { secure: true, indexedDb: true, subtleCrypto: true });
    await page.evaluate(async () => {
        const module = await import("/guest-storage.js");
        await module.guestStorage.initialize();
    });
    return { context, page };
}

function makeRecord(id, version, note, state = "Draft", audioIds = []) {
    return {
        clientSubmissionId: id,
        version,
        state,
        note,
        submission: { originalAudioAttachmentIds: audioIds },
        audioAssets: [],
        updatedAtUtc: "2026-01-01T00:00:00.000Z"
    };
}

async function saveNew(page, record) {
    const result = await page.evaluate(async recordValue => {
        const { guestStorage } = await import("/guest-storage.js");
        return guestStorage.saveBatchVersionedResult([{ record: recordValue, expectedVersion: null }]);
    }, record);
    assert.deepEqual(result, { code: "saved" });
}

async function getRecord(page, id) {
    return page.evaluate(async recordId => {
        const { guestStorage } = await import("/guest-storage.js");
        return guestStorage.getRecord(recordId);
    }, id);
}

async function readRaw(page, storeName, id) {
    return page.evaluate(async ({ databaseName: dbName, name, key }) => {
        const db = await new Promise((resolve, reject) => {
            const request = indexedDB.open(dbName);
            request.onsuccess = () => resolve(request.result);
            request.onerror = () => reject(request.error);
        });
        try {
            return await new Promise((resolve, reject) => {
                const request = db.transaction(name).objectStore(name).get(key);
                request.onsuccess = () => resolve(request.result ?? null);
                request.onerror = () => reject(request.error);
            });
        } finally {
            db.close();
        }
    }, { databaseName, name: storeName, key: id });
}

async function run(browser, baseUrl, name, test) {
    await test(browser, baseUrl);
    process.stdout.write(`PASS ${name}\n`);
}

async function testEncryptedFormSnapshotSurvivesPageRestart(browser, baseUrl) {
    const { context, page } = await createPage(browser, baseUrl);
    try {
        const marker = 'SYNTHETIC-INCOMPLETE-FORM-SNAPSHOT';
        const record = { ...makeRecord(submissionA, 1, 'synthetic'),
            formSnapshot: { schemaVersion: 1, json: JSON.stringify({ occurrenceTime: marker }) } };
        await saveNew(page, record);
        const raw = await readRaw(page, 'records', submissionA);
        assert(!JSON.stringify(raw).includes(marker));
        await page.close();
        const restarted = await context.newPage();
        await restarted.goto(baseUrl);
        await restarted.evaluate(async () => { const { guestStorage } = await import('/guest-storage.js'); await guestStorage.initialize(); });
        assert.deepEqual((await getRecord(restarted, submissionA)).formSnapshot, record.formSnapshot);
    } finally {
        await context.close();
    }
}

async function testConcurrentVersionRace(browser, baseUrl) {
    const { context, page: first } = await createPage(browser, baseUrl);
    try {
        await saveNew(first, makeRecord(submissionA, 1, "initial"));
        const second = await context.newPage();
        await second.goto(baseUrl);
        await second.evaluate(async () => {
            const { guestStorage } = await import("/guest-storage.js");
            await guestStorage.initialize();
        });

        const update = (page, note) => page.evaluate(async ({ id, value }) => {
            const { guestStorage } = await import("/guest-storage.js");
            const current = await guestStorage.getRecord(id);
            const next = { ...current, version: current.version + 1, note: value };
            return guestStorage.saveBatchVersionedResult([{ record: next, expectedVersion: current.version }]);
        }, { id: submissionA, value: note });
        const results = await Promise.all([update(first, "writer one"), update(second, "writer two")]);
        assert.deepEqual(results.map(result => result.code).sort(), ["conflict", "saved"]);
        const stored = await getRecord(first, submissionA);
        assert.equal(stored.version, 2);
        assert.ok(["writer one", "writer two"].includes(stored.note));
    } finally {
        await context.close();
    }
}

async function testStaleBatchAtomicity(browser, baseUrl) {
    const { context, page } = await createPage(browser, baseUrl);
    try {
        await saveNew(page, makeRecord(submissionA, 1, "first original"));
        await saveNew(page, makeRecord(submissionB, 1, "second original"));
        const result = await page.evaluate(async ({ firstId, secondId }) => {
            const { guestStorage } = await import("/guest-storage.js");
            return guestStorage.saveBatchVersionedResult([
                { record: { ...(await guestStorage.getRecord(firstId)), version: 2, note: "must roll back" }, expectedVersion: 1 },
                { record: { ...(await guestStorage.getRecord(secondId)), version: 1, note: "stale member" }, expectedVersion: null }
            ]);
        }, { firstId: submissionA, secondId: submissionB });
        assert.equal(result.code, "conflict");
        assert.deepEqual(await getRecord(page, submissionA), makeRecord(submissionA, 1, "first original"));
        assert.deepEqual(await getRecord(page, submissionB), makeRecord(submissionB, 1, "second original"));
    } finally {
        await context.close();
    }
}

async function testMissingKeyWithDataFailsClosed(browser, baseUrl) {
    const { context, page } = await createPage(browser, baseUrl);
    try {
        await saveNew(page, makeRecord(submissionA, 1, "must remain encrypted"));
        await page.evaluate(async ({ databaseName: dbName, keyId: key }) => {
            const db = await new Promise((resolve, reject) => {
                const request = indexedDB.open(dbName);
                request.onsuccess = () => resolve(request.result);
                request.onerror = () => reject(request.error);
            });
            try {
                await new Promise((resolve, reject) => {
                    const tx = db.transaction("keys", "readwrite");
                    tx.objectStore("keys").delete(key);
                    tx.oncomplete = resolve;
                    tx.onerror = () => reject(tx.error);
                    tx.onabort = () => reject(tx.error);
                });
            } finally {
                db.close();
            }
        }, { databaseName, keyId });

        const restarted = await context.newPage();
        await restarted.goto(baseUrl);
        const failure = await restarted.evaluate(async () => {
            try {
                const { guestStorage } = await import("/guest-storage.js");
                await guestStorage.initialize();
                return null;
            } catch (error) {
                return { name: error.name, code: error.code };
            }
        });
        assert.deepEqual(failure, { name: "GuestStorageError", code: "key-unavailable" });
        assert.equal(await readRaw(page, "keys", keyId), null, "missing key must not be silently replaced");
        const rawRecord = await readRaw(page, "records", submissionA);
        assert.equal(rawRecord.version, 1);
        assert.ok(rawRecord.envelope.ciphertext.length > 16);
    } finally {
        await context.close();
    }
}

async function testTamperedEnvelopeAndAadFailClosed(browser, baseUrl) {
    const { context, page } = await createPage(browser, baseUrl);
    try {
        await saveNew(page, makeRecord(submissionA, 1, "authenticated record"));
        await page.evaluate(async ({ databaseName: dbName, id }) => {
            const db = await new Promise((resolve, reject) => {
                const request = indexedDB.open(dbName);
                request.onsuccess = () => resolve(request.result);
                request.onerror = () => reject(request.error);
            });
            try {
                await new Promise((resolve, reject) => {
                    const tx = db.transaction("records", "readwrite");
                    const store = tx.objectStore("records");
                    const request = store.get(id);
                    request.onsuccess = () => {
                        const entry = request.result;
                        entry.envelope.version += 1; // Alters the authenticated additional data.
                        entry.envelope.ciphertext[0] ^= 0x01;
                        store.put(entry);
                    };
                    request.onerror = () => reject(request.error);
                    tx.oncomplete = resolve;
                    tx.onerror = () => reject(tx.error);
                    tx.onabort = () => reject(tx.error);
                });
            } finally {
                db.close();
            }
        }, { databaseName, id: submissionA });

        const failure = await page.evaluate(async id => {
            try {
                const { guestStorage } = await import("/guest-storage.js");
                await guestStorage.getRecord(id);
                return null;
            } catch (error) {
                return { name: error.name, code: error.code };
            }
        }, submissionA);
        assert.deepEqual(failure, { name: "GuestStorageError", code: "corrupt" });
    } finally {
        await context.close();
    }
}

async function testAudioAndRecordCasAreAtomic(browser, baseUrl) {
    const { context, page } = await createPage(browser, baseUrl);
    try {
        const original = makeRecord(submissionA, 1, "draft", "Draft", [attachmentId]);
        await saveNew(page, original);
        const competing = { ...original, version: 2, note: "newer revision" };
        const competingResult = await page.evaluate(async record => {
            const { guestStorage } = await import("/guest-storage.js");
            return guestStorage.saveBatchVersionedResult([{ record, expectedVersion: 1 }]);
        }, competing);
        assert.deepEqual(competingResult, { code: "saved" });

        const staleResult = await page.evaluate(async ({ record, attachment }) => {
            const { guestStorage } = await import("/guest-storage.js");
            return guestStorage.saveAudioForDraftResult(
                record, 1, attachment, "audio/webm", 1200, new Uint8Array([1, 2, 3, 4]));
        }, { record: original, attachment: attachmentId });
        assert.equal(staleResult.code, "conflict");
        const afterConflict = await getRecord(page, submissionA);
        assert.equal(afterConflict.version, 2);
        assert.deepEqual(afterConflict.audioAssets, []);
        assert.equal(await page.evaluate(async ({ id, attachment }) => {
            const { guestStorage } = await import("/guest-storage.js");
            return guestStorage.readAudio(id, attachment);
        }, { id: submissionA, attachment: attachmentId }), null);

        const bytes = [11, 22, 33, 44, 55];
        const success = await page.evaluate(async ({ id, attachment, content }) => {
            const { guestStorage } = await import("/guest-storage.js");
            const current = await guestStorage.getRecord(id);
            return guestStorage.saveAudioForDraftResult(
                current, current.version, attachment, "audio/webm", 1500, new Uint8Array(content));
        }, { id: submissionA, attachment: attachmentId, content: bytes });
        assert.equal(success.code, "saved");
        const committedRecord = await getRecord(page, submissionA);
        assert.equal(committedRecord.version, 3);
        assert.equal(committedRecord.audioAssets.length, 1);
        assert.equal(committedRecord.audioAssets[0].byteLength, bytes.length);
        assert.deepEqual(await page.evaluate(async ({ id, attachment }) => {
            const { guestStorage } = await import("/guest-storage.js");
            const saved = await guestStorage.readAudio(id, attachment);
            return Array.from(new Uint8Array(saved.bytes));
        }, { id: submissionA, attachment: attachmentId }), bytes);
    } finally {
        await context.close();
    }
}

async function testCleanupRestartAndExactVersionMatching(browser, baseUrl) {
    const { context, page } = await createPage(browser, baseUrl);
    try {
        const oldDraft = makeRecord(submissionA, 1, "old draft", "Draft", [attachmentId]);
        await saveNew(page, oldDraft);
        const savedAudio = await page.evaluate(async ({ record, attachment }) => {
            const { guestStorage } = await import("/guest-storage.js");
            return guestStorage.saveAudioForDraftResult(
                record, 1, attachment, "audio/webm", 900, new Uint8Array([7, 8, 9]));
        }, { record: oldDraft, attachment: attachmentId });
        assert.equal(savedAudio.code, "saved");
        const audioRecord = await getRecord(page, submissionA);
        const superseded = { ...audioRecord, version: 3, state: "Superseded" };
        const supersedeResult = await page.evaluate(async record => {
            const { guestStorage } = await import("/guest-storage.js");
            return guestStorage.saveBatchVersionedResult([{ record, expectedVersion: 2 }]);
        }, superseded);
        assert.deepEqual(supersedeResult, { code: "saved" });
        await saveNew(page, makeRecord(submissionB, 1, "replacement draft"));

        const intents = [
            { intentKey: "cleanup-wrong-old-version", supersededSubmissionId: submissionA, expectedSupersededVersion: 2,
                replacementSubmissionId: submissionB, expectedReplacementVersion: 1, attachmentId },
            { intentKey: "cleanup-wrong-replacement-version", supersededSubmissionId: submissionA, expectedSupersededVersion: 3,
                replacementSubmissionId: submissionB, expectedReplacementVersion: 2, attachmentId },
            { intentKey: "cleanup-exact-version-match", supersededSubmissionId: submissionA, expectedSupersededVersion: 3,
                replacementSubmissionId: submissionB, expectedReplacementVersion: 1, attachmentId }
        ];
        const journaled = await page.evaluate(async values => {
            const { guestStorage } = await import("/guest-storage.js");
            const results = [];
            for (const value of values) results.push(await guestStorage.ensureCleanupPendingResult(value));
            return results;
        }, intents);
        assert.deepEqual(journaled.map(result => result.code), ["pending", "pending", "pending"]);

        const restarted = await context.newPage();
        await restarted.goto(baseUrl);
        const recovered = await restarted.evaluate(async () => {
            const { guestStorage } = await import("/guest-storage.js");
            await guestStorage.initialize();
            return guestStorage.getPendingCleanupResult();
        });
        assert.equal(recovered.code, "ok");
        assert.deepEqual(recovered.intents.map(intent => intent.intentKey).sort(), intents.map(intent => intent.intentKey).sort());

        const wrongVersions = await restarted.evaluate(async keys => {
            const { guestStorage } = await import("/guest-storage.js");
            return Promise.all(keys.map(key => guestStorage.completeAudioCleanupResult(key)));
        }, [intents[0].intentKey, intents[1].intentKey]);
        assert.deepEqual(wrongVersions.map(result => result.code), ["not-safe", "not-safe"]);
        assert.ok(await restarted.evaluate(async ({ id, attachment }) => {
            const { guestStorage } = await import("/guest-storage.js");
            return (await guestStorage.readAudio(id, attachment)) !== null;
        }, { id: submissionA, attachment: attachmentId }));

        const exact = await restarted.evaluate(async key => {
            const { guestStorage } = await import("/guest-storage.js");
            return guestStorage.completeAudioCleanupResult(key);
        }, intents[2].intentKey);
        assert.deepEqual(exact, { code: "removed" });
        assert.deepEqual(await restarted.evaluate(async key => {
            const { guestStorage } = await import("/guest-storage.js");
            return guestStorage.getCleanupStatusResult(key);
        }, intents[2].intentKey), { code: "completed" });
        assert.equal(await restarted.evaluate(async ({ id, attachment }) => {
            const { guestStorage } = await import("/guest-storage.js");
            return guestStorage.readAudio(id, attachment);
        }, { id: submissionA, attachment: attachmentId }), null);
    } finally {
        await context.close();
    }
}

async function main() {
    const server = createServer();
    const baseUrl = await listen(server);
    let browser;
    try {
        browser = await chromium.launch({ channel: "chrome", headless: true });
        await run(browser, baseUrl, "encrypted form snapshot survives page restart", testEncryptedFormSnapshotSurvivesPageRestart);
        await run(browser, baseUrl, "two-instance same-version CAS has one winner", testConcurrentVersionRace);
        await run(browser, baseUrl, "stale member batch aborts all writes", testStaleBatchAtomicity);
        await run(browser, baseUrl, "missing key with encrypted data fails closed", testMissingKeyWithDataFailsClosed);
        await run(browser, baseUrl, "tampered envelope and authenticated data fail closed", testTamperedEnvelopeAndAadFailClosed);
        await run(browser, baseUrl, "audio and draft metadata share one CAS transaction", testAudioAndRecordCasAreAtomic);
        await run(browser, baseUrl, "cleanup journal survives restart and enforces both exact versions", testCleanupRestartAndExactVersionMatching);
        process.stdout.write("PASS all focused browser guest-storage checks\n");
    } finally {
        if (browser) await browser.close();
        await new Promise(resolve => server.close(resolve));
    }
}

main().catch(error => {
    process.stderr.write(`${error.stack ?? error}\n`);
    process.exitCode = 1;
});
