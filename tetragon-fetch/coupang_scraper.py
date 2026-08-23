#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
쿠팡 상품 스크래퍼 (단일 파일, 자립 실행) — 자제(rate-discipline) 버전
=====================================================================

Akamai 보호를 뚫는 검증된 레시피 + 단일 IP를 오래 버티게 하는 자제 로직.

■ 우회 원리 (실측 검증)
  ① Scrapling StealthyFetcher = patchright (CDP Runtime.enable 누수 패치)
  ② headless=False → 실제 창(headed). 통과의 결정적 변수.
  ③ real_chrome=True → 시스템 Google Chrome
  ④ 홈 예열 → 목표 이동 : Akamai 센서가 _abck를 검증하게 함

■ 자제 로직 (IP를 안 태우기 위한 핵심)
  · 캐싱     : 한 번 받은 상품은 재수집 안 함 (요청 자체를 줄임 — 가장 효과 큼)
  · 세션재사용: 유효한 _abck 쿠키를 저장·재주입 → 매번 25초 예열 안 하고 6초로 단축
  · 간격     : 상품 사이 랜덤 대기(기본 3~8초) — 봇 패턴 회피
  이 셋으로 "가끔 소량 수집"은 집 IP 하나로 태우지 않고 계속 쓸 수 있다.
  (대량이면 여전히 주거용 프록시 로테이션이 필요 — --proxy)

■ 설치 (최초 1회)
    pip install "scrapling[all]"
    scrapling install
    patchright install --force chromium

■ 사용법
    python coupang_scraper.py "무선마우스"                 # 검색 → 상위 상품
    python coupang_scraper.py "무선마우스" --limit 5
    python coupang_scraper.py 9179029564                  # 상품 ID (캐시에 있으면 즉시)
    python coupang_scraper.py "무선마우스" --no-cache      # 캐시 무시하고 새로
    python coupang_scraper.py "무선마우스" --delay-min 4 --delay-max 10
    python coupang_scraper.py "무선마우스" --proxy http://ID:PW@host:port

  결과 → 콘솔 + coupang_result.json.  캐시 → .coupang_cache/,  세션 → .coupang_session.json
