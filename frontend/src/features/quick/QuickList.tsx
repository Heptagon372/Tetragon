import { useEffect, useRef, useState } from 'react'
import { useMutation, useQuery } from '@tanstack/react-query'
import { Link } from 'react-router-dom'
import {
  api, subscribePipeline,
  type LinkPreview, type PipelineEvent, type QuickListEntry,
} from '../../shared/api'
import { ErrorBox, Page, shortTime, won } from '../../shared/ui'

/**
 * 빠른 등록 — 도매꾹/도매매 링크 하나를 쿠팡까지 보낸다.
 *
 * 기존 흐름(URL 수집 → 상품 확인 → 등록 버튼)을 한 화면으로 합쳤다.
 * 다만 "확인" 단계는 없애지 않고 미리보기로 남겼다. 잘못된 반품지나 적자 가격으로
 * 올리면 쿠팡에서 내리고 다시 올려야 하기 때문이다.
 */
export default function QuickList() {
  const [urlText, setUrlText] = useState('')
  const [markets, setMarkets] = useState<string[]>(['coupang'])
  const [policyId, setPolicyId] = useState('')
  const [events, setEvents] = useState<PipelineEvent[]>([])
  const feedRef = useRef<HTMLDivElement>(null)

  const policies = useQuery({ queryKey: ['policies'], queryFn: api.policies })
  const plugins = useQuery({ queryKey: ['plugins'], queryFn: api.plugins })

  const urls = urlText.split('\n').map((u) => u.trim()).filter(Boolean)

  const preview = useMutation({
    mutationFn: () => api.previewLink(urls[0], policyId || undefined),
  })

  const send = useMutation({
    mutationFn: () => api.quickList({
      urls,
      marketCodes: markets,
      pricingPolicyId: policyId || undefined,
    }),
  })

  useEffect(() => {
    const unsubscribe = subscribePipeline((event) => setEvents((prev) => [...prev.slice(-80), event]))
    return unsubscribe
  }, [])

  useEffect(() => {
    feedRef.current?.scrollTo({ top: feedRef.current.scrollHeight })
  }, [events])

  // API로 바로 등록되는 마켓만 고른다. 옥션·G마켓은 엑셀 업로드라 여기서 처리할 수 없다.
  const registerable = (plugins.data?.marketplaces ?? []).filter((m) => m.isLive)

  return (
    <Page
      title="빠른 등록"
      desc="도매꾹·도매매 상품 링크를 붙여넣으면 정보를 가져와 쿠팡까지 자동으로 등록합니다."
    >
      <div className="grid grid-2">
        <div className="card">
          <h2 className="card-title">상품 링크</h2>

          <div className="field">
            <label>도매꾹 / 도매매 상품 URL (한 줄에 하나씩)</label>
            <textarea
              value={urlText}
              onChange={(e) => setUrlText(e.target.value)}
              placeholder={'https://domeggook.com/64858155\nhttps://domeme.domeggook.com/s/66739069'}
              rows={5}
            />
          </div>

          <div className="field">
            <label>보낼 마켓</label>
            <div className="row" style={{ gap: 14, flexWrap: 'wrap' }}>
              {registerable.map((m) => (
                <label
                  key={m.code}
                  className="row"
                  style={{ gap: 6, fontSize: 13, opacity: m.isAvailable ? 1 : 0.5 }}
                  title={m.isAvailable ? undefined : '설정에서 API 키를 먼저 등록하세요.'}
                >
                  <input
                    type="checkbox"
                    disabled={!m.isAvailable}
                    checked={markets.includes(m.code)}
                    onChange={(e) => setMarkets((prev) =>
                      e.target.checked ? [...prev, m.code] : prev.filter((c) => c !== m.code))}
                  />
                  {m.displayName}
                  {!m.isAvailable && <span className="muted" style={{ fontSize: 11 }}>(키 없음)</span>}
                </label>
              ))}
            </div>
          </div>

          <div className="field">
            <label>가격 정책</label>
            <select value={policyId} onChange={(e) => setPolicyId(e.target.value)}>
              <option value="">기본 정책 사용</option>
              {policies.data?.map((p) => (
                <option key={p.id} value={p.id}>{p.name}{p.isDefault ? ' (기본)' : ''}</option>
              ))}
            </select>
          </div>

          <div className="row-between">
            <span className="muted" style={{ fontSize: 12 }}>
              {urls.length}개 링크 · {markets.length}개 마켓
            </span>
            <div className="row" style={{ gap: 8 }}>
              <button
                className="ghost"
                onClick={() => preview.mutate()}
                disabled={urls.length === 0 || preview.isPending}
              >
                {preview.isPending ? '가져오는 중…' : '미리보기'}
              </button>
              <button
                onClick={() => send.mutate()}
                disabled={urls.length === 0 || markets.length === 0 || send.isPending}
              >
                {send.isPending ? '보내는 중…' : `${urls.length}건 등록`}
              </button>
            </div>
          </div>

          <p className="muted" style={{ fontSize: 12, marginBottom: 0 }}>
            미리보기는 첫 번째 링크만 확인합니다. 저장하지 않으니 몇 번을 눌러도 상품이 늘지 않습니다.
          </p>

          <ErrorBox error={preview.error} />
          <ErrorBox error={send.error} />
        </div>

        <div className="card">
          <h2 className="card-title">진행 상황 (실시간)</h2>
          <div className="live-feed" ref={feedRef}>
            {events.length === 0 && <div className="muted">등록을 시작하면 여기에 단계가 표시됩니다.</div>}
            {events.map((event, index) => (
              <div key={index}>
                <time>{shortTime(event.at)}</time>
                <span className="badge badge-dim" style={{ marginRight: 6 }}>{event.stage}</span>
                {event.message ?? event.state}
              </div>
            ))}
          </div>
        </div>
      </div>

      {send.data && <SendResult entries={send.data.entries} />}
      {preview.data && <PreviewCard preview={preview.data} />}
    </Page>
  )
}

