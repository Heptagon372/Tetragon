// Tetragon fetch — 세션 예열(warmup) 플로우 검증
//
// 가설: 쿠팡 홈은 통과하지만 카테고리/상품 딥링크는 Access Denied가 나는 이유는
//       유효한 Akamai 세션(_abck 검증 + bm_sz)이 없기 때문이다.
//       → 홈에서 사람처럼 행동해 세션을 예열한 뒤, 같은 컨텍스트로 목표 페이지에 진입한다.
//
// 흐름: 홈 진입 → humanize → 검색창에 키워드 입력(사람 타이핑) → 검색결과 →
//       상품 링크 클릭 → 상품 상세에서 데이터 추출
//
// 사용법: node src/probe-flow.js [검색어] [--channel chrome] [--headless-new]

import { chromium } from 'playwright-extra';
import StealthPlugin from 'puppeteer-extra-plugin-stealth';

chromium.use(StealthPlugin());

const args = process.argv.slice(2);
const newHeadless = args.includes('--headless-new');
const headed = args.includes('--headed');
const chIdx = args.indexOf('--channel');
const channel = chIdx !== -1 ? args[chIdx + 1] : null;
const pxIdx = args.indexOf('--proxy');
const proxyArg = pxIdx !== -1 ? args[pxIdx + 1] : null;
const keyword = args.find((a) => !a.startsWith('--') && a !== channel) || '무선마우스';

const rand = (min, max) => Math.random() * (max - min) + min;
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

async function humanize(page, rounds = 3) {
  await sleep(rand(1200, 2600));
  for (let i = 0; i < rounds; i++) {
    await page.mouse.move(rand(60, 1200), rand(80, 600), { steps: Math.floor(rand(6, 16)) });
    await sleep(rand(200, 600));
    await page.mouse.wheel(0, rand(200, 600));
    await sleep(rand(500, 1300));
  }
}

async function typeLikeHuman(page, selector, text) {
  await page.click(selector);
  for (const ch of text) {
    await page.type(selector, ch, { delay: rand(80, 200) });
  }
}

function abckState(cookies) {
  const c = cookies.find((x) => x.name === '_abck');
  if (!c) return 'none';
  const m = c.value.match(/~(-?\d+)~/g);
  if (!m) return 'unparsed';
  const last = m[m.length - 1];
  return last === '~-1~' ? '-1 (미검증/봇의심)' : last === '~0~' ? '0 (검증됨)' : `기타 ${last}`;
}

const step = (n, msg) => console.log(`[${n}] ${msg}`);

