import { useState } from 'react'
import { useMutation, useQuery } from '@tanstack/react-query'
import { api, type StockScanItem } from '../../shared/api'
import { Empty, ErrorBox, Page } from '../../shared/ui'

// 쿠팡 품절·저재고 스캐너.
// 목록 페이지를 훑어(상품마다 상세를 열지 않고) 품절/저재고 상품만 골라낸다.
// 목록 1페이지 = 1 요청이라 페이지 수만큼만 쿠팡에 접근한다(IP 차단 방지).
export default function StockScan() {
  const [mode, setMode] = useState<'keyword' | 'category'>('keyword')
  const [keyword, setKeyword] = useState('')
  const [categoryCode, setCategoryCode] = useState('')
  const [maxPages, setMaxPages] = useState(3)
  const [includeInStock, setIncludeInStock] = useState(false)

  const plugins = useQuery({ queryKey: ['plugins'], queryFn: api.plugins })
  const coupang = plugins.data?.suppliers.find((s) => s.code === 'coupang')
  const ready = coupang?.isAvailable ?? false

  const scan = useMutation({
    mutationFn: () =>
      api.stockScan({
        keyword: mode === 'keyword' ? keyword.trim() || undefined : undefined,
        categoryCode: mode === 'category' ? categoryCode.trim() || undefined : undefined,
        maxPages,
        includeInStock,
      }),
  })

  const canScan = ready && !scan.isPending &&
    (mode === 'keyword' ? keyword.trim().length > 0 : categoryCode.trim().length > 0)

  const result = scan.data

  return (
    <Page
      title="쿠팡 품절·저재고 스캔"
      desc="카테고리나 검색어로 목록을 훑어 품절·저재고 상품을 골라냅니다. 목록 페이지만 읽어 요청을 최소화합니다."
    >
      {!ready && (
        <div className="error-box">
          쿠팡 공급처가 아직 준비되지 않았습니다. fetch 사이드카를 실행하고 설정에 등록해야 스캔할 수 있습니다.
        </div>
      )}

      <div className="card">
        <h2 className="card-title">스캔 조건</h2>

        <div className="row" style={{ marginBottom: 12 }}>
          <button
            className={mode === 'keyword' ? '' : 'ghost'}
            onClick={() => setMode('keyword')}
          >
            검색어
          </button>
          <button
            className={mode === 'category' ? '' : 'ghost'}
            onClick={() => setMode('category')}
          >
            카테고리 코드
          </button>
        </div>

        {mode === 'keyword' ? (
          <div className="field">
            <label>검색어</label>
            <input
              value={keyword}
              placeholder="예: 무선마우스"
              onChange={(e) => setKeyword(e.target.value)}
              onKeyDown={(e) => { if (e.key === 'Enter' && canScan) scan.mutate() }}
            />
          </div>
        ) : (
          <div className="field">
            <label>쿠팡 카테고리 코드</label>
            <input
              value={categoryCode}
              placeholder="예: 194176 (쿠팡 카테고리 URL의 categories/뒤 숫자)"
              onChange={(e) => setCategoryCode(e.target.value)}
              onKeyDown={(e) => { if (e.key === 'Enter' && canScan) scan.mutate() }}
            />
            <small className="muted">
              쿠팡에서 카테고리를 열면 주소가 <span className="mono">.../np/categories/194176</span> 형태입니다. 그 숫자를 넣으세요.
            </small>
          </div>
        )}

        <div className="grid grid-2">
          <div className="field">
            <label>훑을 페이지 수 (1페이지 ≈ 상품 78개)</label>
            <input
              type="number" min={1} max={10} value={maxPages}
              onChange={(e) => setMaxPages(Math.max(1, Math.min(10, Number(e.target.value) || 1)))}
            />
          </div>
          <div className="field" style={{ justifyContent: 'flex-end' }}>
            <label className="row" style={{ gap: 8, cursor: 'pointer' }}>
              <input
                type="checkbox" checked={includeInStock} style={{ width: 'auto' }}
                onChange={(e) => setIncludeInStock(e.target.checked)}
              />
              정상 재고 상품도 함께 표시
            </label>
          </div>
        </div>

        <div className="row-between" style={{ marginTop: 8 }}>
          <span className="muted" style={{ fontSize: 12 }}>
            페이지당 홈 예열이 있어 페이지 하나에 20~30초쯤 걸립니다. 천천히 진행됩니다.
          </span>
          <button disabled={!canScan} onClick={() => scan.mutate()}>
            {scan.isPending ? '스캔 중…' : '스캔 시작'}
          </button>
        </div>
      </div>

      <ErrorBox error={scan.error} />

      {result && (
        <div className="card" style={{ marginTop: 16 }}>
          <div className="row-between" style={{ marginBottom: 12 }}>
            <h2 className="card-title" style={{ margin: 0 }}>
              결과 — 품절 {result.soldOutCount} · 저재고 {result.lowStockCount}
            </h2>
            <span className="muted" style={{ fontSize: 12 }}>
              {result.scannedPages}페이지 · 상품 {result.totalScanned}개 스캔
            </span>
          </div>

          {result.error && (
            <div className="error-box" style={{ marginBottom: 12 }}>
              일부 페이지에서 중단됨: {result.error} (그때까지의 결과만 표시)
            </div>
          )}

          {result.items.length === 0 ? (
            <Empty>
              {includeInStock ? '상품을 찾지 못했습니다.' : '품절·저재고 상품이 없습니다. 다른 카테고리/검색어나 더 많은 페이지로 시도해 보세요.'}
            </Empty>
          ) : (
            <div className="table-wrap">
              <table>
                <thead>
                  <tr>
                    <th>상태</th>
                    <th>상품명</th>
                    <th style={{ textAlign: 'right' }}>가격</th>
                    <th style={{ textAlign: 'right' }}>남은 수량</th>
                    <th>링크</th>
                  </tr>
                </thead>
                <tbody>
                  {result.items.map((item) => (
                    <StockRow key={item.sourceProductId} item={item} />
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </div>
      )}
    </Page>
  )
}

function StockRow({ item }: { item: StockScanItem }) {
  const badge =
    item.status === 'SoldOut' ? { cls: 'badge-danger', label: '품절' }
    : item.status === 'LowStock' ? { cls: 'badge-warn', label: '저재고' }
    : { cls: 'badge-ok', label: '정상' }

  return (
    <tr>
      <td><span className={`badge ${badge.cls}`}>{badge.label}</span></td>
      <td style={{ maxWidth: 460 }}>{item.name ?? <span className="muted">(이름 없음)</span>}</td>
      <td className="mono" style={{ textAlign: 'right' }}>
        {item.price != null ? `${item.price.toLocaleString()}원` : '—'}
      </td>
      <td className="mono" style={{ textAlign: 'right' }}>
        {item.remaining != null ? `${item.remaining}개` : '—'}
      </td>
      <td>
        <a href={item.url} target="_blank" rel="noreferrer" className="mono" style={{ fontSize: 12 }}>
          쿠팡 ↗
        </a>
      </td>
    </tr>
  )
}
