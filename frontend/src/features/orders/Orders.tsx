import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api } from '../../shared/api'
import { Empty, ErrorBox, Page, shortDate, won } from '../../shared/ui'
import PurchaseSheet from './PurchaseSheet'

const STATUS_LABELS: Record<string, string> = {
  Imported: '수집됨',
  SupplierOrdered: '발주완료',
  AtForwarder: '배대지입고',
  Shipped: '배송중',
  Delivered: '배송완료',
  Cancelled: '취소',
}

export default function Orders() {
  const queryClient = useQueryClient()
  const [trackingInput, setTrackingInput] = useState<Record<string, string>>({})
  // 위탁판매: 발주하려면 공급처·배송지를 봐야 하므로 발주서를 펼친다
  const [sheetOrderId, setSheetOrderId] = useState<string | null>(null)

  const orders = useQuery({ queryKey: ['orders'], queryFn: () => api.orders() })

  const fetchOrders = useMutation({
    mutationFn: () => api.fetchOrders(),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['orders'] }),
  })

  const tracking = useMutation({
    mutationFn: ({ id, no }: { id: string; no: string }) => api.setTracking(id, no),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['orders'] }),
  })

  const items = orders.data?.items ?? []
  const revenue = items.reduce((sum, o) => sum + o.paidAmount, 0)

  return (
    <Page
      title="주문"
      desc={`총 ${items.length}건 · 매출 ${won(revenue)}`}
      actions={
        <button className="sm" onClick={() => fetchOrders.mutate()} disabled={fetchOrders.isPending}>
          {fetchOrders.isPending ? '수집 중…' : '마켓 주문 수집'}
        </button>
      }
    >
      <ErrorBox error={fetchOrders.error} />
      {fetchOrders.data && (
        <div className="ok-box">
          {fetchOrders.data.imported}건 수집됨
          {fetchOrders.data.errors.length > 0 && ` · 오류 ${fetchOrders.data.errors.length}건 (자격증명 확인 필요)`}
        </div>
      )}

      {sheetOrderId && (
        <PurchaseSheet orderId={sheetOrderId} onClose={() => setSheetOrderId(null)} />
      )}

      <div className="card">
        <ErrorBox error={orders.error} />
        {items.length === 0 && <Empty>주문이 없습니다. 마켓 주문 수집을 실행하세요.</Empty>}

        {items.length > 0 && (
          <div className="table-wrap">
            <table>
              <thead>
                <tr>
                  <th>마켓</th>
                  <th>주문번호</th>
                  <th>상품</th>
                  <th style={{ textAlign: 'right' }}>수량</th>
                  <th style={{ textAlign: 'right' }}>결제금액</th>
                  <th>주문자</th>
                  <th>상태</th>
                  <th>주문일</th>
                  <th style={{ width: 220 }}>처리</th>
                </tr>
              </thead>
              <tbody>
                {items.map((order) => (
                  <tr key={order.id}>
                    <td>{order.marketCode}</td>
                    <td className="mono">{order.marketOrderId}</td>
                    <td style={{ maxWidth: 240, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                      {order.productName ?? '—'}
                      {order.optionName && <div className="muted" style={{ fontSize: 11.5 }}>{order.optionName}</div>}
                    </td>
                    <td style={{ textAlign: 'right' }}>{order.quantity}</td>
                    <td style={{ textAlign: 'right', fontWeight: 600 }}>{won(order.paidAmount)}</td>
                    <td className="muted">{order.ordererName ?? '—'}</td>
                    <td>
                      <span className="badge badge-info">{STATUS_LABELS[order.status] ?? order.status}</span>
                    </td>
                    <td className="muted" style={{ fontSize: 12 }}>{shortDate(order.orderedAt)}</td>
                    <td>
                      {order.status === 'Imported' && (
                        <button
                          className="ghost sm"
                          onClick={() => setSheetOrderId(sheetOrderId === order.id ? null : order.id)}
                        >
                          {sheetOrderId === order.id ? '발주서 닫기' : '발주서 열기'}
                        </button>
                      )}
                      {(order.status === 'SupplierOrdered' || order.status === 'AtForwarder') && (
                        <div className="row" style={{ gap: 4 }}>
                          <input
                            placeholder="송장번호"
                            style={{ width: 110, padding: '4px 7px', fontSize: 12 }}
                            value={trackingInput[order.id] ?? ''}
                            onChange={(e) => setTrackingInput({ ...trackingInput, [order.id]: e.target.value })}
                          />
                          <button
                            className="ghost sm"
                            onClick={() => tracking.mutate({ id: order.id, no: trackingInput[order.id] ?? '' })}
                            disabled={!trackingInput[order.id]}
                          >
                            등록
                          </button>
                        </div>
                      )}
                      {order.trackingNo && <span className="mono muted">{order.trackingNo}</span>}
                    </td>
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
