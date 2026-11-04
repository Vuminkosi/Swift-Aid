const DATABASE_NAME = "swiftaid-guest-v1";
const DATABASE_VERSION = 2;
const STORE_KEYS = "keys";
const STORE_RECORDS = "records";
const STORE_AUDIO = "audio";
const STORE_JOURNAL = "journal";
const KEY_ID = "guest-content-key-v1";
const ENVELOPE_VERSION = 1;
const MAX_RECORDS = 256;
const MAX_RECORD_BYTES = 2 * 1024 * 1024;
const MAX_AUDIO_BYTES = 10 * 1024 * 1024;
const MAX_AUDIO_DURATION_MS = 120_000;
const encoder = new TextEncoder();
const decoder = new TextDecoder("utf-8", { fatal: true });

let databasePromise;
let keyPromise;

/**
 * Browser-only encrypted storage for guest report records, original audio, and cleanup intents.
 * The caller must use the typed GuestSubmissionPersistence adapter; this module never submits data.
 */
export const guestStorage = Object.freeze({
    initialize,
    getRecord,
    getAllRecords,
    saveVersioned,
    saveBatchVersioned,
    saveBatchVersionedResult,
    saveAudioForDraft,
    saveAudioForDraftResult,
    readAudio,
    ensureCleanupPending,
    ensureCleanupPendingResult,
    getPendingCleanup,
    getPendingCleanupResult,
    completeAudioCleanup,
    completeAudioCleanupResult,
    getCleanupStatusResult,
    cancelCleanup,
    cancelCleanupResult,
    get status() {
        return storageStatus();
    }
});

async function initialize() {
    await getDatabase();
    await getContentKey();
    return storageStatus();
}

function storageStatus() {
    const supported = globalThis.isSecureContext === true &&
        !!globalThis.crypto?.subtle &&
        typeof globalThis.indexedDB !== "undefined";
    return Object.freeze({ available: supported, durable: false });
}

function requireAvailable() {
    if (!storageStatus().available) {
        throw storageError("unavailable");
    }
}

function storageError(code, conflictId) {
    const error = new Error("Guest storage is unavailable.");
    error.name = code === "conflict" ? "GuestStorageConflictError" : "GuestStorageError";
    error.code = code;
    if (conflictId) error.conflictId = conflictId;
    return error;
}

function getDatabase() {
    requireAvailable();
    databasePromise ??= new Promise((resolve, reject) => {
        const request = indexedDB.open(DATABASE_NAME, DATABASE_VERSION);
        request.onupgradeneeded = () => {
            const db = request.result;
            if (!db.objectStoreNames.contains(STORE_KEYS)) db.createObjectStore(STORE_KEYS);
            if (!db.objectStoreNames.contains(STORE_RECORDS)) db.createObjectStore(STORE_RECORDS, { keyPath: "id" });
            const audio = db.objectStoreNames.contains(STORE_AUDIO)
                ? request.transaction.objectStore(STORE_AUDIO)
                : db.createObjectStore(STORE_AUDIO, { keyPath: "id" });
            if (!audio.indexNames.contains("attachmentId")) {
                audio.createIndex("attachmentId", "attachmentId", { unique: false });
            }
            if (!db.objectStoreNames.contains(STORE_JOURNAL)) db.createObjectStore(STORE_JOURNAL, { keyPath: "id" });
        };
        request.onsuccess = () => {
            request.result.onversionchange = () => request.result.close();
            resolve(request.result);
        };
        request.onerror = () => reject(storageError("unavailable"));
        request.onblocked = () => reject(storageError("unavailable"));
    }).catch(() => {
        databasePromise = undefined;
        throw storageError("unavailable");
    });
    return databasePromise;
}

