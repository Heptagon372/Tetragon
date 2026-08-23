# 쿠팡 상품 HTML을 파일로 1회만 저장 → 이후 오프라인 분석(재요청 없음)
import sys, re
from scrapling.fetchers import StealthyFetcher
from playwright.sync_api import Page

pid = sys.argv[1] if len(sys.argv) > 1 else "8779962500"
out = sys.argv[2] if len(sys.argv) > 2 else "product.html"
cap = {"html": ""}

def flow(page: Page):
    page.wait_for_timeout(25000)
    page.goto(f"https://www.coupang.com/vp/products/{pid}", wait_until="domcontentloaded")
    try: page.wait_for_load_state("networkidle", timeout=12000)
    except Exception: pass
    page.wait_for_timeout(3500)
    cap["html"] = page.content()
    return page

StealthyFetcher.fetch("https://www.coupang.com", headless=False, real_chrome=True,
                      humanize=True, network_idle=True, block_webrtc=True, page_action=flow)
with open(out, "w", encoding="utf-8") as f:
    f.write(cap["html"])
print(f"saved {out} ({len(cap['html']):,} bytes) pid={pid} blocked={'Access Denied' in cap['html'] and len(cap['html'])<5000}")
