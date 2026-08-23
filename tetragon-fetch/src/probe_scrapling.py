# Tetragon fetch — Scrapling StealthyFetcher + page_action (가이드 실전 패턴)
#
# 아티팩트 "Akamai 우회 크롤링 가이드"의 방법1:
#   메인 페이지 진입 → 20초 대기(Akamai JS 챌린지 자동 해결) → 목표 페이지 이동 → 추출
# StealthyFetcher는 내부적으로 patchright(CDP 탐지 패치 Playwright 포크)를 쓴다.
#
# 사용법:
#   python src/probe_scrapling.py                       # 기본: 검색 페이지
#   python src/probe_scrapling.py "<목표 URL>"
#   python src/probe_scrapling.py "<url>" --headed
#   python src/probe_scrapling.py "<url>" --wait 20

import sys
import time
import random

args = sys.argv[1:]
headed = "--headed" in args

def get_after(flag, default=None):
    return args[args.index(flag) + 1] if flag in args and args.index(flag) + 1 < len(args) else default

wait_s = int(get_after("--wait", "20"))
# --bundled 를 주면 patchright 번들 브라우저, 기본은 시스템 Google Chrome(real_chrome).
use_real_chrome = "--bundled" not in args
target = next((a for a in args if a.startswith("http")), "https://www.coupang.com/np/search?q=무선마우스")

from playwright.sync_api import Page
from scrapling.fetchers import StealthyFetcher


def bypass_akamai(page: Page):
    # 1) 메인 페이지에서 JS 챌린지가 풀릴 때까지 대기
    print(f"  [page_action] 메인에서 {wait_s}초 대기 (챌린지 해결)...", flush=True)
    page.wait_for_timeout(wait_s * 1000)

    # 2) 사람처럼 마우스 이동 + 스크롤
    for _ in range(random.randint(2, 4)):
        page.mouse.move(random.randint(80, 1200), random.randint(80, 600))
        page.mouse.wheel(0, random.randint(200, 600))
        page.wait_for_timeout(random.randint(600, 1600))

    # 3) 목표 URL로 이동 (같은 세션/쿠키 유지)
    print(f"  [page_action] 목표 이동: {target[:70]}", flush=True)
    page.goto(target, wait_until="domcontentloaded")
    try:
        page.wait_for_load_state("networkidle", timeout=12000)
    except Exception:
        pass
    page.wait_for_timeout(random.randint(1500, 3000))
    return page


def main():
    print("== Scrapling StealthyFetcher probe ==")
    print(f"target: {target} | headed: {headed} | wait: {wait_s}s | browser: {'real Chrome' if use_real_chrome else 'bundled'}")
    print("[fetch] 메인 페이지 진입 (JS 챌린지 트리거)...", flush=True)

    fetch_kwargs = dict(
        headless=not headed,
        block_webrtc=True,
        disable_resources=False,
        humanize=True,
        network_idle=True,
        page_action=bypass_akamai,
    )
    if use_real_chrome:
        fetch_kwargs["real_chrome"] = True  # 시스템 설치 Google Chrome 사용(번들 다운로드 우회)

    t0 = time.time()
    result = StealthyFetcher.fetch("https://www.coupang.com", **fetch_kwargs)

    html = result.html_content if hasattr(result, "html_content") else str(result)
    status = getattr(result, "status", "?")
    denied = any(n in html for n in ("Access Denied", "errors.edgesuite.net", "Reference #"))

    # 상품 링크(/vp/products/)로 개수 확인 — 클래스명 바뀌어도 안정적
    import re as _re
    prod_links = _re.findall(r"/vp/products/(\d+)", html)
    pcount = len(set(prod_links))

    # 상품명 샘플 — 여러 셀렉터 시도
    sample = None
    for sel in ("li.search-product .name::text", ".search-product__name::text",
                "[class*=ProductUnit] [class*=name]::text", "a[href*='/vp/products/'] img::attr(alt)"):
        try:
            v = result.css_first(sel)
            if v:
                sample = v
                break
        except Exception:
            pass

    print("--- RESULT ---")
    print(f"elapsed : {time.time()-t0:.1f}s")
    print(f"status  : {status}")
    print(f"html len: {len(html):,}")
    print(f"denied  : {denied}")
    print(f"products: {pcount}")
    print(f"sample  : {sample!r}")

    ok = (not denied) and (pcount > 0 or len(html) > 200000)
    print()
    print("VERDICT :", "✅✅✅ 보호 엔드포인트 통과!" if ok else "❌ 미통과")


if __name__ == "__main__":
    main()
