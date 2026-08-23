// Tetragon fetch — Akamai 우회 검증 probe (P1 테스트)
//
// 실제 쿠팡 페이지를 stealth 브라우저 + 행동 시뮬레이션으로 가져와서
// Akamai Bot Manager를 통과하는지 경험적으로 확인한다.
//
// 사용법:
//   node src/probe.js [url] [--headed] [--proxy http://user:pass@host:port]
//
// 판정 신호:
//   - HTTP status (403/428/503 = 차단 계열)
//   - _abck 쿠키 형태:  ...~-1~...  = 센서 미검증(봇 의심) /  ...~0~... = 검증 통과 경향
//   - bm_sz 쿠키 존재 = Akamai 센서가 돌긴 함
//   - 챌린지 문구("Access Denied", "Reference #", "Pardon Our Interruption" 등)
//   - 상품 콘텐츠 셀렉터 존재 여부

import { chromium } from 'playwright-extra';
import StealthPlugin from 'puppeteer-extra-plugin-stealth';

chromium.use(StealthPlugin());

const args = process.argv.slice(2);
const headed = args.includes('--headed');
const newHeadless = args.includes('--headless-new');
const proxyIdx = args.indexOf('--proxy');
const proxyArg = proxyIdx !== -1 ? args[proxyIdx + 1] : null;
const chIdx = args.indexOf('--channel');
const channel = chIdx !== -1 ? args[chIdx + 1] : null;
const url =
  args.find((a) => a.startsWith('http')) ||
  'https://www.coupang.com/np/categories/194276'; // 기본: 카테고리 페이지 (Akamai 보호)

const rand = (min, max) => Math.random() * (max - min) + min;
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

// 사람 유사 행동: 무작위 대기 + 불규칙 스크롤 + 마우스 이동
async function humanize(page) {
  await sleep(rand(1500, 3500));
  const w = 1280;
  for (let i = 0; i < 4; i++) {
    await page.mouse.move(rand(50, w - 50), rand(80, 600), { steps: Math.floor(rand(5, 15)) });
    await sleep(rand(200, 700));
  }
  for (let i = 0; i < 3; i++) {
    await page.mouse.wheel(0, rand(200, 600));
    await sleep(rand(600, 1600));
  }
}

function classifyAbck(v) {
  if (!v) return 'none';
  // Akamai _abck: 마지막 ~숫자~ 구획이 -1이면 미검증(봇 의심), 0이면 검증 경향
  const m = v.match(/~(-?\d+)~/g);
  if (!m) return 'unparsed';
  const last = m[m.length - 1];
  if (last === '~-1~') return 'unvalidated (-1) → 봇 의심';
  if (last === '~0~') return 'validated (0) → 통과 경향';
  return `other ${last}`;
}

function detectChallenge(html) {
  const needles = [
    'Access Denied',
    'Reference #',
    'Pardon Our Interruption',
    'errors.edgesuite.net',
    'To discuss automated access',
    '비정상적인 접근',
    '접근이 제한',
  ];
  const hit = needles.filter((n) => html.includes(n));
  return hit;
}

const main = async () => {
  console.log('== Tetragon fetch probe ==');
  console.log('target :', url);
  console.log('mode   :', headed ? 'headed' : newHeadless ? 'new-headless' : 'headless',
    channel ? `channel=${channel}` : 'bundled', proxyArg ? `proxy=${proxyArg}` : 'no-proxy');
  console.log('');

  const launchOpts = { headless: !headed };
  if (channel) launchOpts.channel = channel;
  if (newHeadless) { launchOpts.headless = false; launchOpts.args = ['--headless=new']; }
  if (proxyArg) launchOpts.proxy = { server: proxyArg };

  const browser = await chromium.launch(launchOpts);
  const context = await browser.newContext({
    locale: 'ko-KR',
    timezoneId: 'Asia/Seoul',
    viewport: { width: 1280, height: 800 },
    userAgent:
      'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36',
  });
  const page = await context.newPage();

  let status = 0;
  const t0 = Date.now();
  try {
    const resp = await page.goto(url, { waitUntil: 'domcontentloaded', timeout: 45000 });
    status = resp ? resp.status() : 0;
  } catch (e) {
    console.log('goto error:', e.message);
  }

  await humanize(page);

  // 네트워크가 잠잠해질 때까지 잠깐 더 기다림 (센서 실행 시간 확보)
  try {
    await page.waitForLoadState('networkidle', { timeout: 15000 });
  } catch { /* 무시 */ }

  const html = await page.content();
  const title = await page.title();
  const cookies = await context.cookies();
  const abck = cookies.find((c) => c.name === '_abck');
  const bmsz = cookies.find((c) => c.name === 'bm_sz');
  const ak = cookies.find((c) => c.name === 'ak_bmsc');
  const challenge = detectChallenge(html);

  // 콘텐츠 지표: 카테고리/상품/검색 페이지에 흔한 셀렉터
  const contentProbe = await page.evaluate(() => {
    const sels = ['.baby-product', '.search-product', '.prod-buy-header__title', '#productList', 'a.baby-product-link'];
    const found = {};
    for (const s of sels) found[s] = document.querySelectorAll(s).length;
    return found;
  }).catch(() => ({}));

  console.log('--- RESULT ---');
  console.log('elapsed      :', ((Date.now() - t0) / 1000).toFixed(1) + 's');
  console.log('http status  :', status);
  console.log('page title   :', JSON.stringify(title));
  console.log('html length  :', html.length.toLocaleString());
  console.log('_abck        :', abck ? classifyAbck(abck.value) : 'none');
  console.log('bm_sz        :', bmsz ? 'present (Akamai 센서 동작)' : 'none');
  console.log('ak_bmsc      :', ak ? 'present' : 'none');
  console.log('challenge    :', challenge.length ? '⚠ ' + challenge.join(', ') : '없음');
  console.log('content sel  :', JSON.stringify(contentProbe));

  const productCount =
    (contentProbe['.baby-product'] || 0) +
    (contentProbe['.search-product'] || 0) +
    (contentProbe['a.baby-product-link'] || 0);

  const passed =
    status === 200 &&
    challenge.length === 0 &&
    (productCount > 0 || contentProbe['.prod-buy-header__title'] > 0) &&
    classifyAbck(abck?.value).startsWith('validated') !== false; // -1이면 아래 verdict에서 경고

  console.log('');
  console.log('VERDICT      :', passed ? '✅ 통과 (실 콘텐츠 확보)' : '❌ 미통과/차단 의심');
  if (abck && classifyAbck(abck.value).includes('-1')) {
    console.log('  note: _abck가 -1 (미검증). 콘텐츠는 왔어도 센서는 봇으로 보는 중 — 프록시/추가행동 필요.');
  }

  await browser.close();
};

main().catch((e) => {
  console.error('fatal:', e);
  process.exit(1);
});
