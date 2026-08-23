import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Link } from 'react-router-dom'
import { api } from '../../shared/api'
import { Empty, ErrorBox, Page, StatusBadge, shortDate, won } from '../../shared/ui'

export default function Listings() {
  const [market, setMarket] = useState('')
  const [status, setStatus] = useState('')

  const listings = useQuery({
    queryKey: ['listings', market, status],
    queryFn: () => api.listings({ market, status }),
    refetchInterval: 5000,
  })
  const plugins = useQuery({ queryKey: ['plugins'], queryFn: api.plugins })

  const items = listings.data?.items ?? []

  return (
    <Page title="등록 현황" desc={`마켓별 등록 상태 · 총 ${items.length}건`}>
      <div className="card">
        <div className="row" style={{ marginBottom: 14 }}>
          <select value={market} onChange={(e) => setMarket(e.target.value)} style={{ width: 170 }}>
            <option value="">전체 마켓</option>
            {plugins.data?.marketplaces.map((m) => (
              <option key={m.code} value={m.code}>{m.displayName}</option>
            ))}
          </select>
          <select value={status} onChange={(e) => setStatus(e.target.value)} style={{ width: 140 }}>
            <option value="">전체 상태</option>
            <option value="Registered">등록됨</option>
            <option value="Failed">실패</option>
            <option value="Pending">대기</option>
            <option value="Suspended">판매중지</option>
          </select>
        </div>

        <ErrorBox error={listings.error} />
        {items.length === 0 && <Empty>등록 내역이 없습니다.</Empty>}

        {items.length > 0 && (
          <div className="table-wrap">
            <table>
              <thead>
                <tr>
                  <th>마켓</th>
                  <th>상태</th>
                  <th>마켓 상품번호</th>
                  <th style={{ textAlign: 'right' }}>등록가</th>
                  <th>최근 동작</th>
                  <th>갱신</th>
                  <th />
                </tr>
              </thead>
              <tbody>
                {items.map((listing) => {
                  const lastLog = listing.syncLogs[listing.syncLogs.length - 1]
                  return (
                    <tr key={listing.id}>
                      <td style={{ fontWeight: 500 }}>{listing.marketCode}</td>
                      <td><StatusBadge status={listing.status} /></td>
                      <td className="mono">{listing.marketItemId ?? '—'}</td>
                      <td style={{ textAlign: 'right', fontWeight: 600 }}>{won(listing.listedPrice)}</td>
                      <td className="muted" style={{ fontSize: 12, maxWidth: 320 }}>
                        {listing.lastError ?? lastLog?.message ?? '—'}
                      </td>
                      <td className="muted" style={{ fontSize: 12 }}>{shortDate(listing.updatedAt)}</td>
                      <td>
                        <Link to={`/products/${listing.productId}`}>
                          <button className="ghost sm">상품</button>
                        </Link>
                      </td>
                    </tr>
                  )
                })}
              </tbody>
            </table>
          </div>
        )}
      </div>
    </Page>
  )
}
