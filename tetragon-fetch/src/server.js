// Tetragon fetch 사이드카 — REST /fetch (P1 MVP)
//
// .NET IBrowserFetcher(HttpBrowserFetcher)가 호출하는 엔드포인트.
// 실제 브라우저 엔진으로 URL을 가져와 { html, status, cookies, blocked }를 돌려준다.
//
// 검증 로그(docs/ANTIBOT-FETCH-DESIGN.md §6.5)대로, 프록시 없이 보호 엔드포인트는
// 대부분 blocked=true가 된다. 프록시는 요청 body의 proxy 또는 PROXY 환경변수로 준다.
//
// 실행: node src/server.js   (PORT 기본 8080, API_KEY 있으면 X-Api-Key 검사)
//
// 엔진 교체 지점: createContext() 한 곳. 지금은 playwright-extra+stealth.
// 통과율이 부족하면 Camoufox/rebrowser-playwright로 이 함수만 바꾼다.

import http from 'node:http';
// rebrowser-playwright: CDP Runtime.enable 누수까지 막은 패치 빌드(stealth보다 강함).
// 설계문서 §6.6 — puppeteer-stealth 단독은 최신 Akamai 센서에 잡힌다.
process.env.REBROWSER_PATCHES_RUNTIME_FIX_MODE ||= 'addBinding';
process.env.REBROWSER_PATCHES_SOURCE_URL ||= 'jquery.min.js';
import { addExtra } from 'playwright-extra';
import { chromium as rebrowserChromium } from 'rebrowser-playwright';
import StealthPlugin from 'puppeteer-extra-plugin-stealth';

// rebrowser 패치 빌드를 playwright-extra로 감싸 stealth 플러그인까지 얹는다(이중 방어).
const chromium = addExtra(rebrowserChromium);
chromium.use(StealthPlugin());

const PORT = Number(process.env.PORT || 8080);
const API_KEY = process.env.API_KEY || null;
const CHANNEL = process.env.CHANNEL || null; // 'chrome' 권장
const DEFAULT_PROXY = process.env.PROXY || null;

const rand = (min, max) => Math.random() * (max - min) + min;
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

// http://user:pass@host:port → Playwright proxy 객체 (인증 분리 필수)
function parseProxy(s) {
  if (!s) return null;
  try {
    const u = new URL(s);
    const p = { server: `${u.protocol}//${u.host}` };
    if (u.username) p.username = decodeURIComponent(u.username);
    if (u.password) p.password = decodeURIComponent(u.password);
    return p;
  } catch {
    return { server: s };
  }
}

// 브라우저는 프로세스 생명주기 동안 재사용, 컨텍스트는 요청마다 폐기.
let browserPromise = null;
function getBrowser() {
  if (!browserPromise) {
    const opts = { headless: true };
    if (CHANNEL) opts.channel = CHANNEL;
    browserPromise = chromium.launch(opts);
  }
  return browserPromise;
}

async function humanize(page, profile) {
  if (profile === 'None') return;
  const rounds = profile === 'Light' ? 1 : 3;
  await sleep(rand(profile === 'Light' ? 500 : 1500, profile === 'Light' ? 1200 : 3500));
  for (let i = 0; i < rounds; i++) {
    await page.mouse.move(rand(60, 1200), rand(80, 600), { steps: Math.floor(rand(5, 15)) }).catch(() => {});
    await sleep(rand(200, 700));
    await page.mouse.wheel(0, rand(200, 600)).catch(() => {});
    await sleep(rand(400, 1200));
  }
}

function isBlocked(html, status) {
  if (status === 403 || status === 428) return true;
  const needles = ['Access Denied', 'Reference #', 'errors.edgesuite.net', 'Pardon Our Interruption', '비정상적인 접근'];
  return needles.some((n) => html.includes(n)) && html.length < 5000;
}