async function getContentKey() {
    if (keyPromise) return keyPromise;
    keyPromise = (async () => {
        requireAvailable();
        const db = await getDatabase();
        const candidate = await crypto.subtle.generateKey(
            { name: "AES-GCM", length: 256 },
            false,
            ["encrypt", "decrypt"]);

        return new Promise((resolve, reject) => {
            const tx = db.transaction([STORE_KEYS, STORE_RECORDS, STORE_AUDIO, STORE_JOURNAL], "readwrite");
            const keys = tx.objectStore(STORE_KEYS);
            let foundKey;
            let pending = 4;
            let initialized = false;
            let failure;

            const settle = () => {
                pending--;
                if (pending !== 0 || initialized || failure) return;
                initialized = true;
                if (foundKey) return;
                const requests = [
                    tx.objectStore(STORE_RECORDS).count(),
                    tx.objectStore(STORE_AUDIO).count(),
                    tx.objectStore(STORE_JOURNAL).count()
                ];
                let countsPending = requests.length;
                let hasData = false;
                for (const countRequest of requests) {
                    countRequest.onsuccess = () => {
                        hasData ||= countRequest.result > 0;
                        countsPending--;
                        if (countsPending !== 0) return;
                        if (hasData) {
                            failure = storageError("key-unavailable");
                            tx.abort();
                            return;
                        }
                        keys.put(candidate, KEY_ID);
                    };
                    countRequest.onerror = () => {
                        failure = storageError("unavailable");
                        tx.abort();
                    };
                }
            };

            const keyRequest = keys.get(KEY_ID);
            keyRequest.onsuccess = () => { foundKey = keyRequest.result; settle(); };
            keyRequest.onerror = () => { failure = storageError("unavailable"); tx.abort(); };
            for (const name of [STORE_RECORDS, STORE_AUDIO, STORE_JOURNAL]) {
                const countRequest = tx.objectStore(name).count();
                countRequest.onsuccess = settle;
                countRequest.onerror = () => { failure = storageError("unavailable"); tx.abort(); };
            }

            tx.oncomplete = () => resolve(foundKey ?? candidate);
            tx.onabort = () => reject(failure ?? storageError("unavailable"));
            tx.onerror = () => { failure ??= storageError("unavailable"); };
        });
    })().catch(error => {
        keyPromise = undefined;
        throw error?.code ? error : storageError("unavailable");
    });
    return keyPromise;
}

