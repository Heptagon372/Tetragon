import { useState } from 'react'
import { useMutation, useQuery } from '@tanstack/react-query'
import { Link } from 'react-router-dom'
import { api } from '../../shared/api'
import { Empty, ErrorBox, Page, won } from '../../shared/ui'

/**
 * 상품 점검 — 팔면 안 되는 상품을 미리 찾아낸다.
 *
 * 위탁판매에서 손해로 이어지는 세 가지를 본다:
 *   1. 공급처 품절 — 주문이 와도 발주할 수 없다 (취소율 = 마켓 페널티)
 *   2. 중복 등록   — 쿠팡이 중복으로 보고 노출을 깎는다
 *   3. 아이템위너  — 같은 물건을 여러 셀러가 팔아 최저가 싸움이 된다
 */
export default function Audit() {
  const [limit, setLimit] = useState(50)
  const [threshold, setThreshold] = useState(10)
  const [listedOnly, setListedOnly] = useState(false)

  const duplicates = useQuery({
    queryKey: ['duplicateAudit'],
    queryFn: () => api.duplicateAudit(1000),
    refetchOnWindowFocus: false,
  })

  // 재고 점검은 공급처 API를 상품 수만큼 호출해 느리다 — 버튼으로만 돌린다
  const stock = useMutation({
    mutationFn: () => api.stockAudit(limit, threshold, listedOnly),
  })

  const dup = duplicates.data
  const st = stock.data

  return (
    <Page title="상품 점검" desc="품절·중복·아이템위너 위험을 등록 전에 찾아냅니다.">
      {/* ── 재고 점검 ─────────────────────────────────────────────── */}
      <div className="card">
        <h2 className="card-title">공급처 재고 점검</h2>
        <p className="muted" style={{ fontSize: 12.5, marginTop: 0 }}>
          공급처에 물건이 없는데 마켓에서 팔리면 주문을 취소해야 하고, 취소율이 쌓이면 마켓 페널티를 받습니다.
        </p>

        <div className="row" style={{ gap: 12, alignItems: 'flex-end', marginBottom: 12 }}>
          <div style={{ width: 110 }}>
            <label>점검 개수</label>
            <input type="number" min={1} max={500} value={limit}
              onChange={(e) => setLimit(Number(e.target.value) || 1)} />
          </div>
          <div style={{ width: 130 }}>
            <label>재고부족 기준</label>
            <input type="number" min={1} value={threshold}
              onChange={(e) => setThreshold(Number(e.target.value) || 1)} />
          </div>
          <label className="row" style={{ gap: 6, marginBottom: 8, fontSize: 12.5 }}>
            <input type="checkbox" checked={listedOnly}
              onChange={(e) => setListedOnly(e.target.checked)} />
            등록된 상품만
          </label>
          <button onClick={() => stock.mutate()} disabled={stock.isPending}>
            {stock.isPending ? '확인 중…' : '재고 점검 실행'}
          </button>
        </div>
        <p className="muted" style={{ fontSize: 12, marginTop: 0 }}>
          상품 1건당 공급처를 한 번씩 조회합니다 — {limit}건이면 약 {Math.ceil(limit * 0.9)}초 걸립니다.
        </p>

        <ErrorBox error={stock.error} />

        {st && (
          <>
            <div className="row" style={{ gap: 18, fontSize: 13, marginBottom: 12 }}>
              <span>{st.checkedCount}건 확인</span>
              <Stat label="품절" count={st.soldOut.length} tone="danger" />
              <Stat label="재고부족" count={st.lowStock.length} tone="warn" />
              <Stat label="원가변동" count={st.priceChanged.length} tone="info" />
              {st.failed.length > 0 && <Stat label="조회실패" count={st.failed.length} tone="dim" />}
            </div>

            {st.riskCount === 0 && st.priceChanged.length === 0 && (
              <div className="ok-box">문제가 발견되지 않았습니다.</div>
            )}

            <EntryTable title="품절 — 마켓에서 내려야 합니다" entries={st.soldOut} tone="danger" />
            <EntryTable title={`재고부족 (${st.lowStockThreshold}개 미만)`} entries={st.lowStock} tone="warn" />
            <EntryTable title="공급처 원가 변동 — 마진 재확인 필요" entries={st.priceChanged} tone="info" showCost />
          </>
        )}
      </div>

      {/* ── 중복 ──────────────────────────────────────────────────── */}
      <div className="card">
        <div className="row-between" style={{ marginBottom: 8 }}>
          <h2 className="card-title" style={{ margin: 0 }}>중복 상품</h2>
          <button className="ghost sm" onClick={() => duplicates.refetch()} disabled={duplicates.isFetching}>
            {duplicates.isFetching ? '확인 중…' : '새로고침'}
          </button>
        </div>
        <ErrorBox error={duplicates.error} />
        {duplicates.isLoading && <div className="muted" style={{ fontSize: 12.5 }}>불러오는 중…</div>}

        {dup && dup.duplicates.length === 0 && <Empty>중복이 발견되지 않았습니다.</Empty>}
        {dup && dup.duplicates.length > 0 && (
          <>
            <p className="muted" style={{ fontSize: 12.5, marginTop: 0 }}>
              {dup.scannedCount}건 중 <strong>{dup.duplicates.length}개 그룹</strong>이 겹칩니다.
              같은 물건을 여러 번 올리면 쿠팡이 중복으로 보고 노출을 깎습니다.
            </p>
            <div style={{ maxHeight: 380, overflowY: 'auto' }}>
              {dup.duplicates.map((g) => (
                <div key={`${g.kind}-${g.key}`} style={{
                  padding: '10px 12px', marginBottom: 8,
                  border: '1px solid var(--border)', borderRadius: 7,
                }}>
                  <div className="row" style={{ gap: 8, marginBottom: 4 }}>
                    <span className="badge badge-warn">{g.kind}</span>
                    <strong style={{ fontSize: 13 }}>{g.products.length}건</strong>
                  </div>
                  <div className="muted" style={{ fontSize: 12, marginBottom: 6 }}>{g.reason}</div>
                  {g.products.slice(0, 5).map((p) => (
                    <div key={p.productId} style={{ fontSize: 12.5, padding: '1px 0' }}>
                      <Link to={`/products/${p.productId}`}>{p.productName.slice(0, 46)}</Link>
                      <span className="muted"> · {p.supplierName ?? p.supplierCode} · {p.status}</span>
                    </div>
                  ))}
                  {g.products.length > 5 && (
                    <div className="muted" style={{ fontSize: 12 }}>… 외 {g.products.length - 5}건</div>
                  )}
                </div>
              ))}
            </div>
          </>
        )}
      </div>

      {/* ── 아이템위너 ────────────────────────────────────────────── */}
      <div className="card">
        <h2 className="card-title">아이템위너 위험</h2>
        <p className="muted" style={{ fontSize: 12.5, marginTop: 0 }}>
          쿠팡은 같은 상품에 여러 셀러가 붙으면 <strong>가장 싼 쪽만 노출</strong>합니다(아이템위너).
          도매 상품은 누구나 같은 물건을 올릴 수 있어 특히 위험합니다.
        </p>
        <div className="warn-box" style={{ marginBottom: 12, fontSize: 12.5 }}>
          쿠팡 검색은 Akamai 봇 차단으로 수집할 수 없어, <strong>경쟁 셀러가 실제로 있는지는 확인하지 못합니다.</strong>
          아래는 상품 특성으로 추정한 위험도입니다 — 브랜드 없음, 낮은 최소구매수량, 공급처 상품명 그대로 사용 등.
        </div>

        {dup && dup.itemWinnerRisks.length === 0 && <Empty>위험 신호가 있는 상품이 없습니다.</Empty>}
        {dup && dup.itemWinnerRisks.length > 0 && (
          <>
            <div className="row" style={{ gap: 18, fontSize: 13, marginBottom: 10 }}>
              <Stat label="High" count={dup.itemWinnerRisks.filter((r) => r.level === 'High').length} tone="danger" />
              <Stat label="Medium" count={dup.itemWinnerRisks.filter((r) => r.level === 'Medium').length} tone="warn" />
            </div>
            <div className="table-wrap" style={{ maxHeight: 400 }}>
              <table>
                <thead>
                  <tr>
                    <th style={{ width: 70 }}>위험</th>
                    <th>상품</th>
                    <th>공급사</th>
                    <th>신호</th>
                  </tr>
                </thead>
                <tbody>
                  {dup.itemWinnerRisks.slice(0, 60).map((r) => (
                    <tr key={r.productId}>
                      <td>
                        <span className={`badge ${r.level === 'High' ? 'badge-danger' : 'badge-warn'}`}>
                          {r.level}
                        </span>
                      </td>
                      <td style={{ maxWidth: 260 }}>
                        <Link to={`/products/${r.productId}`}>{r.productName.slice(0, 40)}</Link>
                      </td>
                      <td className="muted" style={{ fontSize: 12 }}>{r.supplierName ?? '—'}</td>
                      <td className="muted" style={{ fontSize: 11.5, maxWidth: 320 }}>
                        {r.signals.map((s, i) => <div key={i}>· {s}</div>)}
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </>
        )}
      </div>
    </Page>
  )
}

function Stat({ label, count, tone }: { label: string; count: number; tone: string }) {
  const color = tone === 'danger' ? 'var(--danger)'
    : tone === 'warn' ? 'var(--warn)'
    : tone === 'info' ? 'var(--accent)'
    : 'var(--muted)'
  return (
    <span>
      <span className="muted">{label} </span>
      <strong style={{ color: count > 0 ? color : undefined }}>{count}</strong>
    </span>
  )
}

function EntryTable({
  title, entries, tone, showCost,
}: {
  title: string
  entries: { productId: string; productName: string; supplierName?: string; supplierCode: string
    previousStock: number; currentStock: number; previousCost: number; currentCost: number; costChangePct: number }[]
  tone: string
  showCost?: boolean
}) {
  if (entries.length === 0) return null
  return (
    <div style={{ marginTop: 14 }}>
      <h3 style={{
        fontSize: 13, margin: '0 0 6px',
        color: tone === 'danger' ? 'var(--danger)' : tone === 'warn' ? 'var(--warn)' : 'var(--accent)',
      }}>
        {title} ({entries.length})
      </h3>
      <div className="table-wrap" style={{ maxHeight: 260 }}>
        <table>
          <thead>
            <tr>
              <th>상품</th>
              <th>공급사</th>
              <th style={{ textAlign: 'right' }}>{showCost ? '원가' : '재고'}</th>
            </tr>
          </thead>
          <tbody>
            {entries.map((e) => (
              <tr key={e.productId}>
                <td style={{ maxWidth: 300 }}>
                  <Link to={`/products/${e.productId}`}>{e.productName.slice(0, 44)}</Link>
                </td>
                <td className="muted" style={{ fontSize: 12 }}>{e.supplierName ?? e.supplierCode}</td>
                <td style={{ textAlign: 'right', fontSize: 12.5 }}>
                  {showCost ? (
                    <>
                      <span className="muted">{won(e.previousCost)} → </span>
                      <strong>{won(e.currentCost)}</strong>
                      <span style={{ color: e.costChangePct > 0 ? 'var(--danger)' : 'var(--ok)' }}>
                        {' '}({e.costChangePct > 0 ? '+' : ''}{e.costChangePct.toFixed(1)}%)
                      </span>
                    </>
                  ) : (
                    <>
                      <span className="muted">{e.previousStock.toLocaleString()} → </span>
                      <strong style={{ color: e.currentStock === 0 ? 'var(--danger)' : undefined }}>
                        {e.currentStock.toLocaleString()}
                      </strong>
                    </>
                  )}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  )
}
