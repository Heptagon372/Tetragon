// Tetragon fetch — rebrowser-playwright 로 CDP Runtime.enable 누수까지 막고 재측정
//
// 가설: 이 IP는 밴이 아님(홈 통과). _abck가 검증 안 되는 건 센서가 headless를
//       CDP 누수로 잡기 때문. rebrowser-patches가 그 누수를 막으면 이 IP에서도
//       보호 엔드포인트가 열릴 수 있다.
//
// 사용법: node src/probe-rebrowser.js [url] [--channel chrome] [--warmup]

import { chromium } from 'rebrowser-playwright';

const args = process.argv.slice(2);
const chIdx = args.indexOf('--channel');
const channel = chIdx !== -1 ? args[chIdx + 1] : null;
const warmup = args.includes('--warmup');
const pxIdx = args.indexOf('--proxy');
const proxyArg = pxIdx !== -1 ? args[pxIdx + 1] : null;
// 첫 http 인자가 프록시일 수 있으니, --proxy 값은 URL 후보에서 제외한다.
const url = args.find((a) => a.startsWith('http') && a !== proxyArg) || 'https://www.coupang.com/np/search?q=무선마우스';

// http://user:pass@host:port → Playwright proxy 객체 (인증은 분리해야 동작)
function parseProxy(s) {
  if (!s) return null;
  try {
    const u = new URL(s);
    const proxy = { server: `${u.protocol}//${u.host}` };
    if (u.username) proxy.username = decodeURIComponent(u.username);
    if (u.password) proxy.password = decodeURIComponent(u.password);
    return proxy;
  } catch {
    return { server: s };
  }
}

const rand = (min, max) => Math.random() * (max - min) + min;
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

async function humanize(page, rounds = 3) {
  await sleep(rand(1200, 2800));
  for (let i = 0; i < rounds; i++) {
    await page.mouse.move(rand(60, 1200), rand(80, 600), { steps: Math.floor(rand(6, 16)) }).catch(() => {});
    await sleep(rand(200, 700));
    await page.mouse.wheel(0, rand(200, 600)).catch(() => {});
    await sleep(rand(500, 1400));
  }
}

function abckState(cookies) {
  const c = cookies.find((x) => x.name === '_abck');
  if (!c) return 'none';
  // Akamai _abck: 뒤쪽 ~숫자~ 구획. -1=미검증(봇), 0=검증
  const m = c.value.match(/~(-?\d+)~/g);
  const last = m ? m[m.length - 1] : '?';
  return `${last} (${last === '~-1~' ? '봇의심' : last === '~0~' ? '검증됨' : '기타'})`;
}

function denied(html) {
  return ['Access Denied', 'errors.edgesuite.net', 'Reference #'].some((n) => html.includes(n));
}

const main = async () => {
  process.env.REBROWSER_PATCHES_RUNTIME_FIX_MODE ||= 'addBinding';
  process.env.REBROWSER_PATCHES_SOURCE_URL ||= 'jquery.min.js';

  const proxy = parseProxy(proxyArg);
  console.log('== rebrowser probe ==');
  console.log('target:', url, '| channel:', channel || 'bundled', '| warmup:', warmup, '| proxy:', proxy ? proxy.server : 'none');
  console.log('runtime-fix:', process.env.REBROWSER_PATCHES_RUNTIME_FIX_MODE);
  console.log('');

  const opts = { headless: true };
  if (channel) opts.channel = channel;
  if (proxy) opts.proxy = proxy;

  const browser = await chromium.launch(opts);
  const context = await browser.newContext({
    locale: 'ko-KR',
    timezoneId: 'Asia/Seoul',
    viewport: { width: 1366, height: 900 },
    userAgent:
      'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36',
  });
  const page = await context.newPage();

  try {
    if (warmup) {
      console.log('[warmup] 홈 예열 (최대 ~25초, _abck ~0~ 되면 조기 종료)...');
      const wt = Date.now();
      await page.goto('https://www.coupang.com/', { waitUntil: 'domcontentloaded', timeout: 30000 }).catch(() => {});
      // 센서가 _abck를 검증하도록 실제 이벤트를 계속 발생시키며 2초마다 상태 확인.
      let validated = false;
      for (let i = 0; i < 12 && (Date.now() - wt) < 25000; i++) {
        await page.mouse.move(rand(60, 1200), rand(80, 600), { steps: Math.floor(rand(6, 16)) }).catch(() => {});
        await page.mouse.wheel(0, rand(150, 500)).catch(() => {});
        await sleep(1500);
        const st = abckState(await context.cookies());
        console.log(`  예열 ${((Date.now() - wt) / 1000).toFixed(0)}s abck:`, st);
        if (st.startsWith('~0~')) { validated = true; break; }
      }
      console.log('[warmup] _abck 검증됨?:', validated ? 'YES ✅' : 'NO (여전히 봇 판정)');
      await sleep(rand(500, 1200));
    }

    const t0 = Date.now();
    const resp = await page.goto(url, { waitUntil: 'domcontentloaded', timeout: 45000 });
    const status = resp ? resp.status() : 0;
    await humanize(page, 3);
    try { await page.waitForLoadState('networkidle', { timeout: 12000 }); } catch {}

    const html = await page.content();
    const title = await page.title();
    const cookies = await context.cookies();
    const isDenied = denied(html);

    const products = await page.evaluate(() => {
      const sel = 'ul#productList li, li.search-product, .baby-product, .search-product-wrap';
      return document.querySelectorAll(sel).length;
    }).catch(() => 0);

    console.log('--- RESULT ---');
    console.log('elapsed :', ((Date.now() - t0) / 1000).toFixed(1) + 's');
    console.log('status  :', status);
    console.log('title   :', JSON.stringify(title));
    console.log('html len:', html.length.toLocaleString());
    console.log('_abck   :', abckState(cookies));
    console.log('denied  :', isDenied);
    console.log('products:', products);

    const ok = status === 200 && !isDenied && (products > 0 || html.length > 200000);
    console.log('');
    console.log('VERDICT :', ok ? '✅✅✅ 보호 엔드포인트 통과!' : '❌ 미통과');
  } catch (e) {
    console.log('error:', e.message);
  } finally {
    await browser.close();
  }
};

main().catch((e) => { console.error('fatal:', e); process.exit(1); });
