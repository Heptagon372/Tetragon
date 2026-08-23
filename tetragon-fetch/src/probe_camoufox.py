# Tetragon fetch — Camoufox(Firefox 안티디텍트)로 Akamai 재측정
#
# Bright Data 글에서 Akamai(Zalando)를 실제로 뚫은 도구가 Camoufox다.
# 우리가 쓴 Chromium 계열(stealth/rebrowser)은 전부 실패 → Firefox 기반으로 재시도.
#
# 사용법:
#   python src/probe_camoufox.py "https://www.coupang.com/np/search?q=무선마우스"
#   python src/probe_camoufox.py "<url>" --headed
#   python src/probe_camoufox.py "<url>" --proxy http://user:pass@host:port
#
# 무료. 네 집 IP(주거용)에서 돌리는 게 포인트 — 쉬게 둔 뒤 실행.

import sys
import time
import random
import re
from camoufox.sync_api import Camoufox

args = sys.argv[1:]
headed = "--headed" in args

def get_after(flag):
    return args[args.index(flag) + 1] if flag in args and args.index(flag) + 1 < len(args) else None

proxy_arg = get_after("--proxy")
url = next((a for a in args if a.startswith("http") and a != proxy_arg), "https://www.coupang.com/np/search?q=무선마우스")

def rand(a, b): return random.uniform(a, b)

def humanize(page, rounds=3):
    time.sleep(rand(1.2, 2.8))
    for _ in range(rounds):
        try:
            page.mouse.move(rand(60, 1200), rand(80, 600), steps=int(rand(6, 16)))
            page.mouse.wheel(0, rand(150, 550))
        except Exception:
            pass
        time.sleep(rand(0.5, 1.4))

def abck_state(cookies):
    c = next((x for x in cookies if x.get("name") == "_abck"), None)
    if not c:
        return "none"
    m = re.findall(r"~(-?\d+)~", c["value"])
    if not m:
        return "unparsed"
    last = f"~{m[-1]}~"
    return f"{last} ({'봇의심' if last=='~-1~' else '검증됨' if last=='~0~' else '기타'})"

def denied(html):
    return any(n in html for n in ("Access Denied", "errors.edgesuite.net", "Reference #"))

def parse_proxy(s):
    if not s:
        return None
    from urllib.parse import urlparse, unquote
    u = urlparse(s)
    p = {"server": f"{u.scheme}://{u.hostname}:{u.port}"}
    if u.username:
        p["username"] = unquote(u.username)
    if u.password:
        p["password"] = unquote(u.password)
    return p

def main():
    proxy = parse_proxy(proxy_arg)
    print("== Camoufox probe ==")
    print(f"target: {url} | headed: {headed} | proxy: {proxy['server'] if proxy else 'none'}")
    print()

    opts = dict(headless=not headed, humanize=True, locale="ko-KR", os="windows")
    if proxy:
        opts["proxy"] = proxy
        opts["geoip"] = True  # 프록시 IP 위치에 맞춰 지문 일관화

    with Camoufox(**opts) as browser:
        page = browser.new_page()

        # 1) 홈 예열
        print("[warmup] 홈 예열...")
        try:
            page.goto("https://www.coupang.com/", wait_until="domcontentloaded", timeout=45000)
        except Exception as e:
            print("  home goto:", e)
        wt = time.time()
        validated = False
        while time.time() - wt < 25:
            humanize(page, 1)
            st = abck_state(page.context.cookies())
            print(f"  예열 {int(time.time()-wt)}s abck: {st}")
            if st.startswith("~0~"):
                validated = True
                break
        print("[warmup] _abck 검증됨?:", "YES ✅" if validated else "NO")

        # 2) 검색 진입
        t0 = time.time()
        try:
            resp = page.goto(url, wait_until="domcontentloaded", timeout=45000)
            status = resp.status if resp else 0
        except Exception as e:
            print("goto:", e)
            status = 0
        humanize(page, 3)
        try:
            page.wait_for_load_state("networkidle", timeout=12000)
        except Exception:
            pass

        html = page.content()
        title = page.title()
        cookies = page.context.cookies()
        is_denied = denied(html)
        try:
            products = page.evaluate(
                "document.querySelectorAll('ul#productList li, li.search-product, .baby-product, .search-product-wrap').length"
            )
        except Exception:
            products = 0

        print("--- RESULT ---")
        print(f"elapsed : {time.time()-t0:.1f}s")
        print(f"status  : {status}")
        print(f"title   : {title!r}")
        print(f"html len: {len(html):,}")
        print(f"_abck   : {abck_state(cookies)}")
        print(f"denied  : {is_denied}")
        print(f"products: {products}")

        ok = status == 200 and not is_denied and (products > 0 or len(html) > 200000)
        print()
        print("VERDICT :", "✅✅✅ 보호 엔드포인트 통과!" if ok else "❌ 미통과")

if __name__ == "__main__":
    main()