function cookieHeaderFrom(cookies) {
  return cookies.map((c) => `${c.name}=${c.value}`).join('; ');
}

// ── 핵심: 한 번의 fetch 처리 ──
async function doFetch(req) {
  const {
    url,
    waitForSelector = null,
    behavior = 'Human',
    cookieHeader = null,
    proxy = DEFAULT_PROXY,
    warmupHost = null, // 예: "https://www.coupang.com/" — 세션 예열용
  } = req;

  if (!url) throw new Error('url required');

  const browser = await getBrowser();
  const ctxOpts = {
    locale: 'ko-KR',
    timezoneId: 'Asia/Seoul',
    viewport: { width: 1366, height: 900 },
    userAgent:
      'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36',
  };
  if (proxy) ctxOpts.proxy = typeof proxy === 'string' ? parseProxy(proxy) : proxy;

  // fall through: ctxOpts.proxy 는 아래에서 parseProxy로 설정됨
  const context = await browser.newContext(ctxOpts);
  try {
    if (cookieHeader) {
      const host = new URL(url).hostname;
      const cookies = cookieHeader.split(';').map((kv) => {
        const [name, ...rest] = kv.trim().split('=');
        return { name, value: rest.join('='), domain: '.' + host.replace(/^www\./, ''), path: '/' };
      }).filter((c) => c.name);
      await context.addCookies(cookies).catch(() => {});
    }

    const page = await context.newPage();

    // 세션 예열 (보호 엔드포인트 딥링크는 홈 먼저 태우면 통과율↑)
    if (warmupHost) {
      await page.goto(warmupHost, { waitUntil: 'domcontentloaded', timeout: 45000 }).catch(() => {});
      await humanize(page, behavior);
    }

    const resp = await page.goto(url, { waitUntil: 'domcontentloaded', timeout: 45000 });
    const status = resp ? resp.status() : 0;
    await humanize(page, behavior);
    if (waitForSelector) {
      await page.waitForSelector(waitForSelector, { timeout: 12000 }).catch(() => {});
    } else {
      await page.waitForLoadState('networkidle', { timeout: 12000 }).catch(() => {});
    }

    const html = await page.content();
    const finalUrl = page.url();
    const cookies = await context.cookies();
    const blocked = isBlocked(html, status);

    return {
      statusCode: status,
      html,
      finalUrl,
      setCookie: cookieHeaderFrom(cookies),
      challengeSolved: false,
      blocked,
    };
  } finally {
    await context.close().catch(() => {});
  }
}

// ── HTTP 서버 ──
const server = http.createServer(async (req, res) => {
  const send = (code, obj) => {
    res.writeHead(code, { 'content-type': 'application/json; charset=utf-8' });
    res.end(JSON.stringify(obj));
  };

  if (req.method === 'GET' && req.url === '/healthz') {
    const up = browserPromise !== null;
    return send(200, { ok: true, browserUp: up });
  }

  if (req.method === 'POST' && req.url === '/fetch') {
    if (API_KEY && req.headers['x-api-key'] !== API_KEY) return send(401, { error: 'unauthorized' });
    let body = '';
    req.on('data', (c) => (body += c));
    req.on('end', async () => {
      try {
        const parsed = JSON.parse(body || '{}');
        const t0 = Date.now();
        const result = await doFetch(parsed);
        console.log(`[fetch] ${parsed.url} → ${result.statusCode} blocked=${result.blocked} ${(Date.now() - t0) / 1000}s`);
        send(200, result);
      } catch (e) {
        console.error('[fetch] error:', e.message);
        send(500, { error: e.message });
      }
    });
    return;
  }

  send(404, { error: 'not found' });
});

server.listen(PORT, () => {
  console.log(`tetragon-fetch sidecar listening on :${PORT} (channel=${CHANNEL || 'bundled'}, proxy=${DEFAULT_PROXY ? 'set' : 'none'}, apiKey=${API_KEY ? 'set' : 'none'})`);
});
