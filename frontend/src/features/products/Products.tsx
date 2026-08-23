import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Link } from 'react-router-dom'
import { api, downloadFile } from '../../shared/api'
import { Empty, ErrorBox, Page, StatusBadge, shortDate, won } from '../../shared/ui'

const STATUS_FILTERS = [
  { value: '', label: '전체' },
  { value: 'Ready', label: '등록대기' },
  { value: 'Listed', label: '등록됨' },
  { value: 'Blocked', label: '차단' },
  { value: 'Failed', label: '실패' },
]

export default function Products() {
  const [status, setStatus] = useState('')
  const [keyword, setKeyword] = useState('')
  const [selected, setSelected] = useState<Set<string>>(new Set())
  const [markets, setMarkets] = useState<Set<string>>(new Set())
  const queryClient = useQueryClient()

  const products = useQuery({
    queryKey: ['products', status, keyword],
    queryFn: () => api.products({ status, keyword, pageSize: 100 }),
  })
  const plugins = useQuery({ queryKey: ['plugins'], queryFn: api.plugins })

  const list = useMutation({
    mutationFn: () => api.requestListing([...selected], [...markets]),
    onSuccess: () => {
      setSelected(new Set())
      setTimeout(() => queryClient.invalidateQueries({ queryKey: ['products'] }), 1500)
    },
  })

  const sync = useMutation({
    mutationFn: () => api.checkInventory([...selected]),
  })

  const templates = useQuery({ queryKey: ['excelTemplates'], queryFn: api.excelTemplates })

  const exportExcel = useMutation({
    mutationFn: async (marketCode: string) => {
      const result = await downloadFile(`/excel/listings/${marketCode}`, {
        method: 'POST',
        // 선택이 없으면 등록 대기 상태 전체를 내보낸다
        body: JSON.stringify(
          selected.size > 0 ? { productIds: [...selected] } : { status: 'Ready' }),
      })
      if (!result.ok) throw new Error(result.error)
    },
  })

  const toggle = (id: string) => {
    const next = new Set(selected)
    next.has(id) ? next.delete(id) : next.add(id)
    setSelected(next)
  }

  const toggleMarket = (code: string) => {
    const next = new Set(markets)
    next.has(code) ? next.delete(code) : next.add(code)
    setMarkets(next)
  }

  const items = products.data?.items ?? []
  const allSelected = items.length > 0 && items.every((p) => selected.has(p.id))

  return (
    <Page title="상품" desc={`총 ${products.data?.total ?? 0}건`}>
      <div className="card">
        <div className="row-between" style={{ marginBottom: 14 }}>
          <div className="row">
            <select value={status} onChange={(e) => setStatus(e.target.value)} style={{ width: 130 }}>
              {STATUS_FILTERS.map((f) => <option key={f.value} value={f.value}>{f.label}</option>)}
            </select>
            <input
              value={keyword}
              onChange={(e) => setKeyword(e.target.value)}
              placeholder="상품명 검색"
              style={{ width: 200 }}
            />
          </div>
          <div className="row">
            <span className="muted" style={{ fontSize: 12 }}>
              엑셀 내보내기{selected.size > 0 ? ` (${selected.size}건)` : ' (등록대기 전체)'}:
            </span>
            {templates.data?.map((t) => (
              <button
                key={t.marketCode}
                className="ghost sm"
                title={t.uploadGuide}
                onClick={() => exportExcel.mutate(t.marketCode)}
                disabled={exportExcel.isPending}
              >
                {t.displayName}
              </button>
            ))}
          </div>
        </div>
        <ErrorBox error={exportExcel.error} />

        {selected.size > 0 && (
          <div className="card" style={{ background: 'var(--surface-2)', marginBottom: 14 }}>
            <div className="row-between">
              <div className="row">
                <strong style={{ fontSize: 13 }}>{selected.size}개 선택됨</strong>
                <span className="muted" style={{ fontSize: 12 }}>· 등록할 마켓:</span>
                {plugins.data?.marketplaces.map((m) => (
                  <label key={m.code} className="row" style={{ margin: 0, gap: 4, cursor: 'pointer' }}>
                    <input type="checkbox" checked={markets.has(m.code)} onChange={() => toggleMarket(m.code)} />
                    <span style={{ color: 'var(--text)', fontSize: 12.5 }}>
                      {m.displayName}
                      {!m.isLive && <span className="muted"> (시뮬)</span>}
                    </span>
                  </label>
                ))}
              </div>
              <div className="row">
                <button className="ghost sm" onClick={() => sync.mutate()} disabled={sync.isPending}>
                  재고 동기화
                </button>
                <button onClick={() => list.mutate()} disabled={markets.size === 0 || list.isPending}>
                  {list.isPending ? '요청 중…' : '마켓 등록'}
                </button>
              </div>
            </div>
            {list.data && (
              <div className="ok-box" style={{ marginTop: 10, marginBottom: 0 }}>
                {list.data.requested}건 등록 요청됨
                {list.data.skipped.length > 0 && ` · ${list.data.skipped.length}건 제외 (${list.data.skipped[0].reason})`}
              </div>
            )}
            <ErrorBox error={list.error} />
          </div>
        )}

        <ErrorBox error={products.error} />
        {products.isLoading && <Empty>불러오는 중…</Empty>}
        {items.length === 0 && !products.isLoading && (
          <Empty>상품이 없습니다. <Link to="/collect" style={{ color: 'var(--accent)' }}>수집 페이지</Link>에서 URL을 입력하세요.</Empty>
        )}

        {items.length > 0 && (
          <div className="table-wrap">
            <table>
              <thead>
                <tr>
                  <th style={{ width: 34 }}>
                    <input
                      type="checkbox"
                      checked={allSelected}
                      onChange={() => setSelected(allSelected ? new Set() : new Set(items.map((p) => p.id)))}
                    />
                  </th>
                  <th style={{ width: 52 }} />
                  <th>상품명</th>
                  <th>상태</th>
                  <th>공급처</th>
                  <th style={{ textAlign: 'right' }}>원가</th>
                  <th style={{ textAlign: 'right' }}>판매가</th>
                  <th style={{ textAlign: 'right' }}>옵션</th>
                  <th style={{ textAlign: 'right' }}>재고</th>
                  <th>수집일</th>
                </tr>
              </thead>
              <tbody>
                {items.map((p) => (
                  <tr key={p.id}>
                    <td><input type="checkbox" checked={selected.has(p.id)} onChange={() => toggle(p.id)} /></td>
                    <td>
                      {p.mainImage && <img className="thumb" src={p.mainImage} alt="" loading="lazy" />}
                    </td>
                    <td style={{ maxWidth: 320 }}>
                      <Link to={`/products/${p.id}`} style={{ color: 'var(--text)', fontWeight: 500 }}>
                        {p.name}
                      </Link>
                      {p.originalName && (
                        <div className="muted" style={{ fontSize: 11.5, marginTop: 2, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                          {p.originalName}
                        </div>
                      )}
                    </td>
                    <td><StatusBadge status={p.status} /></td>
                    <td className="muted">{p.supplier}</td>
                    <td style={{ textAlign: 'right' }} className="mono">
                      {p.basePrice.amount.toLocaleString()} {p.basePrice.currency}
                    </td>
                    <td style={{ textAlign: 'right', fontWeight: 600 }}>{won(p.salePrice)}</td>
                    <td style={{ textAlign: 'right' }} className="muted">{p.variantCount}</td>
                    <td style={{ textAlign: 'right' }} className="muted">{p.totalStock.toLocaleString()}</td>
                    <td className="muted" style={{ fontSize: 12 }}>{shortDate(p.createdAt)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>
    </Page>
  )
}
