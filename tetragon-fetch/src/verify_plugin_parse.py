# CoupangSupplierPlugin.cs 의 파싱 정규식을 실제 쿠팡 HTML에 대입해 검증.
# C# 플러그인이 상품을 제대로 파싱하는지 런타임 .NET 없이 확인한다.

import re, sys
from scrapling.fetchers import StealthyFetcher
from playwright.sync_api import Page

pid = sys.argv[1] if len(sys.argv) > 1 else None
cap = {"html": ""}

def flow(page: Page):
    page.wait_for_timeout(25000)  # 홈 예열
    if pid:
        page.goto(f"https://www.coupang.com/vp/products/{pid}", wait_until="domcontentloaded")
    else:
        page.goto("https://www.coupang.com/np/search?q=무선마우스", wait_until="domcontentloaded")
        page.wait_for_timeout(2500)
        ids = re.findall(r"/vp/products/(\d+)", page.content())
        page.goto(f"https://www.coupang.com/vp/products/{ids[0]}", wait_until="domcontentloaded")
    try: page.wait_for_load_state("networkidle", timeout=12000)
    except Exception: pass
    page.wait_for_timeout(3000)
    cap["html"] = page.content()
    return page

StealthyFetcher.fetch("https://www.coupang.com", headless=False, real_chrome=True,
                      humanize=True, network_idle=True, block_webrtc=True, page_action=flow)
html = cap["html"]

# ── C# 플러그인과 동일한 정규식 ──
def meta_og(prop):
    for pat in (rf'<meta[^>]+property=["\']og:{prop}["\'][^>]+content=["\']([^"\']*)',
                rf'<meta[^>]+content=["\']([^"\']*)["\'][^>]+property=["\']og:{prop}["\']'):
        m = re.search(pat, html, re.I)
        if m: return m.group(1)
    return None

def clean_title(raw):
    if not raw: return None
    t = raw.strip()
    b = t.rfind(" | 쿠팡")
    if b > 0: t = t[:b]
    d = t.rfind(" - ")
    if d > 20: t = t[:d]
    return t.strip()

title = clean_title(meta_og("title"))
sale = re.search(r'"(?:salePrice|couponPrice|finalPrice)"\s*:\s*(\d{3,})', html)
won = re.search(r'([0-9]{1,3}(?:,[0-9]{3})+)\s*원', html)
price = sale.group(1)+" (JSON)" if sale else (won.group(1)+" (표시가)" if won else None)
imgs = list(dict.fromkeys(re.findall(r'(?:https?:)?//[^"\'\s]*coupangcdn\.com/[^"\'\s]*vendor_inventory[^"\'\s]*\.(?:jpg|jpeg|png|webp)', html, re.I)))

print("=== CoupangSupplierPlugin 파싱 검증 ===")
print(f"html len : {len(html):,}")
print(f"상품명    : {title!r}")
print(f"가격      : {price!r}")
print(f"이미지    : {len(imgs)}장 (vendor_inventory)")
print(f"  대표    : {imgs[0] if imgs else None}")
ok = title and price and len(imgs) > 0
print()
print("결과:", "✅ 플러그인 파싱 OK (RawProduct 생성 가능)" if ok else "❌ 파싱 실패")
