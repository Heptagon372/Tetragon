import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { api } from '../../shared/api'
import { Empty, ErrorBox } from '../../shared/ui'

/**
 * 공급처별 배송지 등록 현황.
 *
 * 출고지는 API로 자동 생성되지만, 반품지는 쿠팡이 API 생성을 막아 두어
 * WING에 사람이 한 번 등록해야 한다. 상품 등록에 실패해야 그걸 알게 되면
 * 공급처가 늘 때마다 같은 일을 반복하게 되므로, 여기서 한 번에 보여준다.
 */
export default function ShippingPlaces() {
  const [copied, setCopied] = useState<string | null>(null)

  const audit = useQuery({
    queryKey: ['shippingPlaces', 'coupang'],
    queryFn: () => api.shippingPlaces('coupang'),
    // 배송지 확인은 공급처 수만큼 쿠팡 API를 호출해 느리다 — 수동 새로고침만 한다
    refetchOnWindowFocus: false,
    staleTime: 5 * 60 * 1000,
  })

  const copy = async (text: string, key: string) => {
    await navigator.clipboard.writeText(text)
    setCopied(key)
    setTimeout(() => setCopied(null), 1500)
  }

  const data = audit.data
  const missing = data?.suppliers.filter((s) => !s.returnReady) ?? []
  const ready = data?.suppliers.filter((s) => s.returnReady) ?? []

  return (
    <div className="card">
      <div className="row-between" style={{ marginBottom: 8 }}>
        <h2 className="card-title" style={{ margin: 0 }}>쿠팡 배송지 현황</h2>
        <button className="ghost sm" onClick={() => audit.refetch()} disabled={audit.isFetching}>
          {audit.isFetching ? '확인 중…' : '새로고침'}
        </button>
      </div>

      <p className="muted" style={{ fontSize: 12.5, marginTop: 0 }}>
        <strong>출고지는 자동으로 등록</strong>됩니다. 반품지는 쿠팡이 API 생성을 막아 두어
        (굿스플로 정보 필수 → 넣으면 서버 오류) 직접 등록해야 합니다.
      </p>

      <ErrorBox error={audit.error} />
      {audit.isLoading && <div className="muted" style={{ fontSize: 12.5 }}>공급처별로 확인하는 중… (수 초 걸립니다)</div>}
      {data?.message && <Empty>{data.message}</Empty>}

      {missing.length > 0 && (
        <>
          <div className="warn-box" style={{ marginBottom: 12 }}>
            <div style={{ marginBottom: 6 }}>
              반품지가 연결되지 않은 공급처 <strong>{missing.length}곳</strong>입니다.
            </div>
            <div>
              <strong>기본 반품지를 지정하는 것을 권합니다.</strong> 아래 쿠팡 설정의{' '}
              <span className="mono">return_center_code</span>에 판매자 반품지 코드를 넣으면,
              등록되지 않은 공급처는 그 주소를 쓰고 반품은 판매자가 받아 공급처로 전달합니다.
              공급처 수가 많으면 이 방식이 현실적입니다.
            </div>
            <div className="muted" style={{ marginTop: 6 }}>
              특정 공급처의 반품을 직접 받게 하려면 그 공급처만 WING에 등록하세요 —
              등록된 것은 우편번호+주소로 자동 매칭되어 우선 적용됩니다.
            </div>
          </div>
          <div style={{ marginBottom: 10 }}>
            <a href="https://wing.coupang.com" target="_blank" rel="noreferrer">
              <button className="sm">WING 열기 ↗</button>
            </a>
            <span className="muted" style={{ fontSize: 12, marginLeft: 10 }}>
              판매자정보 → 반품지 관리 → 반품지 추가
            </span>
          </div>
          <div className="table-wrap" style={{ marginBottom: 18 }}>
            <table>
              <thead>
                <tr>
                  <th style={{ width: 110 }}>공급사</th>
                  <th>WING 「새 주소지 등록 → 반품지」 폼에 넣을 값</th>
                  <th style={{ width: 96 }}>전체</th>
                </tr>
              </thead>
              <tbody>
                {missing.map((s) => (
                  <tr key={s.supplierName}>
                    <td style={{ fontWeight: 600 }}>{s.supplierName}</td>
                    <td style={{ fontSize: 12.5 }}>
                      <FormField label="주소지명" value={s.formPlaceName}
                        onCopy={copy} copied={copied} keyPrefix={s.supplierName} />
                      <FormField label="우편번호" value={s.returnZipcode ?? ''} mono
                        onCopy={copy} copied={copied} keyPrefix={s.supplierName} />
                      <FormField label="주소" value={s.formAddress}
                        onCopy={copy} copied={copied} keyPrefix={s.supplierName} />
                      {s.formAddressDetail && (
                        <FormField label="상세주소" value={s.formAddressDetail}
                          onCopy={copy} copied={copied} keyPrefix={s.supplierName} />
                      )}
                      <FormField label="전화번호" value={s.formPhone} mono
                        onCopy={copy} copied={copied} keyPrefix={s.supplierName} />
                    </td>
                    <td>
                      <button
                        className="ghost sm"
                        onClick={() => copy(s.registerLine, s.supplierName)}
                      >
                        {copied === s.supplierName ? '복사됨 ✓' : '한 줄 복사'}
                      </button>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </>
      )}

      {ready.length > 0 && (
        <>
          <div className="ok-box" style={{ marginBottom: 10 }}>
            반품지가 연결된 공급처 <strong>{ready.length}곳</strong> — 바로 등록할 수 있습니다.
          </div>
          <div className="table-wrap">
            <table>
              <thead>
                <tr>
                  <th style={{ width: 120 }}>공급사</th>
                  <th>반품지</th>
                  <th style={{ width: 110 }}>반품지코드</th>
                  <th style={{ width: 100 }}>출고지코드</th>
                </tr>
              </thead>
              <tbody>
                {ready.map((s) => (
                  <tr key={s.supplierName}>
                    <td style={{ fontWeight: 600 }}>{s.supplierName}</td>
                    <td style={{ fontSize: 12.5 }}>
                      {s.returnZipcode && <span className="mono">({s.returnZipcode}) </span>}
                      {s.returnAddress}
                    </td>
                    <td className="mono" style={{ fontSize: 12 }}>{s.returnCenterCode}</td>
                    <td className="mono muted" style={{ fontSize: 12 }}>
                      {s.outboundCode ?? '—'}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </>
      )}
    </div>
  )
}

/** WING 폼 한 칸에 대응하는 값 + 개별 복사 버튼. */
function FormField({
  label, value, mono, onCopy, copied, keyPrefix,
}: {
  label: string
  value: string
  mono?: boolean
  onCopy: (text: string, key: string) => void
  copied: string | null
  keyPrefix: string
}) {
  const key = `${keyPrefix}:${label}`
  return (
    <div style={{ display: 'flex', gap: 6, alignItems: 'baseline', padding: '1px 0' }}>
      <span className="muted" style={{ fontSize: 11, width: 52, flexShrink: 0 }}>{label}</span>
      <span className={mono ? 'mono' : undefined} style={{ flex: 1 }}>{value || '—'}</span>
      <button
        className="ghost sm"
        style={{ padding: '0 6px', fontSize: 11 }}
        onClick={() => onCopy(value, key)}
        disabled={!value}
      >
        {copied === key ? '✓' : '복사'}
      </button>
    </div>
  )
}