const main = async () => {
  console.log('== Tetragon warmup-flow probe ==');
  console.log('keyword:', keyword, '| channel:', channel || 'bundled', '| mode:',
    headed ? 'headed(실제창)' : newHeadless ? 'new-headless' : 'headless', proxyArg ? '| proxy=set' : '');
  console.log('');

  const launchOpts = { headless: !headed };
  if (channel) launchOpts.channel = channel;
  if (newHeadless && !headed) { launchOpts.headless = false; launchOpts.args = ['--headless=new']; }
  if (proxyArg) launchOpts.proxy = { server: proxyArg };

  const browser = await chromium.launch(launchOpts);
  const context = await browser.newContext({
    locale: 'ko-KR',
    timezoneId: 'Asia/Seoul',
    viewport: { width: 1366, height: 900 },
    userAgent:
      'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36',
  });
  const page = await context.newPage();

  try {
    // 1) 홈에서 세션 예열
    step(1, '홈 진입 + 장시간 예열...');
    const home = await page.goto('https://www.coupang.com/', { waitUntil: 'domcontentloaded', timeout: 45000 });
    console.log('    home status:', home?.status(), '| abck:', abckState(await context.cookies()));
    // 센서가 _abck를 검증하려면 실제 상호작용 + 체류시간이 필요. 최대 25초까지 관찰.
    for (let r = 0; r < 8; r++) {
      await humanize(page, 1);
      const st = abckState(await context.cookies());
      console.log(`    예열 ${r + 1}/8 후 abck:`, st);
      if (st.startsWith('0')) break;
      await sleep(rand(1500, 2500));
    }

    // 2) 검색어 입력 (사람 타이핑)
    step(2, `검색: "${keyword}"`);
    const searchSel = 'input#headerSearchKeyword, input[name="q"], input.headerSearchKeyword';
    try {
      await page.waitForSelector(searchSel, { timeout: 8000 });
      await typeLikeHuman(page, searchSel, keyword);
      await sleep(rand(300, 800));
      await page.keyboard.press('Enter');
    } catch {
      // 검색창을 못 찾으면 검색 URL로 직접 이동 (같은 예열된 컨텍스트라 통과 기대)
      console.log('    검색창 미발견 → 검색 URL로 이동');
      await page.goto('https://www.coupang.com/np/search?q=' + encodeURIComponent(keyword), {
        waitUntil: 'domcontentloaded', timeout: 45000,
      });
    }

    await page.waitForLoadState('domcontentloaded');
    await humanize(page, 2);
    try { await page.waitForLoadState('networkidle', { timeout: 12000 }); } catch {}

    const searchHtml = await page.content();
    const denied = searchHtml.includes('Access Denied') || searchHtml.includes('errors.edgesuite.net');
    console.log('    검색결과 status_url:', page.url().slice(0, 80));
    console.log('    검색결과 denied?:', denied, '| html:', searchHtml.length.toLocaleString(), '| abck:', abckState(await context.cookies()));

    // 3) 상품 목록 파싱
    step(3, '상품 목록 추출...');
    const products = await page.evaluate(() => {
      const out = [];
      const cards = document.querySelectorAll('ul#productList li, li.search-product, .baby-product');
      cards.forEach((c) => {
        const a = c.querySelector('a');
        const name = c.querySelector('.name, .baby-product-title, [class*="name"]')?.textContent?.trim();
        const price = c.querySelector('.price-value, strong.price-value, [class*="price"]')?.textContent?.trim();
        const href = a?.getAttribute('href');
        if (href) out.push({ name: name?.slice(0, 40), price, href });
      });
      return out.slice(0, 5);
    }).catch(() => []);
    console.log('    상품 수(상위5):', products.length);
    products.forEach((p, i) => console.log(`      #${i + 1} ${p.name} | ${p.price} | ${p.href?.slice(0, 60)}`));

    // 4) 첫 상품 상세 진입
    if (products.length && products[0].href) {
      step(4, '상품 상세 진입...');
      const url = new URL(products[0].href, 'https://www.coupang.com').href;
      await sleep(rand(800, 1800));
      const d = await page.goto(url, { waitUntil: 'domcontentloaded', timeout: 45000 });
      await humanize(page, 2);
      const detailHtml = await page.content();
      const dDenied = detailHtml.includes('Access Denied') || detailHtml.includes('errors.edgesuite.net');
      const detail = await page.evaluate(() => ({
        title: document.querySelector('h1.prod-buy-header__title, .prod-buy-header__title')?.textContent?.trim(),
        price: document.querySelector('.total-price strong, .prod-price .total-price')?.textContent?.trim(),
      })).catch(() => ({}));
      console.log('    상세 status:', d?.status(), '| denied?:', dDenied, '| html:', detailHtml.length.toLocaleString());
      console.log('    상품명:', JSON.stringify(detail.title));
      console.log('    가격  :', JSON.stringify(detail.price));

      const ok = !dDenied && (detail.title || detailHtml.length > 100000);
      console.log('');
      console.log('VERDICT:', ok ? '✅ 예열 플로우로 상품 상세까지 통과' : '❌ 상세에서 차단');
    } else {
      console.log('');
      console.log('VERDICT:', denied ? '❌ 검색결과 단계에서 차단' : '⚠ 통과했으나 상품 파싱 실패(셀렉터 갱신 필요)');
    }
  } catch (e) {
    console.log('flow error:', e.message);
  } finally {
    await browser.close();
  }
};

main().catch((e) => { console.error('fatal:', e); process.exit(1); });