function SendResult({ entries }: { entries: QuickListEntry[] }) {
  const tone: Record<string, string> = {
    Queued: 'badge-ok',
    ExistingProduct: 'badge-dim',
    NeedsAttention: 'badge-warn',
    Rejected: 'badge-danger',
  }
  const label: Record<string, string> = {
    Queued: '수집 시작',
    ExistingProduct: '기존 상품 재사용',
    NeedsAttention: '확인 필요',
    Rejected: '처리 불가',
  }

  return (
    <div className="card">
      <h2 className="card-title">등록 요청 결과</h2>
      <div className="table-wrap">
        <table>
          <thead>
            <tr>
              <th style={{ width: 120 }}>결과</th>
              <th>링크</th>
              <th>설명</th>
            </tr>
          </thead>
          <tbody>
            {entries.map((e) => (
              <tr key={e.url}>
                <td><span className={`badge ${tone[e.outcome]}`}>{label[e.outcome]}</span></td>
                <td className="mono" style={{ fontSize: 12, maxWidth: 260, overflow: 'hidden', textOverflow: 'ellipsis' }}>
                  {e.productId
                    ? <Link to={`/products/${e.productId}`}>{e.url}</Link>
                    : e.url}
                </td>
                <td className="muted" style={{ fontSize: 12.5 }}>{e.message}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      <p className="muted" style={{ fontSize: 12, marginBottom: 0 }}>
        수집이 끝나면 자동으로 마켓에 등록됩니다. 결과는 <Link to="/listings">등록 현황</Link>에서 확인하세요.
      </p>
    </div>
  )
}

function PreviewCard({ preview }: { preview: LinkPreview }) {
  const l = preview.logistics
  const marginTone = preview.margin?.level === 'Loss' ? 'var(--danger)'
    : preview.margin?.level === 'Thin' ? 'var(--warn)'
    : 'var(--ok)'

  return (
    <div className="card">
      <div className="row-between" style={{ marginBottom: 10 }}>
        <h2 className="card-title" style={{ margin: 0 }}>쿠팡에 등록될 내용</h2>
        <span className="muted" style={{ fontSize: 12 }}>
          {preview.supplierDisplayName} · {preview.sourceProductId}
        </span>
      </div>

      {preview.warnings.length > 0 && (
        <div className="warn-box" style={{ marginBottom: 12, fontSize: 12.5 }}>
          {preview.warnings.map((w, i) => <div key={i}>· {w}</div>)}
        </div>
      )}

      <div className="row" style={{ gap: 16, alignItems: 'flex-start' }}>
        {preview.imageUrls[0] && (
          <img
            src={preview.imageUrls[0]}
            alt=""
            style={{ width: 132, height: 132, objectFit: 'cover', borderRadius: 8, flexShrink: 0 }}
          />
        )}
        <div style={{ flex: 1, minWidth: 0 }}>
          <div style={{ fontSize: 14, fontWeight: 600, marginBottom: 8 }}>{preview.name}</div>

          <div className="row" style={{ gap: 20, fontSize: 13, marginBottom: 10, flexWrap: 'wrap' }}>
            <span><span className="muted">공급가 </span>{won(preview.sourceCost.amount)}</span>
            <span><span className="muted">판매가 </span><strong>{won(preview.salePrice.amount)}</strong></span>
            {preview.margin && (
              <span>
                <span className="muted">순이익 </span>
                <strong style={{ color: marginTone }}>
                  {won(preview.margin.profit)} ({preview.margin.marginPct.toFixed(1)}%)
                </strong>
              </span>
            )}
            <span className="muted">{preview.policyName} · 수수료 {preview.marketFeePct}%</span>
          </div>

          <div className="row" style={{ gap: 20, fontSize: 12.5, flexWrap: 'wrap' }}>
            <span><span className="muted">옵션 </span>{preview.variantCount}개</span>
            <span><span className="muted">재고 </span>{preview.totalStock.toLocaleString()}</span>
            <span><span className="muted">이미지 </span>{preview.imageUrls.length}장</span>
            <span><span className="muted">출고 </span>{l.outboundShippingDays}일</span>
          </div>
        </div>
      </div>

      {preview.variants.length > 0 && <VariantTable preview={preview} />}

      <div className="grid grid-2" style={{ marginTop: 16, gap: 16 }}>
        <Facts title="배송지 (공급처 주소)" rows={[
          ['공급사', l.supplierName],
          ['출고지', l.outboundAddress],
          ['출고지 우편번호', l.outboundZipcode],
          ['반품지', l.returnAddress],
          ['반품지 우편번호', l.returnZipcode],
          ['반품 연락처', l.returnPhone ?? l.supplierPhone],
          ['반품배송비', l.returnFee != null ? won(l.returnFee) : undefined],
        ]} note={l.outboundUsesReturnAddress
          ? '공급처가 출고지 우편번호를 주지 않아 반품지 주소를 출고지로 씁니다 — 주소와 우편번호가 어긋나지 않게 하기 위함입니다.'
          : undefined} />

        <Facts title="상품 정보" rows={[
          ['검색어', preview.searchKeywords],
          ['최소구매수량', l.minOrderQty ? `${l.minOrderQty}개` : undefined],
          ['평균출고일', l.averageOutboundDays != null ? `${l.averageOutboundDays}일` : undefined],
          ['상세이미지', l.detailImagesAllowed ? '사용 허용' : '사용 불가'],
          ['옵션', preview.optionGroups.map((g) => `${g.name}(${g.values.length})`).join(', ') || undefined],
        ]} />
      </div>
    </div>
  )
}

/**
 * 옵션 조합 표.
 *
 * 도매 상품은 옵션마다 공급가와 재고가 다르다. "빨강 / 8(250)"은 재고 245개인데
 * "블랙 / XL"은 2,000원 더 비싸고 품절인 식이다. 등록 전에 그걸 봐야
 * 적자 옵션이나 품절 옵션을 걸러낼 수 있다.
 */
function VariantTable({ preview }: { preview: LinkPreview }) {
  const [expanded, setExpanded] = useState(false)
  const groups = preview.optionGroups.map((g) => g.name)
  const rows = expanded ? preview.variants : preview.variants.slice(0, 12)
  const soldOut = preview.variants.filter((v) => v.stock === 0).length

  return (
    <div style={{ marginTop: 16 }}>
      <div className="row-between" style={{ marginBottom: 6 }}>
        <h3 style={{ fontSize: 13, margin: 0 }}>
          옵션 조합 {preview.variants.length}개
          {soldOut > 0 && <span style={{ color: 'var(--warn)' }}> · 품절 {soldOut}개</span>}
        </h3>
        {preview.variants.length > 12 && (
          <button className="ghost sm" onClick={() => setExpanded((v) => !v)}>
            {expanded ? '접기' : `전체 ${preview.variants.length}개 보기`}
          </button>
        )}
      </div>

      <div className="table-wrap" style={{ maxHeight: expanded ? 420 : undefined }}>
        <table>
          <thead>
            <tr>
              {groups.map((g) => <th key={g}>{g}</th>)}
              <th style={{ textAlign: 'right' }}>공급가</th>
              <th style={{ textAlign: 'right' }}>판매가</th>
              <th style={{ textAlign: 'right' }}>재고</th>
            </tr>
          </thead>
          <tbody>
            {rows.map((v) => (
              <tr key={v.variantId} style={{ opacity: v.stock === 0 ? 0.5 : 1 }}>
                {groups.map((g) => (
                  <td key={g} style={{ fontSize: 12.5 }}>{v.options[g] ?? '—'}</td>
                ))}
                <td style={{ textAlign: 'right', fontSize: 12.5 }}>{won(v.sourceCost.amount)}</td>
                <td style={{ textAlign: 'right', fontSize: 12.5 }}>
                  {v.salePrice ? <strong>{won(v.salePrice.amount)}</strong> : '—'}
                </td>
                <td style={{
                  textAlign: 'right', fontSize: 12.5,
                  color: v.stock === 0 ? 'var(--danger)' : undefined,
                }}>
                  {v.stock === 0 ? '품절' : v.stock.toLocaleString()}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      {!expanded && preview.variants.length > rows.length && (
        <p className="muted" style={{ fontSize: 11.5, marginBottom: 0 }}>
          싼 순서로 {rows.length}개만 표시했습니다.
        </p>
      )}
    </div>
  )
}

function Facts({
  title, rows, note,
}: {
  title: string
  rows: [string, string | undefined | null][]
  note?: string
}) {
  return (
    <div>
      <h3 style={{ fontSize: 13, margin: '0 0 8px' }}>{title}</h3>
      <table style={{ fontSize: 12.5, width: '100%' }}>
        <tbody>
          {rows.map(([key, value]) => (
            <tr key={key}>
              <td className="muted" style={{ width: 108, verticalAlign: 'top', padding: '3px 0' }}>{key}</td>
              <td style={{ padding: '3px 0' }}>{value || <span className="muted">—</span>}</td>
            </tr>
          ))}
        </tbody>
      </table>
      {note && <p className="muted" style={{ fontSize: 11.5, marginBottom: 0 }}>{note}</p>}
    </div>
  )
}
