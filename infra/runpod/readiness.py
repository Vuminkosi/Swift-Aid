"""Loopback-only aggregate readiness endpoint, forwarded by nginx."""
from http.server import BaseHTTPRequestHandler, HTTPServer
from pathlib import Path
from health import healthy


class Handler(BaseHTTPRequestHandler):
    def do_GET(self):
        if self.path != '/pod-health/ready':
            self.send_error(404)
            return
        ready = healthy(Path('/run/swiftaid/health.json'))
        body = b'{"status":"ready"}' if ready else b'{"status":"not-ready"}'
        self.send_response(200 if ready else 503)
        self.send_header('Content-Type', 'application/json')
        self.send_header('Cache-Control', 'no-store')
        self.send_header('Content-Length', str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, *_):
        pass


if __name__ == '__main__':
    HTTPServer(('127.0.0.1', 8092), Handler).serve_forever()