"""

import sys
import os
import re
import json
import time
import random
import argparse
from urllib.parse import quote

# Windows 콘솔(cp949)에서 한글·기호(✓✗) 출력이 깨지지 않게 stdout을 utf-8로 고정.
try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass

try:
    from scrapling.fetchers import StealthyFetcher
    from playwright.sync_api import Page
except ImportError:
    print("의존성 없음. 설치:  pip install \"scrapling[all]\" && scrapling install && patchright install --force chromium")
    sys.exit(1)

CACHE_DIR = ".coupang_cache"
SESSION_FILE = ".coupang_session.json"
SESSION_FRESH_SEC = 15 * 60           # 세션 쿠키를 신선하다고 볼 시간 (ak_bmsc 수명 고려)


# ─────────────────────────── 파싱 헬퍼 ───────────────────────────

def _html_unescape(s):
    import html as _h
    return _h.unescape(s) if s else s

def meta_og(html, prop):
    for pat in (rf'<meta[^>]+property=["\']og:{prop}["\'][^>]+content=["\']([^"\']*)',
                rf'<meta[^>]+content=["\']([^"\']*)["\'][^>]+property=["\']og:{prop}["\']'):
        m = re.search(pat, html, re.I)
        if m:
            return _html_unescape(m.group(1))
    return None

def clean_title(raw):
    if not raw:
        return None
    t = raw.strip()
    b = t.rfind(" | 쿠팡")
    if b > 0:
        t = t[:b]
    d = t.rfind(" - ")
    if d > 20:
        t = t[:d]
    return t.strip()

def extract_price(html):
    m = re.search(r'"(?:salePrice|couponPrice|finalPrice)"\s*:\s*(\d{3,})', html)
    if m:
        return int(m.group(1)), "판매가(JSON)"
    m = re.search(r'([0-9]{1,3}(?:,[0-9]{3})+)\s*원', html)
    if m:
        return int(m.group(1).replace(",", "")), "표시가격"
    return None, ""

def extract_images(html, cap=20):
    imgs, seen = [], set()
    og = meta_og(html, "image")
    if og:
        u = ("https:" + og) if og.startswith("//") else og
        if u not in seen:
            seen.add(u); imgs.append(u)
    for m in re.findall(r'(?:https?:)?//[^"\'\s]*coupangcdn\.com/[^"\'\s]*vendor_inventory[^"\'\s]*\.(?:jpg|jpeg|png|webp)', html, re.I):
        u = ("https:" + m) if m.startswith("//") else m
        if u not in seen:
            seen.add(u); imgs.append(u)
        if len(imgs) >= cap:
            break
    return imgs

def extract_options(html):
    """쿠팡 fashion-option DOM에서 옵션 그룹 추출 (실측 검증).
    · 드롭다운형(사이즈): fashion-option-select__content 의 <li> 텍스트
    · 스와치형(색상): fashion-option__button-list 의 이미지 <li> 개수 + 선택 라벨
    조합별 가격은 정적 HTML에 없어(선택 시 API 로드) 변형 가격은 추출하지 않는다.
    """
    groups = []
    for sec in re.split(r'<section class="twc-my-\[16px\]', html):
        if "fashion-option-select" not in sec and "fashion-option__button-list" not in sec:
            continue
        mname = re.search(r'twc-font-bold twc-mb-\[4px\][^>]*>(?:<span>)?([^<:]{1,20})', sec)
        if not mname:
            continue
        name = mname.group(1).strip()
        values = []
        content = re.search(r'fashion-option-select__content.*?</ul>', sec, re.S)
        if content:
            for li in re.findall(r'<li[^>]*>([^<]{1,40})</li>', content.group(0)):
                v = li.strip()
                if v and v not in values:
                    values.append(v)
        if not values:
            blist = re.search(r'fashion-option__button-list.*?</ul>', sec, re.S)
            if blist:
                cnt = len(re.findall(r'<li>', blist.group(0)))
                sel = re.search(r'fashion-option__label-item-text[^>]*>([^<]+)', sec)
                if cnt:
                    values = [f"{cnt}종" + (f" (선택:{sel.group(1).strip()})" if sel else "")]
        if values:
            groups.append({"name": name, "values": values})
    return groups

def is_blocked(html, status=200):
    if status in (403, 428):
        return True
    return any(n in html for n in ("Access Denied", "errors.edgesuite.net")) and len(html) < 5000

def parse_product(pid, url, html):
    if is_blocked(html):
        return {"id": pid, "url": url, "blocked": True}
    price, psrc = extract_price(html)
    return {
        "id": pid, "url": url, "blocked": False,
        "title": clean_title(meta_og(html, "title")),
        "price": price, "price_source": psrc,
        "images": extract_images(html),
        "options": extract_options(html),
        "cached_at": int(time.time()),
    }

def product_id_from(s):
    m = re.search(r"/vp/products/(\d+)", s)
    return m.group(1) if m else (s if s.isdigit() else None)


# ─────────────────────────── 자제: 캐시 & 세션 ───────────────────────────

def cache_path(pid):
    return os.path.join(CACHE_DIR, f"{pid}.json")

def cache_get(pid, ttl_hours):
    p = cache_path(pid)
    if not os.path.exists(p):
        return None
    try:
        with open(p, encoding="utf-8") as f:
            d = json.load(f)
        if ttl_hours <= 0:
            return d
        if time.time() - d.get("cached_at", 0) <= ttl_hours * 3600:
            return d
    except Exception:
        pass
    return None

def cache_put(pid, data):
    os.makedirs(CACHE_DIR, exist_ok=True)
    with open(cache_path(pid), "w", encoding="utf-8") as f:
        json.dump(data, f, ensure_ascii=False, indent=2)

def session_load():
    if not os.path.exists(SESSION_FILE):
        return None
    try:
        with open(SESSION_FILE, encoding="utf-8") as f:
            d = json.load(f)
        if time.time() - d.get("saved_at", 0) <= SESSION_FRESH_SEC and d.get("cookies"):
            return d["cookies"]
    except Exception:
        pass
    return None

def session_save(cookies):
    try:
        keep = [c for c in cookies if c.get("name") in
                ("_abck", "bm_sz", "ak_bmsc", "bm_sv", "PCID", "x-coupang-accept-language")]
        with open(SESSION_FILE, "w", encoding="utf-8") as f:
            json.dump({"saved_at": int(time.time()), "cookies": keep}, f, ensure_ascii=False)
    except Exception:
        pass

def session_clear():
    try:
        os.remove(SESSION_FILE)
    except Exception:
        pass


# ─────────────────────────── 수집 ───────────────────────────

def scrape(arg, limit, headless, proxy, delay_min, delay_max, cache_ttl, use_cache):
    pid = product_id_from(arg)
    is_search = pid is None

    # 직접 상품 ID인데 캐시에 신선하게 있으면 → 브라우저 안 띄우고 즉시 반환 (요청 0)
    if not is_search and use_cache:
        c = cache_get(pid, cache_ttl)
        if c:
            print(f"· 캐시 적중: {pid} (네트워크 요청 없음)")
            return {"query": arg, "is_search": False, "search_blocked": False,
                    "count": 1, "elapsed_sec": 0.0, "from_cache": 1, "products": [c]}

    saved_cookies = session_load()
    warmup_s = 6 if saved_cookies else 25    # 세션 있으면 예열 단축 → 홈 부하↓
    captured = {"search_html": "", "fetched": [], "cookies": []}

    def flow(page: Page):
        # 저장된 세션 쿠키 재주입 (있으면)
        if saved_cookies:
            try:
                page.context.add_cookies([
                    {"name": c["name"], "value": c["value"], "domain": ".coupang.com", "path": "/"}
                    for c in saved_cookies if c.get("name") and c.get("value") is not None
                ])
            except Exception:
                pass

        print(f"· 홈 {warmup_s}초 예열{' (세션 재사용)' if saved_cookies else ''}…", flush=True)
        page.wait_for_timeout(warmup_s * 1000)
        for _ in range(2):
            page.mouse.move(random.randint(80, 1200), random.randint(80, 600))
            page.mouse.wheel(0, random.randint(200, 500))
            page.wait_for_timeout(random.randint(500, 1100))

        if is_search:
            print(f"· 검색: {arg}", flush=True)
            page.goto(f"https://www.coupang.com/np/search?q={quote(arg)}", wait_until="domcontentloaded")
            try: page.wait_for_load_state("networkidle", timeout=12000)
            except Exception: pass
            page.wait_for_timeout(2500)
            captured["search_html"] = page.content()
            found = list(dict.fromkeys(re.findall(r"/vp/products/(\d+)", captured["search_html"])))
            # 캐시에 없는 것만 실제로 방문 (자제)
            todo = []
            for one in found:
                if use_cache and cache_get(one, cache_ttl):
                    captured["fetched"].append({"id": one, "cached": True})
                else:
                    todo.append(one)
                if len(captured["fetched"]) + len(todo) >= limit:
                    break
            print(f"· 상품 {len(found)}개 중 신규 {len(todo)}개 수집 (캐시 {len(captured['fetched'])}개 건너뜀)", flush=True)
        else:
            todo = [pid]

        for i, one in enumerate(todo, 1):
            if i > 1:
                d = random.uniform(delay_min, delay_max)   # 요청 간격 (정중)
                print(f"    · {d:.1f}s 대기…", flush=True)
                page.wait_for_timeout(int(d * 1000))
            url = f"https://www.coupang.com/vp/products/{one}"
            print(f"· [{i}/{len(todo)}] 상세 {one}", flush=True)
            page.goto(url, wait_until="domcontentloaded")
            try: page.wait_for_load_state("networkidle", timeout=12000)
            except Exception: pass
            page.wait_for_timeout(random.randint(1500, 3000))
            captured["fetched"].append({"id": one, "cached": False, "url": url, "html": page.content()})

        try:
            captured["cookies"] = page.context.cookies()
        except Exception:
            pass
        return page

    print("쿠팡 스크래퍼 —", "검색" if is_search else "상품", arg)
    print(f"모드: {'headless' if headless else 'headed'} · real_chrome · 예열 {warmup_s}s"
          + (" · proxy" if proxy else "") + (" · 캐시ON" if use_cache else " · 캐시OFF"))
    t0 = time.time()
    kwargs = dict(headless=headless, real_chrome=True, humanize=True,
                  network_idle=True, block_webrtc=True, page_action=flow)
    if proxy:
        kwargs["proxy"] = proxy
    StealthyFetcher.fetch("https://www.coupang.com", **kwargs)

    # 세션 쿠키 저장/무효화
    if captured["cookies"]:
        abck = next((c for c in captured["cookies"] if c.get("name") == "_abck"), None)
        session_save(captured["cookies"])

    # 결과 조립 (캐시 + 신규)
    results, from_cache = [], 0
    for item in captured["fetched"]:
        if item.get("cached"):
            c = cache_get(item["id"], cache_ttl)
            if c:
                results.append(c); from_cache += 1
            continue
        parsed = parse_product(item["id"], item["url"], item["html"])
        if not parsed.get("blocked"):
            cache_put(item["id"], parsed)     # 성공한 것만 캐시
        results.append(parsed)

    search_blocked = is_search and is_blocked(captured["search_html"])
    all_blocked = results and all(r.get("blocked") for r in results if not r.get("cached_at"))
    if search_blocked or all_blocked:
        session_clear()    # 차단당했으면 세션 무효화 → 다음엔 풀 예열

    return {"query": arg, "is_search": is_search, "search_blocked": search_blocked,
            "count": len(results), "elapsed_sec": round(time.time() - t0, 1),
            "from_cache": from_cache, "products": results}


def main():
    ap = argparse.ArgumentParser(description="쿠팡 상품 스크래퍼 (Akamai 우회 + 자제 로직)")
    ap.add_argument("query", help="검색어 또는 상품 ID/URL")
    ap.add_argument("--limit", type=int, default=3, help="검색 시 수집 개수 (기본 3)")
    ap.add_argument("--headless", action="store_true", help="headless (비권장)")
    ap.add_argument("--proxy", default=None, help="http://ID:PW@host:port")
    ap.add_argument("--delay-min", type=float, default=3.0, help="상품 간 최소 대기초 (기본 3)")
    ap.add_argument("--delay-max", type=float, default=8.0, help="상품 간 최대 대기초 (기본 8)")
    ap.add_argument("--cache-ttl", type=float, default=24, help="캐시 유효시간(시간). 0=무한 (기본 24)")
    ap.add_argument("--no-cache", action="store_true", help="캐시 무시하고 새로 수집")
    ap.add_argument("--out", default="coupang_result.json")
    a = ap.parse_args()

    data = scrape(a.query, a.limit, a.headless, a.proxy,
                  a.delay_min, a.delay_max, a.cache_ttl, not a.no_cache)

    print("\n" + "=" * 60)
    if data["search_blocked"]:
        print("⚠ 검색 차단됨(403) — IP rate-limit. IP 변경(핫스팟/프록시) 또는 쿨다운 후 재시도.")
    for p in data["products"]:
        if p.get("blocked"):
            print(f"  ✗ {p['id']} — 차단됨(403)")
            continue
        print(f"  ✓ {p['id']}")
        print(f"     상품명: {p.get('title')}")
        print(f"     가격  : {p['price']:,}원 ({p['price_source']})" if p.get("price") else "     가격  : (파싱 실패)")
        print(f"     이미지: {len(p.get('images', []))}장")
        for o in p.get("options", []):
            print(f"     옵션  : {o['name']} = {', '.join(o['values'][:8])}")
    print("=" * 60)
    print(f"총 {data['count']}개 (캐시 {data.get('from_cache', 0)}개) · {data['elapsed_sec']}초")
    with open(a.out, "w", encoding="utf-8") as f:
        json.dump(data, f, ensure_ascii=False, indent=2)
    print(f"→ 저장: {a.out}")


if __name__ == "__main__":
    main()
