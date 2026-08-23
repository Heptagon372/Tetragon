# 쿠팡 상품 옵션/변형 구조 조사 — 옵션 있는 상품의 HTML/JSON 임베드 구조를 덤프.
import re, sys, json
from scrapling.fetchers import StealthyFetcher
from playwright.sync_api import Page

arg = sys.argv[1] if len(sys.argv) > 1 else "반팔티셔츠"
direct_pid = arg if arg.isdigit() else None
keyword = None if direct_pid else arg
cap = {"html": "", "pid": ""}

def flow(page: Page):
    page.wait_for_timeout(25000)
    if direct_pid:
        pid = direct_pid
    else:
        page.goto(f"https://www.coupang.com/np/search?q={keyword}", wait_until="domcontentloaded")
        page.wait_for_timeout(2500)
        ids = re.findall(r"/vp/products/(\d+)", page.content())
        if not ids:
            print("검색 차단(403) — 직접 pid 인자로 실행: python src/inspect_options.py <상품ID>")
            return page
        pid = ids[0]
    cap["pid"] = pid
    page.goto(f"https://www.coupang.com/vp/products/{pid}", wait_until="domcontentloaded")
    try: page.wait_for_load_state("networkidle", timeout=12000)
    except Exception: pass
    page.wait_for_timeout(3500)
    cap["html"] = page.content()
    return page

StealthyFetcher.fetch("https://www.coupang.com", headless=False, real_chrome=True,
                      humanize=True, network_idle=True, block_webrtc=True, page_action=flow)
html = cap["html"]
print(f"=== 옵션 구조 조사 pid={cap['pid']} htmlLen={len(html):,} ===\n")

# 1) 옵션 관련 키워드 등장 위치
for kw in ['optionList', 'vendorItem', 'attributeList', 'optionValue', 'rocketOption',
           'item_option', '"attributes"', 'optionCategory', 'selectedItem', 'itemList',
           'skuId', 'vendorItemId', 'unitPrice', 'PRELOADED', 'window.__']:
    idxs = [m.start() for m in re.finditer(re.escape(kw), html)]
    if idxs:
        print(f"[{kw}] {len(idxs)}회, 첫 위치 {idxs[0]}")

# 2) DOM 옵션 요소 클래스 추정
print("\n--- 옵션 관련 class 후보 ---")
classes = set(re.findall(r'class="([^"]*(?:option|Option|attribute|Attribute|sku)[^"]*)"', html))
for c in sorted(classes)[:25]:
    print("  ", c)

# 3) 큰 JSON 블록에서 option 관련 스니펫 덤프
print("\n--- optionList/vendorItem 스니펫 ---")
for kw in ['optionList', 'attributeList', 'vendorItems']:
    m = re.search(re.escape(kw), html)
    if m:
        s = max(0, m.start()-30)
        print(f"\n[{kw}] …")
        print(html[s:s+600])
