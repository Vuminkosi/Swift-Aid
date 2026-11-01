"""Send the existing case CSV format in bounded, resumable Gateway batches.

Credentials are read only from SWIFTAID_IMPORT_ACCESS_TOKEN. This command never
creates users, grants permissions, invents confirmations, or writes directly to SQL.
"""

import argparse
import base64
import csv
import io
import json
import os
import sys
import urllib.error
import urllib.parse
import urllib.request
import uuid
from pathlib import Path


class BoundedLines:
    def __init__(self, stream):
        self.stream = stream
        self.characters = 0

    def __iter__(self):
        return self

    def __next__(self):
        line = self.stream.readline(65_537)
        if not line:
            raise StopIteration
        self.characters += len(line)
        if self.characters > 65_536 or "\0" in line:
            raise ValueError("CSV record exceeds its bound or contains an invalid character")
        return line


def records(stream):
    csv.field_size_limit(16_384)
    lines = BoundedLines(stream)
    reader = csv.reader(lines, strict=True)
    while True:
        lines.characters = 0
        row = next(reader, None)
        if row is None:
            return
        if len(row) > 32:
            raise ValueError("CSV record has too many columns")
        yield row


def request(url, token, body=None, metadata=None):
    headers = {"Authorization": "Bearer " + token, "Accept": "application/json"}
    if body is not None:
        headers["Content-Type"] = "text/csv; charset=utf-8"
        headers["X-SwiftAid-Import"] = metadata
    message = urllib.request.Request(url, data=body, headers=headers)
    try:
        opener = urllib.request.build_opener(NoRedirect())
        with opener.open(message, timeout=300) as response:
            return response.status, read_response(response)
    except urllib.error.HTTPError as error:
        # Only typed safe Gateway results are printed; never echo input records or credentials.
        with error:
            try:
                return error.code, read_response(error)
            except (ValueError, UnicodeError):
                return error.code, {"errorCode": "gateway-unavailable"}


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


def read_response(response):
    payload = response.read(1024 * 1024 + 1)
    if len(payload) > 1024 * 1024:
        raise ValueError("Gateway response is too large")
    return json.loads(payload)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("csv", type=Path)
    parser.add_argument("--gateway", required=True)
    parser.add_argument("--context", type=Path, required=True, help="Reviewed workspace/confirmations JSON; see gateway-import-format.md")
    parser.add_argument("--import-id", type=uuid.UUID, required=True, help="Keep this ID when resuming the same immutable import")
    parser.add_argument("--batch-size", type=int, default=100)
    args = parser.parse_args()
    if not 1 <= args.batch_size <= 1000:
        parser.error("batch-size must be 1..1000")
    origin = urllib.parse.urlparse(args.gateway)
    if origin.username or origin.password or origin.query or origin.fragment:
        parser.error("gateway must be an origin without credentials, query or fragment")
    if origin.scheme != "https" and not (origin.scheme == "http" and origin.hostname in {"localhost", "127.0.0.1", "::1"}):
        parser.error("use HTTPS, or loopback HTTP for local development")
    token = os.environ.get("SWIFTAID_IMPORT_ACCESS_TOKEN")
    if not token:
        parser.error("SWIFTAID_IMPORT_ACCESS_TOKEN must contain an authorized staff access token")
    with args.context.open("rb") as context_file:
        context_bytes = context_file.read(6001)
    if len(context_bytes) > 6000:
        parser.error("context is too large")
    context = json.loads(context_bytes)
    metadata = base64.b64encode(json.dumps(context, separators=(",", ":")).encode()).decode()
    endpoint = args.gateway.rstrip("/") + "/api/v1/case-imports/" + str(args.import_id)
    code, summary = request(endpoint, token)
    if code == 200:
        if summary["state"] == "Completed":
            print(json.dumps(summary))
            return 0
        start = summary["nextRow"]
    elif code == 404:
        start = 1
    else:
        print(json.dumps({"status": code, "errorCode": summary.get("code", "status-unavailable")}))
        return 1
    with args.csv.open("r", encoding="utf-8-sig", newline="") as stream:
        rows = records(stream)
        header = next(rows)
        for _ in range(start - 1):
            if next(rows, None) is None:
                raise ValueError("CSV ends before the durable checkpoint; verify the original source file")
        pending = next(rows, None)
        if pending is None:
            # The last case may have committed before a lost final-batch response/checkpoint.
            output = io.StringIO(newline="")
            csv.writer(output, lineterminator="\n").writerow(header)
            code, result = request(endpoint + "/rows?" + urllib.parse.urlencode({"startRow": start, "finalBatch": "true"}),
                                   token, output.getvalue().encode("utf-8"), metadata)
            print(json.dumps({"importId": str(args.import_id), "status": code, "summary": result.get("summary"), "errorCode": result.get("errorCode")}))
            return 0 if code == 200 and not result.get("errorCode") else 1
        while pending is not None:
            batch = []
            while pending is not None and len(batch) < args.batch_size:
                batch.append(pending)
                pending = next(rows, None)
            output = io.StringIO(newline="")
            writer = csv.writer(output, lineterminator="\n")
            writer.writerow(header)
            writer.writerows(batch)
            payload = output.getvalue().encode("utf-8")
            if len(payload) > 8 * 1024 * 1024:
                raise ValueError("Batch exceeds 8 MiB; rerun with a smaller batch-size")
            url = endpoint + "/rows?" + urllib.parse.urlencode({"startRow": start, "finalBatch": str(pending is None).lower()})
            code, result = request(url, token, payload, metadata)
            print(json.dumps({"importId": str(args.import_id), "status": code, "summary": result.get("summary"), "errorCode": result.get("errorCode", result.get("code"))}), flush=True)
            if code != 200 or result.get("errorCode"):
                return 1
            start = result["summary"]["nextRow"]
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except (OSError, ValueError, csv.Error, StopIteration):
        print("Import stopped. Accepted rows remain durable; verify the file/context and resume with the same import ID.", file=sys.stderr)
        sys.exit(1)