function recordId(record) {
    const id = record?.clientSubmissionId ?? record?.ClientSubmissionId;
    if (typeof id !== "string" || !/^[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i.test(id)) {
        throw storageError("invalid-request");
    }
    return id.toLowerCase();
}

function recordVersion(record) {
    const version = record?.version ?? record?.Version;
    if (!Number.isSafeInteger(version) || version < 1) throw storageError("invalid-request");
    return version;
}

function validateWrite(write) {
    const record = write?.record ?? write?.Record;
    const id = recordId(record);
    const version = recordVersion(record);
    const expectedVersion = write.expectedVersion ?? null;
    if (expectedVersion !== null && (!Number.isSafeInteger(expectedVersion) || expectedVersion < 1)) {
        throw storageError("invalid-request");
    }
    const requiredVersion = expectedVersion === null ? 1 : expectedVersion + 1;
    if (!Number.isSafeInteger(requiredVersion) || version !== requiredVersion) throw storageError("invalid-request");
    const bytes = encoder.encode(JSON.stringify(record));
    if (bytes.byteLength > MAX_RECORD_BYTES) throw storageError("invalid-request");
    return { id, version, expectedVersion, bytes };
}

async function encrypt(purpose, id, version, bytes) {
    const key = await getContentKey();
    const iv = crypto.getRandomValues(new Uint8Array(12));
    const aad = encoder.encode(`SwiftAid.Guest.${purpose}.v${ENVELOPE_VERSION}|${id}|${version}`);
    const ciphertext = await crypto.subtle.encrypt({ name: "AES-GCM", iv, additionalData: aad, tagLength: 128 }, key, bytes);
    return {
        envelopeVersion: ENVELOPE_VERSION,
        version,
        iv,
        ciphertext: new Uint8Array(ciphertext)
    };
}

async function decrypt(purpose, id, envelope) {
    if (!envelope || envelope.envelopeVersion !== ENVELOPE_VERSION ||
        !Number.isSafeInteger(envelope.version) || envelope.version < 1 ||
        !isByteArray(envelope.iv) || envelope.iv.length !== 12 ||
        !isByteArray(envelope.ciphertext) || envelope.ciphertext.length < 16) {
        throw storageError("corrupt");
    }
    try {
        const key = await getContentKey();
        const iv = Uint8Array.from(envelope.iv);
        const aad = encoder.encode(`SwiftAid.Guest.${purpose}.v${ENVELOPE_VERSION}|${id}|${envelope.version}`);
        const clear = await crypto.subtle.decrypt(
            { name: "AES-GCM", iv, additionalData: aad, tagLength: 128 },
            key,
            Uint8Array.from(envelope.ciphertext));
        return new Uint8Array(clear);
    } catch {
        throw storageError("corrupt");
    }
}

async function decodeRecord(entry) {
    if (!entry || typeof entry.id !== "string" || !Number.isSafeInteger(entry.version) || entry.version < 1) {
        throw storageError("corrupt");
    }
    const bytes = await decrypt("record", entry.id, entry.envelope);
    try {
        const record = JSON.parse(decoder.decode(bytes));
        if (recordId(record) !== entry.id || recordVersion(record) !== entry.version) throw storageError("corrupt");
        return record;
    } catch {
        throw storageError("corrupt");
    }
}

export async function getRecord(id) {
    if (typeof id !== "string") throw storageError("invalid-request");
    await getContentKey();
    const db = await getDatabase();
    const entry = await requestResult(db.transaction(STORE_RECORDS).objectStore(STORE_RECORDS).get(id.toLowerCase()));
    return entry ? decodeRecord(entry) : null;
}

export async function getAllRecords() {
    await getContentKey();
    const db = await getDatabase();
    const entries = await requestResult(db.transaction(STORE_RECORDS).objectStore(STORE_RECORDS).getAll());
    const results = [];
    for (const entry of entries) results.push(await decodeRecord(entry));
    return results;
}

async function saveVersioned(record, expectedVersion) {
    await saveBatchVersioned([{ record, expectedVersion }]);
}

async function saveBatchVersioned(writes) {
    if (!Array.isArray(writes) || writes.length === 0 || writes.length > MAX_RECORDS) {
        throw storageError("invalid-request");
    }
    const prepared = writes.map(validateWrite);
    if (new Set(prepared.map(item => item.id)).size !== prepared.length) throw storageError("invalid-request");
    const db = await getDatabase();
    let entries;
    try {
        entries = await Promise.all(prepared.map(async item => ({
            ...item,
            envelope: await encrypt("record", item.id, item.version, item.bytes)
        })));
    } finally {
        for (const item of prepared) item.bytes.fill(0);
    }

    return new Promise((resolve, reject) => {
        const tx = db.transaction(STORE_RECORDS, "readwrite");
        const store = tx.objectStore(STORE_RECORDS);
        const current = new Map();
        let remaining = entries.length;
        let failure;
        for (const item of entries) {
            const request = store.get(item.id);
            request.onsuccess = () => {
                current.set(item.id, request.result);
                remaining--;
                if (remaining !== 0 || failure) return;
                for (const entry of entries) {
                    const old = current.get(entry.id);
                    const actualVersion = old?.version ?? null;
                    if (actualVersion !== entry.expectedVersion) {
                        failure = storageError("conflict", entry.id);
                        tx.abort();
                        return;
                    }
                }
                const additions = entries.filter(entry => !current.get(entry.id)).length;
                const countRequest = store.count();
                countRequest.onsuccess = () => {
                    if (countRequest.result + additions > MAX_RECORDS) {
                        failure = storageError("unavailable");
                        tx.abort();
                        return;
                    }
                    for (const entry of entries) {
                        store.put({ id: entry.id, version: entry.version, envelope: entry.envelope });
                    }
                };
                countRequest.onerror = () => { failure = storageError("unavailable"); tx.abort(); };
            };
            request.onerror = () => { failure = storageError("unavailable"); tx.abort(); };
        }
        tx.oncomplete = () => resolve();
        tx.onabort = () => reject(failure ?? storageError("unavailable"));
        tx.onerror = () => { failure ??= storageError("unavailable"); };
    });
}

async function saveAudioForDraft(record, expectedVersion, attachmentId, mediaType, durationMilliseconds, content) {
    const submissionKey = recordId(record);
    const attachmentKey = normalizeUuid(attachmentId);
    const currentVersion = recordVersion(record);
    const submission = record.submission ?? record.Submission;
    const submissionState = record.state ?? record.State;
    if (!Number.isSafeInteger(expectedVersion) || expectedVersion < 1 ||
        currentVersion !== expectedVersion ||
        (submissionState !== "Draft" && submissionState !== 0 &&
            submissionState !== "NeedsAttention" && submissionState !== 3) ||
        !Array.isArray(submission?.originalAudioAttachmentIds ?? submission?.OriginalAudioAttachmentIds) ||
        !(submission.originalAudioAttachmentIds ?? submission.OriginalAudioAttachmentIds)
            .some(id => normalizeUuid(id) === attachmentKey)) {
        throw storageError("invalid-request");
    }
    if (!/^audio\/[a-z0-9.+-]+$/i.test(mediaType ?? "") ||
        !Number.isSafeInteger(durationMilliseconds) || durationMilliseconds < 1 || durationMilliseconds > MAX_AUDIO_DURATION_MS) {
        throw storageError("invalid-request");
    }
    const bytes = content instanceof ArrayBuffer ? new Uint8Array(content) : new Uint8Array(content.buffer, content.byteOffset, content.byteLength);
    let digest;
    try {
        if (bytes.byteLength < 1 || bytes.byteLength > MAX_AUDIO_BYTES) throw storageError("invalid-request");
        const id = `${submissionKey}:${attachmentKey}`;
        digest = new Uint8Array(await crypto.subtle.digest("SHA-256", bytes));
        const asset = {
            attachmentId: attachmentKey,
            sha256: toHex(digest),
            mediaType,
            byteLength: bytes.byteLength,
            durationMilliseconds,
            state: 0
        };
        const existingAssets = record.audioAssets ?? record.AudioAssets ?? [];
        const existingAsset = existingAssets.find(item =>
            normalizeUuid(item.attachmentId ?? item.AttachmentId) === attachmentKey);
        if (existingAsset) {
            const metadataMatches = (existingAsset.sha256 ?? existingAsset.Sha256) === asset.sha256 &&
                (existingAsset.mediaType ?? existingAsset.MediaType) === asset.mediaType &&
                Number(existingAsset.byteLength ?? existingAsset.ByteLength) === asset.byteLength &&
                Number(existingAsset.durationMilliseconds ?? existingAsset.DurationMilliseconds) === asset.durationMilliseconds;
            if (!metadataMatches) throw storageError("conflict", submissionKey);
            const saved = await readAudio(submissionKey, attachmentKey);
            if (!saved || saved.asset.sha256 !== asset.sha256) throw storageError("corrupt");
            new Uint8Array(saved.bytes).fill(0);
            return existingAsset;
        }
        if (existingAssets.length >= 2) throw storageError("invalid-request");

        const updatedRecord = {
            ...record,
            audioAssets: [...existingAssets, asset],
            updatedAtUtc: new Date().toISOString(),
            version: expectedVersion + 1
        };
        const recordBytes = encoder.encode(JSON.stringify(updatedRecord));
        let recordEnvelope;
        try {
            recordEnvelope = await encrypt("record", submissionKey, expectedVersion + 1, recordBytes);
        } finally {
            recordBytes.fill(0);
        }
        const iv = crypto.getRandomValues(new Uint8Array(12));
        const key = await getContentKey();
        const aad = encoder.encode(`SwiftAid.Guest.audio.v${ENVELOPE_VERSION}|${submissionKey}|${attachmentKey}`);
        const ciphertext = await crypto.subtle.encrypt({ name: "AES-GCM", iv, additionalData: aad, tagLength: 128 }, key, bytes);
        const entry = {
            id,
            submissionId: submissionKey,
            attachmentId: attachmentKey,
            sha256: asset.sha256,
            mediaType,
            byteLength: asset.byteLength,
            durationMilliseconds,
            envelopeVersion: ENVELOPE_VERSION,
            iv,
            ciphertext: new Uint8Array(ciphertext)
        };
        const db = await getDatabase();
        await putAudioAndRecord(
            db,
            submissionKey,
            expectedVersion,
            { id: submissionKey, version: expectedVersion + 1, envelope: recordEnvelope },
            entry,
            attachmentKey);
        return asset;
    } finally {
        digest?.fill(0);
        bytes.fill(0);
    }
}

export async function saveBatchVersionedResult(writes) {
    try {
        await saveBatchVersioned(writes);
        return { code: "saved" };
    } catch (error) {
        if (error?.code === "conflict") return { code: "conflict", conflictId: error.conflictId ?? null };
        if (error?.code === "invalid-request") return { code: "invalid-request" };
        return { code: "unavailable" };
    }
}

export async function saveAudioForDraftResult(...args) {
    try {
        return { code: "saved", asset: await saveAudioForDraft(...args) };
    } catch (error) {
        return { code: error?.code === "conflict" ? "conflict" : error?.code === "invalid-request" ? "invalid-request" : "unavailable" };
    }
}

export async function readAudio(submissionId, attachmentId) {
    const submissionKey = normalizeUuid(submissionId);
    const attachmentKey = normalizeUuid(attachmentId);
    const id = `${submissionKey}:${attachmentKey}`;
    await getContentKey();
    const db = await getDatabase();
    const entry = await requestResult(db.transaction(STORE_AUDIO).objectStore(STORE_AUDIO).get(id));
    if (!entry) return null;
    if (entry.submissionId !== submissionKey || entry.attachmentId !== attachmentKey ||
        entry.envelopeVersion !== ENVELOPE_VERSION || entry.byteLength > MAX_AUDIO_BYTES ||
        !isByteArray(entry.iv) || entry.iv.length !== 12 ||
        !isByteArray(entry.ciphertext) || entry.ciphertext.length < 16) throw storageError("corrupt");
    try {
        const key = await getContentKey();
        const aad = encoder.encode(`SwiftAid.Guest.audio.v${ENVELOPE_VERSION}|${submissionKey}|${attachmentKey}`);
        const clear = new Uint8Array(await crypto.subtle.decrypt(
            { name: "AES-GCM", iv: Uint8Array.from(entry.iv), additionalData: aad, tagLength: 128 },
            key,
            Uint8Array.from(entry.ciphertext)));
        const digest = new Uint8Array(await crypto.subtle.digest("SHA-256", clear));
        if (clear.byteLength !== entry.byteLength || toHex(digest) !== entry.sha256) {
            clear.fill(0);
            digest.fill(0);
            throw storageError("corrupt");
        }
        digest.fill(0);
        return { bytes: clear.buffer, asset: {
            attachmentId: entry.attachmentId,
            sha256: entry.sha256,
            mediaType: entry.mediaType,
            byteLength: entry.byteLength,
            durationMilliseconds: entry.durationMilliseconds,
            state: 0
        } };
    } catch {
        throw storageError("corrupt");
    }
}

async function ensureCleanupPending(intent) {
    const id = validateIntent(intent);
    const db = await getDatabase();
    const existing = await requestResult(db.transaction(STORE_JOURNAL).objectStore(STORE_JOURNAL).get(id));
    if (existing) return ensureIntentMatches(existing, intent, id);
    const version = 1;
    const bytes = encoder.encode(JSON.stringify({ ...intent, status: "pending", version }));
    const envelope = await encrypt("cleanup", id, version, bytes);
    bytes.fill(0);
    try {
        await transactionPutIfAbsent(db, STORE_JOURNAL, id, { id, version, envelope });
    } catch (error) {
        if (error?.code !== "conflict") throw error;
        const raced = await requestResult(db.transaction(STORE_JOURNAL).objectStore(STORE_JOURNAL).get(id));
        if (!raced) throw error;
        return ensureIntentMatches(raced, intent, id);
    }
}

async function ensureIntentMatches(entry, requested, id) {
    const bytes = await decrypt("cleanup", id, entry.envelope);
    let existing;
    try { existing = JSON.parse(decoder.decode(bytes)); }
    finally { bytes.fill(0); }
    if (existing.status !== "pending" || entry.version !== existing.version ||
        existing.supersededSubmissionId !== requested.supersededSubmissionId ||
        existing.expectedSupersededVersion !== requested.expectedSupersededVersion ||
        existing.replacementSubmissionId !== requested.replacementSubmissionId ||
        existing.expectedReplacementVersion !== requested.expectedReplacementVersion ||
        existing.attachmentId !== requested.attachmentId) {
        throw storageError("conflict", id);
    }
}

async function getPendingCleanup() {
    await getContentKey();
    const db = await getDatabase();
    const entries = await requestResult(db.transaction(STORE_JOURNAL).objectStore(STORE_JOURNAL).getAll());
    const pending = [];
    for (const entry of entries) {
        const bytes = await decrypt("cleanup", entry.id, entry.envelope);
        let intent;
        try { intent = JSON.parse(decoder.decode(bytes)); }
        catch { throw storageError("corrupt"); }
        finally { bytes.fill(0); }
        if (intent.version !== entry.version || validateIntent(intent) !== entry.id) throw storageError("corrupt");
        if (intent.status === "pending") pending.push(intent);
    }
    return pending;
}

export async function ensureCleanupPendingResult(intent) {
    try {
        await ensureCleanupPending(intent);
        return { code: "pending" };
    } catch (error) {
        return { code: error?.code === "conflict" ? "conflict" : error?.code === "invalid-request" ? "invalid-request" : "unavailable" };
    }
}

export async function getPendingCleanupResult() {
    try { return { code: "ok", intents: await getPendingCleanup() }; }
    catch { return { code: "unavailable", intents: [] }; }
}

async function completeAudioCleanup(intentKey) {
    if (typeof intentKey !== "string" || intentKey.length > 240) throw storageError("invalid-request");
    await getContentKey();
    const db = await getDatabase();
    const journalEntry = await requestResult(db.transaction(STORE_JOURNAL).objectStore(STORE_JOURNAL).get(intentKey));
    if (!journalEntry) return false;
    const intentBytes = await decrypt("cleanup", intentKey, journalEntry.envelope);
    let intent;
    try { intent = JSON.parse(decoder.decode(intentBytes)); }
    finally { intentBytes.fill(0); }
    if (validateIntent(intent) !== intentKey) return false;
    if (intent.status === "complete") return intent.cleanupOutcome === "retained" ? "retained" : "removed";
    if (intent.status !== "pending") return false;

    const supersededId = normalizeUuid(intent.supersededSubmissionId);
    const replacementId = normalizeUuid(intent.replacementSubmissionId);
    const audioId = `${normalizeUuid(intent.supersededSubmissionId)}:${normalizeUuid(intent.attachmentId)}`;
    const [supersededEntry, replacementEntry, audioEntry] = await Promise.all([
        requestResult(db.transaction(STORE_RECORDS).objectStore(STORE_RECORDS).get(supersededId)),
        requestResult(db.transaction(STORE_RECORDS).objectStore(STORE_RECORDS).get(replacementId)),
        requestResult(db.transaction(STORE_AUDIO).objectStore(STORE_AUDIO).get(audioId))
    ]);
    if (!supersededEntry || !replacementEntry || !audioEntry ||
        audioEntry.submissionId !== supersededId || audioEntry.attachmentId !== normalizeUuid(intent.attachmentId)) return false;
    const [superseded, replacement] = await Promise.all([decodeRecord(supersededEntry), decodeRecord(replacementEntry)]);
    if (!isSupersededState(superseded.state ?? superseded.State) ||
        Number(superseded.version ?? superseded.Version) !== intent.expectedSupersededVersion ||
        Number(replacement.version ?? replacement.Version) !== intent.expectedReplacementVersion ||
        (replacement.submission?.originalAudioAttachmentIds ?? replacement.Submission?.OriginalAudioAttachmentIds ?? []).some(id => normalizeUuid(id) === normalizeUuid(intent.attachmentId)) ||
        (replacement.audioAssets ?? replacement.AudioAssets ?? []).some(asset => normalizeUuid(asset.attachmentId ?? asset.AttachmentId) === normalizeUuid(intent.attachmentId))) {
        return false;
    }

    const supersededSubmission = superseded.submission ?? superseded.Submission;
    const supersededAssets = superseded.audioAssets ?? superseded.AudioAssets ?? [];
    const attachmentId = normalizeUuid(intent.attachmentId);
    if (!(supersededSubmission?.originalAudioAttachmentIds ?? supersededSubmission?.OriginalAudioAttachmentIds ?? [])
        .some(id => normalizeUuid(id) === attachmentId) ||
        !supersededAssets.some(asset => normalizeUuid(asset.attachmentId ?? asset.AttachmentId) === attachmentId)) {
        return false;
    }

    const recordSnapshot = await requestResult(db.transaction(STORE_RECORDS).objectStore(STORE_RECORDS).getAll());
    const referencedElsewhere = [];
    for (const entry of recordSnapshot) {
        if (entry.id === supersededId) continue;
        const record = await decodeRecord(entry);
        const submission = record.submission ?? record.Submission;
        const assets = record.audioAssets ?? record.AudioAssets ?? [];
        if ((submission?.originalAudioAttachmentIds ?? submission?.OriginalAudioAttachmentIds ?? [])
            .some(id => normalizeUuid(id) === attachmentId) ||
            assets.some(asset => normalizeUuid(asset.attachmentId ?? asset.AttachmentId) === attachmentId)) {
            referencedElsewhere.push(entry.id);
        }
    }

    const cleanupOutcome = referencedElsewhere.length > 0 ? "retained" : "removed";
    const complete = { ...intent, status: "complete", cleanupOutcome, version: intent.version + 1 };
    const completeBytes = encoder.encode(JSON.stringify(complete));
    const completeEnvelope = await encrypt("cleanup", intentKey, complete.version, completeBytes);
    completeBytes.fill(0);

    return new Promise((resolve, reject) => {
        const tx = db.transaction([STORE_RECORDS, STORE_AUDIO, STORE_JOURNAL], "readwrite");
        const records = tx.objectStore(STORE_RECORDS);
        const audio = tx.objectStore(STORE_AUDIO);
        const journal = tx.objectStore(STORE_JOURNAL);
        let remaining = 5;
        let failed = false;
        let currentRecordSnapshot = null;
        const read = (request, verify) => {
            request.onsuccess = () => {
                if (!verify(request.result)) {
                    failed = true;
                    tx.abort();
                    return;
                }
                remaining--;
                if (remaining !== 0 || failed) return;
                if (!Array.isArray(recordSnapshot) || !Array.isArray(currentRecordSnapshot) ||
                    recordSnapshot.length !== currentRecordSnapshot.length ||
                    currentRecordSnapshot.some((current, index) => !rawEntryMatches(current, recordSnapshot[index]))) {
                    failed = true;
                    tx.abort();
                    return;
                }
                if (cleanupOutcome === "removed") audio.delete(audioId);
                journal.put({ id: intentKey, version: complete.version, envelope: completeEnvelope });
            };
            request.onerror = () => { failed = true; tx.abort(); };
        };
        read(records.get(supersededId), entry => rawEntryMatches(entry, supersededEntry));
        read(records.get(replacementId), entry => rawEntryMatches(entry, replacementEntry));
        read(audio.get(audioId), entry => rawEntryMatches(entry, audioEntry));
        read(journal.get(intentKey), entry => rawEntryMatches(entry, journalEntry));
        read(records.getAll(), currentRecords => {
            if (!Array.isArray(currentRecords) || currentRecords.length !== recordSnapshot.length) return false;
            currentRecordSnapshot = currentRecords;
            return true;
        });
        tx.oncomplete = () => resolve(cleanupOutcome);
        tx.onabort = () => resolve(false);
        tx.onerror = () => { /* transaction abort is reported through onabort */ };
    });
}

export async function completeAudioCleanupResult(intentKey) {
    try {
        const outcome = await completeAudioCleanup(intentKey);
        return { code: outcome === "removed" ? "removed" : outcome === "retained" ? "retained" : "not-safe" };
    }
    catch { return { code: "unavailable" }; }
}

export async function getCleanupStatusResult(intentKey) {
    try {
        await getContentKey();
        const db = await getDatabase();
        const entry = await requestResult(db.transaction(STORE_JOURNAL).objectStore(STORE_JOURNAL).get(intentKey));
        if (!entry) return { code: "not-found" };
        const bytes = await decrypt("cleanup", intentKey, entry.envelope);
        let intent;
        try { intent = JSON.parse(decoder.decode(bytes)); }
        finally { bytes.fill(0); }
        if (intent.version !== entry.version) return { code: "corrupt" };
        return { code: intent.status === "pending" ? "pending" : intent.status === "complete" ? "completed" : "cancelled" };
    } catch {
        return { code: "unavailable" };
    }
}

async function cancelCleanup(intentKey) {
    if (typeof intentKey !== "string" || intentKey.length > 240) throw storageError("invalid-request");
    await getContentKey();
    const db = await getDatabase();
    const entry = await requestResult(db.transaction(STORE_JOURNAL).objectStore(STORE_JOURNAL).get(intentKey));
    if (!entry) return false;
    const bytes = await decrypt("cleanup", intentKey, entry.envelope);
    let intent;
    try { intent = JSON.parse(decoder.decode(bytes)); }
    finally { bytes.fill(0); }
    if (intent.status !== "pending") return false;
    const cancelled = { ...intent, status: "cancelled", version: intent.version + 1 };
    const clear = encoder.encode(JSON.stringify(cancelled));
    const envelope = await encrypt("cleanup", intentKey, cancelled.version, clear);
    clear.fill(0);
    return transactionCompareAndPut(db, STORE_JOURNAL, intentKey, entry, {
        id: intentKey, version: cancelled.version, envelope
    });
}

export async function cancelCleanupResult(intentKey) {
    try {
        if (await cancelCleanup(intentKey)) return { code: "cancelled" };
        const status = await getCleanupStatusResult(intentKey);
        return { code: status.code === "cancelled" ? "already-cancelled" : status.code === "completed" ? "completed" : status.code };
    }
    catch { return { code: "unavailable" }; }
}

function validateIntent(intent) {
    if (!intent || typeof intent.intentKey !== "string" || intent.intentKey.length < 1 || intent.intentKey.length > 240 ||
        !Number.isSafeInteger(intent.expectedSupersededVersion) || intent.expectedSupersededVersion < 1 ||
        !Number.isSafeInteger(intent.expectedReplacementVersion) || intent.expectedReplacementVersion < 1) {
        throw storageError("invalid-request");
    }
    normalizeUuid(intent.supersededSubmissionId);
    normalizeUuid(intent.replacementSubmissionId);
    normalizeUuid(intent.attachmentId);
    return intent.intentKey;
}

function normalizeUuid(value) {
    if (typeof value !== "string" || !/^[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i.test(value)) {
        throw storageError("invalid-request");
    }
    return value.toLowerCase();
}

function rawEntryMatches(current, expected) {
    if (!current || current.id !== expected.id || current.version !== expected.version) return false;
    const currentEnvelope = current.envelope ?? current;
    const expectedEnvelope = expected.envelope ?? expected;
    return currentEnvelope.envelopeVersion === expectedEnvelope.envelopeVersion &&
        sameBytes(currentEnvelope.iv, expectedEnvelope.iv) &&
        sameBytes(currentEnvelope.ciphertext, expectedEnvelope.ciphertext);
}

function sameBytes(left, right) {
    return isByteArray(left) && isByteArray(right) && left.length === right.length &&
        left.every((value, index) => value === right[index]);
}

function isByteArray(value) {
    return value instanceof Uint8Array ||
        (Array.isArray(value) && value.every(byte => Number.isInteger(byte) && byte >= 0 && byte <= 255));
}

function isSupersededState(state) {
    return state === "Superseded" || state === 4;
}

async function transactionPutIfAbsent(db, storeName, id, value) {
    return new Promise((resolve, reject) => {
        const tx = db.transaction(storeName, "readwrite");
        const store = tx.objectStore(storeName);
        const request = store.get(id);
        let failure;
        request.onsuccess = () => {
            if (request.result) {
                failure = storageError("conflict");
                tx.abort();
                return;
            }
            store.put(value);
        };
        request.onerror = () => { failure = storageError("unavailable"); tx.abort(); };
        tx.oncomplete = () => resolve();
        tx.onabort = () => reject(failure ?? storageError("unavailable"));
        tx.onerror = () => { failure ??= storageError("unavailable"); };
    });
}

async function putAudioAndRecord(db, recordIdValue, expectedVersion, nextRecord, audioEntry, attachmentId) {
    return new Promise((resolve, reject) => {
        const tx = db.transaction([STORE_RECORDS, STORE_AUDIO], "readwrite");
        const records = tx.objectStore(STORE_RECORDS);
        const audio = tx.objectStore(STORE_AUDIO);
        const currentById = records.get(recordIdValue);
        const currentAudio = audio.get(audioEntry.id);
        const matchingAttachments = audio.index("attachmentId").getAll(attachmentId);
        let remaining = 3;
        let currentRecord;
        let existingAudio;
        let attachmentRows;
        let failure;
        const finishRead = () => {
            remaining--;
            if (remaining !== 0 || failure) return;
            if (!currentRecord || currentRecord.version !== expectedVersion) {
                failure = storageError("conflict", recordIdValue);
                tx.abort();
                return;
            }
            if (existingAudio || attachmentRows.some(item => item.submissionId !== recordIdValue)) {
                failure = storageError("conflict", recordIdValue);
                tx.abort();
                return;
            }
            records.put(nextRecord);
            audio.put(audioEntry);
        };
        currentById.onsuccess = () => { currentRecord = currentById.result; finishRead(); };
        currentAudio.onsuccess = () => { existingAudio = currentAudio.result; finishRead(); };
        matchingAttachments.onsuccess = () => { attachmentRows = matchingAttachments.result; finishRead(); };
        for (const request of [currentById, currentAudio, matchingAttachments]) {
            request.onerror = () => { failure = storageError("unavailable"); tx.abort(); };
        }
        tx.oncomplete = () => resolve();
        tx.onabort = () => reject(failure ?? storageError("unavailable"));
        tx.onerror = () => { failure ??= storageError("unavailable"); };
    });
}

async function transactionCompareAndPut(db, storeName, id, expected, replacement) {
    return new Promise((resolve, reject) => {
        const tx = db.transaction(storeName, "readwrite");
        const store = tx.objectStore(storeName);
        const request = store.get(id);
        let matched = false;
        request.onsuccess = () => {
            matched = rawEntryMatches(request.result, expected);
            if (matched) store.put(replacement);
            else tx.abort();
        };
        request.onerror = () => tx.abort();
        tx.oncomplete = () => resolve(matched);
        tx.onabort = () => resolve(false);
        tx.onerror = () => { /* transaction abort is reported through onabort */ };
    });
}

function requestResult(request) {
    return new Promise((resolve, reject) => {
        request.onsuccess = () => resolve(request.result);
        request.onerror = () => reject(storageError("unavailable"));
    });
}

function toHex(bytes) {
    return Array.from(bytes, value => value.toString(16).padStart(2, "0")).join("");
}
