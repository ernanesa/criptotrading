#!/usr/bin/env python3
"""Preserve bounded, redacted command diagnostics without copying raw logs."""

from collections import deque
import os
import re
import sys


MAX_LINE_BYTES = 16_384
MAX_OUTPUT_BYTES = 16_384
MAX_LINES = 200

SECRET_ASSIGNMENT = re.compile(
    r'(?i)(\b[a-z0-9_.-]*(?:password|passwd|secret|token|api[_-]?key|authorization|'
    r'connection(?:string)?|private[_-]?key)\b["\']?\s*[=:]\s*["\']?)'
    r'([^"\'\s,;]+)'
)
BEARER = re.compile(r'(?i)\b(Bearer)\s+[A-Za-z0-9._~+/-]+=*')
URI_CREDENTIALS = re.compile(r'(?i)([a-z][a-z0-9+.-]*://[^:/\s]+:)[^@/\s]+(@)')
HEADER_SECRET = re.compile(r'(?i)(X-MBX-APIKEY\s*[:=]\s*)[^\s,;]+')
lines: deque[str] = deque(maxlen=MAX_LINES)


def sanitize(line: str) -> str:
    line = SECRET_ASSIGNMENT.sub(r'\1[REDACTED]', line)
    line = BEARER.sub(r'\1 [REDACTED]', line)
    line = URI_CREDENTIALS.sub(r'\1[REDACTED]\2', line)
    return HEADER_SECRET.sub(r'\1[REDACTED]', line)


def main() -> int:
    if len(sys.argv) != 3:
        print("usage: redact-diagnostic-log.py INPUT OUTPUT", file=sys.stderr)
        return 2
    source, destination = sys.argv[1:]
    with open(source, "rb") as stream:
        while True:
            raw_line = stream.readline(MAX_LINE_BYTES + 1)
            if not raw_line:
                break
            if len(raw_line) > MAX_LINE_BYTES:
                while raw_line and not raw_line.endswith(b"\n"):
                    raw_line = stream.readline(MAX_LINE_BYTES + 1)
                lines.append("[REDACTED: oversized diagnostic line omitted]\n")
                continue
            lines.append(sanitize(raw_line.decode("utf-8", errors="replace")))

    bounded = list(lines)
    encoded = [line.encode("utf-8", errors="replace") for line in bounded]
    while encoded and sum(map(len, encoded)) > MAX_OUTPUT_BYTES:
        encoded.pop(0)
    if encoded and len(encoded[-1]) > MAX_OUTPUT_BYTES:
        encoded[-1] = b"[REDACTED: oversized sanitized line omitted]\n"
    os.umask(0o077)
    with open(destination, "wb") as stream:
        stream.write(b"".join(encoded))
    os.chmod(destination, 0o600)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
