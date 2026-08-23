# Tetragon fetch — 쿠팡 상품 상세 추출 검증
#
# 흐름: 홈 예열 → 검색 → 첫 상품 ID 추출 → 상품 상세 이동 → 데이터 추출
# 자연스러운 네비게이션(홈→검색→상품)이 Akamai 통과율을 높인다.
#
# 사용법: python src/probe_product.py [검색어] [--wait 25] [--headless]

import sys, re, time, json

args = sys.argv[1:]
headless = "--headless" in args
def after(flag, d=None): return args[args.index(flag)+1] if flag in args and args.index(flag)+1 < len(args) else d
wait_s = int(after("--wait", "25"))
keyword = next((a for a in args if not a.startswith("--") and not a.isdigit()), "무선마우스")

from playwright.sync_api import Page
from scrapling.fetchers import StealthyFetcher

search_url = f"https://www.coupang.com/np/search?q={keyword}"
captured = {"search_html": "", "product_html": "", "product_url": "", "product_id": ""}


def flow(page: Page):
    print(f"  [1] 홈 {wait_s}초 예열...", flush=True)
    page.wait_for_timeout(wait_s * 1000)

    print(f"  [2] 검색 이동: {keyword}", flush=True)
    page.goto(search_url, wait_until="domcontentloaded")
    try: page.wait_for_load_state("networkidle", timeout=12000)
    except Exception: pass
    page.wait_for_timeout(2500)
    captured["search_html"] = page.content()

    # 첫 상품 ID 추출
    ids = re.findall(r"/vp/products/(\d+)", captured["search_html"])
    if not ids:
        print("  [!] 검색결과에서 상품 링크를 못 찾음", flush=True)
        return page
    pid = ids[0]
    captured["product_id"] = pid
    product_url = f"https://www.coupang.com/vp/products/{pid}"
    captured["product_url"] = product_url

    print(f"  [3] 상품 상세 이동: {pid}", flush=True)
    page.goto(product_url, wait_until="domcontentloaded")
    try: page.wait_for_load_state("networkidle", timeout=12000)
    except Exception: pass
    page.wait_for_timeout(3000)
    captured["product_html"] = page.content()
    return page


def extract(html: str) -> dict:
    def meta(prop):
        m = re.search(rf'<meta[^>]+property=["\']{re.escape(prop)}["\'][^>]+content=["\']([^"\']+)', html) \
            or re.search(rf'<meta[^>]+content=["\']([^"\']+)["\'][^>]+property=["\']{re.escape(prop)}["\']', html)
        return m.group(1) if m else None
    def metaname(name):
        m = re.search(rf'<meta[^>]+name=["\']{re.escape(name)}["\'][^>]+content=["\']([^"\']+)', html)
        return m.group(1) if m else None

    title = meta("og:title") or (re.search(r"<title>([^<]+)</title>", html) or [None, None])[1]
    image = meta("og:image")
    # 가격: og:price / JSON 내 salePrice / 숫자+원 패턴
    price = meta("product:price:amount") or metaname("twitter:data1")
    if not price:
        m = re.search(r'"salePrice"\s*:\s*(\d+)', html) or re.search(r'"couponPrice"\s*:\s*(\d+)', html)
        if m: price = m.group(1)
    if not price:
        m = re.search(r'([0-9]{1,3}(?:,[0-9]{3})+)\s*원', html)
        if m: price = m.group(1)
    # 이미지 개수 (쿠팡 CDN)
    imgs = set(re.findall(r'https?://[^"\']*?coupangcdn\.com/[^"\']+\.(?:jpg|jpeg|png|webp)', html))
    return {"title": title, "price": price, "image": image, "cdn_images": len(imgs)}


def main():
    print(f"== 쿠팡 상품 상세 추출 probe ==  keyword={keyword} wait={wait_s}s headless={headless}")
    t0 = time.time()
    StealthyFetcher.fetch(
        "https://www.coupang.com",
        headless=headless, real_chrome=True,
        humanize=True, network_idle=True, block_webrtc=True,
        page_action=flow,
    )

    s_html, p_html = captured["search_html"], captured["product_html"]
    denied = lambda h: any(n in h for n in ("Access Denied", "errors.edgesuite.net")) and len(h) < 5000

    search_ids = len(set(re.findall(r"/vp/products/(\d+)", s_html)))
    print("\n--- 검색 단계 ---")
    print(f"  html: {len(s_html):,}  denied: {denied(s_html)}  상품ID: {search_ids}개")

    print("\n--- 상품 상세 단계 ---")
    if not p_html:
        print("  상세 미도달")
        print("\nVERDICT: ❌ 상품 상세 실패")
        return
    print(f"  product_id: {captured['product_id']}")
    print(f"  url       : {captured['product_url']}")
    print(f"  html      : {len(p_html):,}  denied: {denied(p_html)}")
    data = extract(p_html)
    print(f"  상품명    : {data['title']!r}")
    print(f"  가격      : {data['price']!r}")
    print(f"  대표이미지: {data['image']!r}")
    print(f"  CDN 이미지: {data['cdn_images']}개")

    ok = not denied(p_html) and data["title"] and (data["price"] or data["cdn_images"] > 0)
    print(f"\n엔드투엔드: {time.time()-t0:.1f}s")
    print("VERDICT:", "✅✅✅ 상품 상세 추출 성공!" if ok else "❌ 상세 도달했으나 데이터 추출 실패(셀렉터 조정 필요)")


if __name__ == "__main__":
    main()
