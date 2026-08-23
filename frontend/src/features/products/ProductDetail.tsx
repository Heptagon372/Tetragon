import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Link, useParams } from 'react-router-dom'
import { api, type ProductLogistics } from '../../shared/api'
import { Empty, ErrorBox, Page, StatusBadge, shortDate, won } from '../../shared/ui'

export default function ProductDetail() {
  const { id = '' } = useParams()
  const queryClient = useQueryClient()
  const [editing, setEditing] = useState(false)
  const [name, setName] = useState('')

  const product = useQuery({ queryKey: ['product', id], queryFn: () => api.product(id) })

  const save = useMutation({
    mutationFn: () => api.editProduct(id, { name }),
    onSuccess: () => {
      setEditing(false)
      queryClient.invalidateQueries({ queryKey: ['product', id] })
    },
  })

  const resume = useMutation({
    mutationFn: () => api.resumeProduct(id),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['product', id] }),
  })

  if (product.isLoading) return <Empty>불러오는 중…</Empty>
  if (product.error) return <ErrorBox error={product.error} />
  const p = product.data
  if (!p) return <Empty>상품을 찾을 수 없습니다.</Empty>

  const calc = p.priceCalculations[0]

  return (
    <Page
      title={p.name}
      desc={`${p.source.supplierCode} · ${p.source.sourceProductId}`}
      actions={
        <>
          <Link to="/products"><button className="ghost sm">목록</button></Link>
          <a href={p.source.url} target="_blank" rel="noreferrer">
            <button className="ghost sm">원본 페이지</button>
          </a>
          {p.status === 'Blocked' && (
            <button className="sm" onClick={() => resume.mutate()} disabled={resume.isPending}>
              차단 해제하고 등록 대기로
            </button>
          )}
        </>
      }
    >
      <ErrorBox error={resume.error} />

      <div className="grid grid-2">
        <div className="card">
          <div className="row-between" style={{ marginBottom: 12 }}>
            <h2 className="card-title" style={{ margin: 0 }}>기본 정보</h2>
            <StatusBadge status={p.status} />
          </div>

          {editing ? (
            <div className="field">
              <label>상품명 (한국어)</label>
              <input value={name} onChange={(e) => setName(e.target.value)} />
              <div className="row" style={{ marginTop: 8 }}>
                <button className="sm" onClick={() => save.mutate()} disabled={save.isPending}>저장</button>
                <button className="ghost sm" onClick={() => setEditing(false)}>취소</button>
              </div>
            </div>
          ) : (
            <div className="field">
              <label>상품명 (한국어) <button className="ghost sm" style={{ marginLeft: 6, padding: '1px 7px' }}
                onClick={() => { setName(p.name); setEditing(true) }}>편집</button></label>
              <div>{p.name}</div>
            </div>
          )}

          {Object.entries(p.names).filter(([locale]) => locale !== 'ko-KR').map(([locale, value]) => (
            <div className="field" key={locale}>
              <label>원문 ({locale})</label>
              <div className="muted">{value}</div>
            </div>
          ))}

          <div className="row" style={{ gap: 24 }}>
            <div>
              <label>원가</label>
              <div className="mono">{p.basePrice.amount.toLocaleString()} {p.basePrice.currency}</div>
            </div>
            <div>
              <label>계산 판매가</label>
              <div style={{ fontWeight: 650, color: 'var(--accent)' }}>{won(p.salePrice)}</div>
            </div>
            <div>
              <label>총 재고</label>
              <div>{p.totalStock.toLocaleString()}</div>
            </div>
          </div>

          {p.images.length > 0 && (
            <>
              <label style={{ marginTop: 14 }}>이미지 ({p.images.length})</label>
              <div className="row" style={{ gap: 6 }}>
                {p.images.slice(0, 8).map((url) => (
                  <img key={url} src={url} className="thumb" style={{ width: 56, height: 56 }} alt="" loading="lazy" />
                ))}
              </div>
            </>
          )}
        </div>

        <ConsignmentCard logistics={p.logistics} />

        <div className="card">
          <h2 className="card-title">가격 계산 추적</h2>
          {!calc && <Empty>가격이 아직 계산되지 않았습니다.</Empty>}
          {calc && (
            <>
              <div className="muted" style={{ fontSize: 12, marginBottom: 10 }}>
                원가 {calc.sourceCost.amount.toLocaleString()} {calc.sourceCost.currency}
                {calc.exchangeRate !== 1 && ` · 환율 ${calc.exchangeRate.toFixed(2)}`}
                {' · '}{shortDate(calc.calculatedAt)}
              </div>
              <table className="price-steps">
                <tbody>
                  {calc.steps.map((step, index) => (
                    <tr key={index}>
                      <td style={{ width: '45%' }}>{step.description}</td>
                      <td style={{ textAlign: 'right' }} className="mono">
                        {Math.round(step.before).toLocaleString()}
                      </td>
                      <td style={{ textAlign: 'center', width: 24 }} className="muted">→</td>
                      <td style={{ textAlign: 'right' }} className="mono">
                        {Math.round(step.after).toLocaleString()}
                      </td>
                    </tr>
                  ))}
                  <tr>
                    <td>최종 판매가</td>
                    <td colSpan={3} style={{ textAlign: 'right' }}>{won(calc.finalPrice)}</td>
                  </tr>
                </tbody>
              </table>
            </>
          )}
        </div>
      </div>

      <DetailImagesCard productId={p.id} logistics={p.logistics} />

      {p.compliance && (
        <div className="card">
          <div className="row-between" style={{ marginBottom: 10 }}>
            <h2 className="card-title" style={{ margin: 0 }}>컴플라이언스 검사</h2>
            <StatusBadge status={p.compliance.verdict} />
          </div>
          {p.compliance.hits.length === 0
            ? <div className="muted" style={{ fontSize: 13 }}>금지어가 발견되지 않았습니다.</div>
            : (
              <table>
                <thead>
                  <tr><th>키워드</th><th>위치</th><th>등급</th><th>사유</th></tr>
                </thead>
                <tbody>
                  {p.compliance.hits.map((hit, index) => (
                    <tr key={index}>
                      <td style={{ fontWeight: 600 }}>{hit.keyword}</td>
                      <td className="muted">{hit.field}</td>
                      <td><StatusBadge status={hit.severity} /></td>
                      <td className="muted">{hit.reason}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            )}
        </div>
      )}

      <div className="card">
        <h2 className="card-title">옵션 · SKU ({p.variants.length})</h2>
        <div className="table-wrap">
          <table>
            <thead>
              <tr>
                <th>SKU</th>
                <th>옵션</th>
                <th style={{ textAlign: 'right' }}>원가</th>
                <th style={{ textAlign: 'right' }}>판매가</th>
                <th style={{ textAlign: 'right' }}>재고</th>
              </tr>
            </thead>
            <tbody>
              {p.variants.map((v) => (
                <tr key={v.id}>
                  <td className="mono muted">{v.sku}</td>
                  <td>
                    {Object.entries(v.options).map(([group, value]) => (
                      <span key={group} className="badge badge-dim" style={{ marginRight: 4 }}>
                        {group}: {value}
                      </span>
                    ))}
                  </td>
                  <td style={{ textAlign: 'right' }} className="mono">
                    {v.sourcePrice.amount.toLocaleString()} {v.sourcePrice.currency}
                  </td>
                  <td style={{ textAlign: 'right', fontWeight: 600 }}>{won(v.calculatedPrice?.amount)}</td>
                  <td style={{ textAlign: 'right' }} className={v.stock === 0 ? 'badge badge-danger' : 'muted'}>
                    {v.stock.toLocaleString()}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </div>

      {p.listings.length > 0 && (
        <div className="card">
          <h2 className="card-title">마켓 등록 현황</h2>
          <table>
            <thead>
              <tr><th>마켓</th><th>상태</th><th>마켓 상품번호</th><th style={{ textAlign: 'right' }}>등록가</th><th>비고</th></tr>
            </thead>
            <tbody>
              {p.listings.map((listing) => (
                <tr key={listing.id}>
                  <td style={{ fontWeight: 500 }}>{listing.marketCode}</td>
                  <td><StatusBadge status={listing.status} /></td>
                  <td className="mono">{listing.marketItemId ?? '—'}</td>
                  <td style={{ textAlign: 'right' }}>{won(listing.listedPrice)}</td>
                  <td className="muted" style={{ fontSize: 12, maxWidth: 300 }}>{listing.lastError ?? ''}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </Page>
  )
}

/**
 * 위탁판매 물류 카드.
 *
 * 출고지·반품지가 판매자가 아니라 공급처 주소라는 점이 위탁판매의 핵심이라,
 * 마켓에 실제로 등록될 값을 상품 화면에서 바로 확인할 수 있게 한다.
 */
function ConsignmentCard({ logistics: l }: { logistics: ProductLogistics }) {
  if (!l.outboundAddress && !l.returnAddress && !l.supplierName) return null

  const won0 = (n?: number) => (n == null ? null : `${n.toLocaleString()}원`)

  return (
    <div className="card">
      <h2 className="card-title">위탁판매 물류</h2>
      <p className="muted" style={{ fontSize: 12, marginTop: 0 }}>
        마켓에 등록될 값입니다. 위탁판매이므로 판매자 주소가 아닌 <strong>공급처 주소</strong>를 사용합니다.
      </p>
      <table className="kv">
        <tbody>
          {l.supplierName && (
            <tr><th>공급사</th><td>
              {l.supplierName}
              {l.supplierPhone && <span className="mono muted" style={{ marginLeft: 8 }}>{l.supplierPhone}</span>}
            </td></tr>
          )}
          <tr><th>출고지</th><td>
            {l.outboundAddress ?? <span className="muted">공급처 미제공 — 판매자 기본값 사용</span>}
          </td></tr>
          <tr><th>반품지</th><td>
            {l.returnAddress ? (
              <>
                {l.returnZipcode && <span className="mono">({l.returnZipcode}) </span>}
                {l.returnAddress}
                {l.returnPhone && <div className="mono muted" style={{ fontSize: 12 }}>{l.returnPhone}</div>}
              </>
            ) : <span className="muted">공급처 미제공 — 판매자 기본값 사용</span>}
          </td></tr>
          <tr><th>출고 소요일</th><td>
            <strong>{l.outboundShippingDays}일</strong>
            {l.averageOutboundDays != null && (
              <span className="muted" style={{ marginLeft: 8, fontSize: 12 }}>
                공급처 평균 {l.averageOutboundDays}일 + 발주 여유 1일 (올림)
              </span>
            )}
          </td></tr>
          {(l.deliveryFee != null || l.returnFee != null) && (
            <tr><th>배송비</th><td>
              {l.deliveryFee != null && <span>기본 {won0(l.deliveryFee)}</span>}
              {l.returnFee != null && <span className="muted"> · 반품 {won0(l.returnFee)}</span>}
              {l.jejuExtraFee != null && <span className="muted"> · 제주 +{won0(l.jejuExtraFee)}</span>}
              {l.islandExtraFee != null && <span className="muted"> · 도서산간 +{won0(l.islandExtraFee)}</span>}
            </td></tr>
          )}
          {l.minOrderQty != null && (
            <tr><th>최소구매</th><td>
              {l.minOrderQty}개
              {l.minOrderQty > 1 && (
                <span className="badge badge-warn" style={{ marginLeft: 6 }}>
                  1개 주문 시 {l.minOrderQty}개 구매 필요
                </span>
              )}
            </td></tr>
          )}
          <tr><th>통관</th><td>
            {l.isOverseasPurchase
              ? <span className="badge badge-info">해외구매대행 (개인통관고유부호 필요)</span>
              : <span className="badge badge-ok">국내 배송 (구매대행 아님)</span>}
          </td></tr>
        </tbody>
      </table>
    </div>
  )
}

/**
 * 공급처 상세설명 이미지.
 *
 * 공급처가 재사용을 허용한 경우에만 가져와 보여주고 마켓에도 그대로 등록한다.
 * 허용하지 않은 이미지를 올리면 저작권 문제가 되므로 여기서 명확히 구분한다.
 */
function DetailImagesCard({ productId, logistics }: { productId: string; logistics: ProductLogistics }) {
  const detail = useQuery({
    queryKey: ['productDetailHtml', productId],
    queryFn: () => api.productDetailHtml(productId),
  })

  return (
    <div className="card">
      <div className="row-between" style={{ marginBottom: 8 }}>
        <h2 className="card-title" style={{ margin: 0 }}>상세설명 이미지</h2>
        {logistics.detailImagesAllowed
          ? <span className="badge badge-ok">사용 허용</span>
          : <span className="badge badge-danger">사용 불가</span>}
      </div>

      {!logistics.detailImagesAllowed && (
        <p className="muted" style={{ fontSize: 12.5, marginTop: 0 }}>
          공급처가 상세설명 이미지 재사용을 허용하지 않았습니다.
          마켓에는 대표 이미지와 상품명으로만 상세페이지를 만듭니다.
        </p>
      )}

      {logistics.detailImagesAllowed && (
        <>
          <p className="muted" style={{ fontSize: 12.5, marginTop: 0 }}>
            공급처가 재사용을 허용했습니다. 아래 이미지가 마켓 상세페이지에 그대로 등록됩니다.
          </p>
          <ErrorBox error={detail.error} />
          {detail.isLoading && <div className="muted" style={{ fontSize: 12.5 }}>불러오는 중…</div>}
          {detail.data && detail.data.images.length === 0 && (
            <Empty>공급처가 상세설명 이미지를 제공하지 않았습니다.</Empty>
          )}
          {detail.data && detail.data.images.length > 0 && (
            <div style={{ display: 'flex', flexWrap: 'wrap', gap: 8, maxHeight: 420, overflowY: 'auto' }}>
              {detail.data.images.map((url) => (
                <a key={url} href={url} target="_blank" rel="noreferrer">
                  <img
                    src={url}
                    alt=""
                    loading="lazy"
                    style={{
                      width: 104, height: 104, objectFit: 'cover',
                      borderRadius: 6, border: '1px solid var(--border)',
                    }}
                  />
                </a>
              ))}
            </div>
          )}
        </>
      )}
    </div>
  )
}
