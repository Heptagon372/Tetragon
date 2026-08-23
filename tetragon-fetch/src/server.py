# Tetragon fetch 사이드카 (Python · Scrapling) — 검증된 우회 엔진
#
# 실측(docs/ANTIBOT-FETCH-DESIGN.md §6.5)에서 유일하게 쿠팡 보호 엔드포인트를
# 통과한 레시피를 REST로 감싼다:
#   StealthyFetcher(headed · real_chrome) → 홈 N초 예열(_abck 검증) → 목표 이동 → 추출
#
# .NET IBrowserFetcher(HttpBrowserFetcher)가 호출하는 것과 동일한 계약:
#   POST /fetch  { url, waitForSelector?, behavior?, cookieHeader?, proxy?, warmupHost? }
#            →   { statusCode, html, finalUrl, setCookie, challengeSolved, blocked }
#   GET  /healthz → { ok, busy }
#
# 실행:
#   python src/server.py                      # 기본 headed(창 뜸) real_chrome, :8080
#   PORT=8080 API_KEY=secret python src/server.py
#   HEADLESS=1 python src/server.py           # headless (통과율↓ — 서버 무인용 아니면 비권장)
#
# ⚠️ headed가 통과의 핵심이라 기본이 headed다. 무인 서버는 Xvfb 등 가상 디스플레이 위에서 돌려라.

import os
import json
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import urlparse

from playwright.sync_api import Page
from scrapling.fetchers import StealthyFetcher

PORT = int(os.environ.get("PORT", "8080"))
API_KEY = os.environ.get("API_KEY") or None
HEADLESS = os.environ.get("HEADLESS", "") not in ("", "0", "false", "False")
REAL_CHROME = os.environ.get("REAL_CHROME", "1") not in ("0", "false", "False")
DEFAULT_PROXY = os.environ.get("PROXY") or None

# headed 브라우저 창은 동시에 여러 개 띄우면 불안정 → 한 번에 하나만 처리.
_fetch_lock = threading.Lock()

# BehaviorProfile(.NET enum) → 홈 예열 대기(ms). 25초 예열이 _abck 검증의 핵심.
_WARMUP_MS = {"None": 0, "Light": 12000, "Human": 25000}

_BLOCK_MARKERS = ("Access Denied", "errors.edgesuite.net", "Reference #",
                  "Pardon Our Interruption", "비정상적인 접근")


def _blocked(html: str, status: int) -> bool:
    if status in (403, 428):
        return True
    return any(m in html for m in _BLOCK_MARKERS) and len(html) < 5000


def _cookie_header(result) -> str:
    try:
        cookies = getattr(result, "cookies", None)
        if isinstance(cookies, dict):
            return "; ".join(f"{k}={v}" for k, v in cookies.items())
        if isinstance(cookies, (list, tuple)):
            return "; ".join(f"{c.get('name')}={c.get('value')}" for c in cookies if c.get("name"))
    except Exception:
        pass
    return ""


def do_fetch(req: dict) -> dict:
    url = req.get("url")
    if not url:
        raise ValueError("url required")

    behavior = req.get("behavior", "Human")
    warmup_ms = _WARMUP_MS.get(behavior, 25000)
    wait_selector = req.get("waitForSelector")
    proxy = req.get("proxy") or DEFAULT_PROXY
    cookie_header = req.get("cookieHeader")

    origin = f"{urlparse(url).scheme}://{urlparse(url).netloc}/"
    warmup_url = req.get("warmupHost") or origin

    # page_action: 홈 예열 → (선택)쿠키 주입 → 목표 이동 → 대기
    def action(page: Page):
        if cookie_header:
            try:
                host = urlparse(url).hostname or ""
                base = "." + host[4:] if host.startswith("www.") else "." + host
                cks = []
                for kv in cookie_header.split(";"):
                    if "=" in kv:
                        name, _, val = kv.strip().partition("=")
                        cks.append({"name": name, "value": val, "domain": base, "path": "/"})
                if cks:
                    page.context.add_cookies(cks)
            except Exception:
                pass
        if warmup_ms:
            page.wait_for_timeout(warmup_ms)  # 홈에서 챌린지 자동 해결
        # 목표로 이동 (같은 세션/쿠키 유지)
        if url.rstrip("/") != warmup_url.rstrip("/"):
            page.goto(url, wait_until="domcontentloaded")
        try:
            page.wait_for_load_state("networkidle", timeout=12000)
        except Exception:
            pass
        if wait_selector:
            try:
                page.wait_for_selector(wait_selector, timeout=12000)
            except Exception:
                pass
        return page

    fetch_kwargs = dict(
        headless=HEADLESS,
        humanize=True,
        network_idle=True,
        block_webrtc=True,
        page_action=action,
    )
    if REAL_CHROME:
        fetch_kwargs["real_chrome"] = True
    if proxy:
        fetch_kwargs["proxy"] = proxy

    with _fetch_lock:
        result = StealthyFetcher.fetch(warmup_url, **fetch_kwargs)

    html = getattr(result, "html_content", None) or getattr(result, "body", None) or str(result)
    status = getattr(result, "status", 0) or 0
    final_url = getattr(result, "url", url) or url

    return {
        "statusCode": int(status),
        "html": html,
        "finalUrl": str(final_url),
        "setCookie": _cookie_header(result),
        "challengeSolved": not _blocked(html, int(status)),
        "blocked": _blocked(html, int(status)),
    }


class Handler(BaseHTTPRequestHandler):
    def _send(self, code, obj):
        body = json.dumps(obj, ensure_ascii=False).encode("utf-8")
        self.send_response(code)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, fmt, *a):  # 요청 로그 간결화
        pass

    def do_GET(self):
        if self.path == "/healthz":
            return self._send(200, {"ok": True, "busy": _fetch_lock.locked()})
        self._send(404, {"error": "not found"})

    def do_POST(self):
        if self.path != "/fetch":
            return self._send(404, {"error": "not found"})
        if API_KEY and self.headers.get("X-Api-Key") != API_KEY:
            return self._send(401, {"error": "unauthorized"})
        try:
            length = int(self.headers.get("Content-Length", "0"))
            req = json.loads(self.rfile.read(length) or b"{}")
        except Exception as e:
            return self._send(400, {"error": f"bad request: {e}"})
        try:
            import time
            t0 = time.time()
            result = do_fetch(req)
            print(f"[fetch] {req.get('url')} -> {result['statusCode']} "
                  f"blocked={result['blocked']} {time.time()-t0:.1f}s", flush=True)
            self._send(200, result)
        except Exception as e:
            print(f"[fetch] error: {e}", flush=True)
            self._send(500, {"error": str(e)})


def main():
    mode = "headless" if HEADLESS else "headed(창)"
    print(f"tetragon-fetch (python/scrapling) :{PORT} "
          f"[{mode}, real_chrome={REAL_CHROME}, proxy={'set' if DEFAULT_PROXY else 'none'}, "
          f"apiKey={'set' if API_KEY else 'none'}]", flush=True)
    ThreadingHTTPServer(("0.0.0.0", PORT), Handler).serve_forever()


if __name__ == "__main__":
    main()
