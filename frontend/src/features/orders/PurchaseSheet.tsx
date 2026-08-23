import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api } from '../../shared/api'
import { ErrorBox, won } from '../../shared/ui'

/**
 * 위탁판매 발주서.
 *
 * 주문 1건에 대해 "무엇을 · 어디서 · 누구에게 · 얼마에" 사야 하는지 모아 보여준다.
 * 도매꾹은 외부 발주 API가 없어 완전 자동 발주가 불가능하므로,
 * 사람이 바로 실행할 수 있도록 상품 링크와 복사용 배송지를 제공한다.
 */
export default function PurchaseSheet({ orderId, onClose }: { orderId: string; onClose: () => void }) {
  const queryClient = useQueryClient()
  const [supplierOrderNo, setSupplierOrderNo] = useState('')
  const [paidAmount, setPaidAmount] = useState('')
  const [copied, setCopied] = useState<string | null>(null)

  const sheet = useQuery({
    queryKey: ['purchaseSheet', orderId],
    queryFn: () => api.purchaseSheet(orderId),
  })

  const preflight = useQuery({
    queryKey: ['purchasePreflight', orderId],
    queryFn: () => api.purchasePreflight(orderId),
  })

  const autoPurchase = useMutation({
    mutationFn: () => api.autoPurchase(orderId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['orders'] })
      queryClient.invalidateQueries({ queryKey: ['purchaseSheet', orderId] })
      queryClient.invalidateQueries({ queryKey: ['purchasePreflight', orderId] })
      queryClient.invalidateQueries({ queryKey: ['wallet'] })
    },
  })

  const markPurchased = useMutation({
    mutationFn: () => api.markPurchased(orderId, {
      supplierOrderNo: supplierOrderNo || undefined,
      supplierPaidAmount: paidAmount ? Number(paidAmount) : undefined,
    }),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['orders'] })
      queryClient.invalidateQueries({ queryKey: ['purchaseSheet', orderId] })
    },
  })

  const copy = async (text: string, label: string) => {
    await navigator.clipboard.writeText(text)
    setCopied(label)
    setTimeout(() => setCopied(null), 1500)
  }

  const s = sheet.data

  return (
    <div className="card" style={{ borderColor: 'var(--accent)' }}>
      <div className="row-between" style={{ marginBottom: 12 }}>
        <h2 className="card-title" style={{ margin: 0 }}>발주서</h2>
        <button className="ghost sm" onClick={onClose}>닫기</button>
      </div>

      <ErrorBox error={sheet.error} />
      {sheet.isLoading && <div className="muted" style={{ fontSize: 12.5 }}>불러오는 중…</div>}

      {s && (
        <>
          {s.warnings.length > 0 && (
            <div className="error-box" style={{ marginBottom: 14 }}>
              {s.warnings.map((w, i) => <div key={i}>· {w}</div>)}
            </div>
          )}

          <div className="grid grid-2" style={{ gap: 16 }}>
            <div>
              <h3 style={{ fontSize: 13, margin: '0 0 8px', color: 'var(--muted)' }}>① 무엇을 · 어디서 사는가</h3>
              <table className="kv">
                <tbody>
                  <tr><th>상품</th><td>{s.productName ?? '—'}</td></tr>
                  {s.optionName && <tr><th>옵션</th><td>{s.optionName}</td></tr>}
                  <tr><th>수량</th><td><strong>{s.quantity}개</strong>
                    {s.minOrderQty && s.minOrderQty > s.quantity && (
                      <span className="badge badge-warn" style={{ marginLeft: 6 }}>
                        최소 {s.minOrderQty}개
                      </span>
                    )}
                  </td></tr>
                  <tr><th>공급처</th><td>{s.supplierName ?? s.supplierCode ?? '—'}</td></tr>
                  {s.supplierPhone && <tr><th>공급처 연락처</th><td className="mono">{s.supplierPhone}</td></tr>}
                  <tr><th>공급가</th><td>
                    {s.supplierUnitCost ? `${won(s.supplierUnitCost)} × ${s.quantity}` : '—'}
                  </td></tr>
                </tbody>
              </table>
              {s.supplierUrl && (
                <a href={s.supplierUrl} target="_blank" rel="noreferrer">
                  <button style={{ marginTop: 10, width: '100%' }}>공급처에서 주문하기 ↗</button>
                </a>
              )}
            </div>

            <div>
              <h3 style={{ fontSize: 13, margin: '0 0 8px', color: 'var(--muted)' }}>② 누구에게 보내는가</h3>
              <table className="kv">
                <tbody>
                  <tr><th>수령인</th><td>{s.receiverName ?? '—'}</td></tr>
                  <tr><th>연락처</th><td className="mono">{s.receiverPhone ?? '—'}</td></tr>
                  <tr><th>주소</th><td>
                    {s.receiverZipcode && <span className="mono">({s.receiverZipcode}) </span>}
                    {s.receiverAddress ?? '—'}
                  </td></tr>
                  {s.deliveryMessage && <tr><th>요청사항</th><td>{s.deliveryMessage}</td></tr>}
                </tbody>
              </table>
              <button
                className="ghost"
                style={{ marginTop: 10, width: '100%' }}
                disabled={!s.receiverAddress}
                onClick={() => copy(s.shippingLine, 'ship')}
              >
                {copied === 'ship' ? '복사됨 ✓' : '배송지 한 줄 복사'}
              </button>
            </div>
          </div>

          <div style={{ marginTop: 16, paddingTop: 14, borderTop: '1px solid var(--border)' }}>
            <h3 style={{ fontSize: 13, margin: '0 0 8px', color: 'var(--muted)' }}>③ 수익</h3>
            <div className="row" style={{ gap: 24, fontSize: 13 }}>
              <span>판매 <strong>{won(s.paidAmount)}</strong></span>
              <span className="muted">공급가 {s.estimatedCost ? won(s.estimatedCost) : '—'}</span>
              <span style={{
                color: (s.estimatedMargin ?? 0) > 0 ? 'var(--ok)' : 'var(--danger)',
                fontWeight: 600,
              }}>
                마진 {s.estimatedMargin != null ? won(s.estimatedMargin) : '—'}
              </span>
            </div>
          </div>

          <div style={{ marginTop: 16, paddingTop: 14, borderTop: '1px solid var(--border)' }}>
            <h3 style={{ fontSize: 13, margin: '0 0 8px', color: 'var(--muted)' }}>④ 발주</h3>

            {preflight.data && !s.supplierOrderNo && (
              <div style={{ marginBottom: 14 }}>
                <div className="row" style={{ gap: 20, fontSize: 12.5, marginBottom: 10 }}>
                  <span>
                    금고 가용 <strong>{won(preflight.data.walletAvailable)}</strong>
                  </span>
                  <span className="muted">발주 예상 {won(preflight.data.estimatedAmount)}</span>
                  {preflight.data.walletShortfall > 0 && (
                    <span style={{ color: 'var(--danger)', fontWeight: 600 }}>
                      {won(preflight.data.walletShortfall)} 부족
                    </span>
                  )}
                </div>

                {preflight.data.blockers.length > 0 && (
                  <div className="error-box" style={{ marginBottom: 10 }}>
                    {preflight.data.blockers.map((b, i) => <div key={i}>· {b}</div>)}
                  </div>
                )}

                {!preflight.data.canAutoOrder && preflight.data.blockers.length === 0
                  && preflight.data.capabilityReason && (
                  <div className="warn-box" style={{ marginBottom: 10 }}>
                    <div>· {preflight.data.capabilityReason}</div>
                    {preflight.data.capabilityHowToEnable && (
                      <div className="muted" style={{ marginTop: 4 }}>
                        {preflight.data.capabilityHowToEnable}
                      </div>
                    )}
                  </div>
                )}

                <div className="row">
                  <button
                    onClick={() => autoPurchase.mutate()}
                    disabled={!preflight.data.canAutoOrder || autoPurchase.isPending}
                    title={preflight.data.canAutoOrder ? undefined : '자동 발주를 할 수 없는 상태입니다'}
                  >
                    {autoPurchase.isPending ? '발주 중…' : '자동 발주 (금고에서 결제)'}
                  </button>
                  {preflight.data.supplierUrl && (
                    <a href={preflight.data.supplierUrl} target="_blank" rel="noreferrer">
                      <button className="ghost">공급사 사이트에서 주문하기 ↗</button>
                    </a>
                  )}
                </div>
                <ErrorBox error={autoPurchase.error} />
                {autoPurchase.data?.success && (
                  <div className="ok-box" style={{ marginTop: 10 }}>
                    발주 완료 · 공급처 주문번호 <strong>{autoPurchase.data.supplierOrderNo}</strong>
                    {autoPurchase.data.paidAmount != null && ` · ${won(autoPurchase.data.paidAmount)} 결제`}
                  </div>
                )}
              </div>
            )}

            <div style={{ fontSize: 12.5, color: 'var(--muted)', marginBottom: 6 }}>
              직접 주문했다면 아래에 결과를 기록하세요
            </div>
            {s.supplierOrderNo ? (
              <div className="ok-box">
                발주 완료 · 공급처 주문번호 <strong className="mono">{s.supplierOrderNo}</strong>
              </div>
            ) : (
              <>
                <div className="row" style={{ gap: 10 }}>
                  <input
                    placeholder="공급처 주문번호"
                    value={supplierOrderNo}
                    onChange={(e) => setSupplierOrderNo(e.target.value)}
                    style={{ flex: 2 }}
                  />
                  <input
                    type="number"
                    placeholder="실제 결제액"
                    value={paidAmount}
                    onChange={(e) => setPaidAmount(e.target.value)}
                    style={{ flex: 1 }}
                  />
                  <button onClick={() => markPurchased.mutate()} disabled={markPurchased.isPending}>
                    {markPurchased.isPending ? '기록 중…' : '발주 완료'}
                  </button>
                </div>
                <p className="muted" style={{ fontSize: 12, marginBottom: 0 }}>
                  공급처에서 주문한 뒤 받은 주문번호를 입력하면 마진이 실제 결제액 기준으로 정확해집니다.
                </p>
              </>
            )}
            <ErrorBox error={markPurchased.error} />
          </div>
        </>
      )}
    </div>
  )
}
