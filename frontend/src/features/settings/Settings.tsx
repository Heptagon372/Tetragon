import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api } from '../../shared/api'
import { Empty, ErrorBox, Page } from '../../shared/ui'
import ShippingPlaces from './ShippingPlaces'

/** 스코프별 필요한 자격증명 키 정의 (플러그인 문서와 일치). */
const SCOPES: { scope: string; label: string; hint: string; keys: { key: string; label: string; help?: string }[] }[] = [
  {
    scope: 'ai:claude',
    label: 'Claude (번역/SEO)',
    hint: '미설정 시 내장 시뮬레이션 번역기로 자동 폴백됩니다.',
    keys: [{ key: 'api_key', label: 'API Key', help: 'console.anthropic.com에서 발급' }],
  },
  {
    scope: 'supplier:taobao',
    label: '타오바오/티몰',
    hint: '로그인 상태의 브라우저 쿠키가 필요합니다 (개발자도구 → Network → 요청 헤더 Cookie 전체 복사).',
    keys: [{ key: 'cookie', label: 'Cookie 헤더', help: '_m_h5_tk 토큰이 포함되어야 합니다' }],
  },
  {
    scope: 'supplier:aliexpress',
    label: 'AliExpress',
    hint: 'AliExpress 상세 페이지는 클라이언트 렌더링이라 HTML에 가격이 없습니다. '
      + '가격·옵션까지 수집하려면 Open Platform(오픈 플랫폼) 자격증명이 필요합니다.',
    keys: [
      { key: 'app_key', label: 'App Key' },
      { key: 'app_secret', label: 'App Secret' },
      { key: 'access_token', label: 'Access Token' },
      { key: 'currency', label: '수집 통화 (기본 USD)' },
    ],
  },
  {
    scope: 'supplier:domeggook',
    label: '도매꾹 / 도매매 (국내 도매)',
    hint: 'openapi.domeggook.com에서 API 키를 발급받으세요. 카테고리 단위 대량 수집을 지원하는 유일한 공급처입니다.',
    keys: [
      { key: 'api_key', label: 'API Key', help: 'openapi.domeggook.com → API 키 발급' },
      { key: 'market', label: '대상 마켓', help: 'dome=도매꾹, supply=도매매 (기본 dome)' },
    ],
  },
  {
    scope: 'market:smartstore',
    label: '네이버 스마트스토어',
    hint: '커머스API센터에서 애플리케이션을 등록하고 발급받은 값입니다.',
    keys: [
      { key: 'client_id', label: 'Client ID' },
      { key: 'client_secret', label: 'Client Secret' },
      { key: 'default_category_id', label: '기본 카테고리 ID', help: '예: 50000803' },
      { key: 'as_telephone', label: 'A/S 전화번호' },
    ],
  },
  {
    scope: 'market:coupang',
    label: '쿠팡 WING',
    hint: 'WING → 판매자정보 → 오픈API 키 발급에서 확인할 수 있습니다.',
    keys: [
      { key: 'access_key', label: 'Access Key' },
      { key: 'secret_key', label: 'Secret Key' },
      { key: 'vendor_id', label: 'Vendor ID', help: '예: A00123456' },
      { key: 'default_category_code', label: '기본 노출카테고리 코드' },
      { key: 'outbound_shipping_place_code', label: '출고지 코드' },
      { key: 'return_center_code', label: '반품지 코드' },
      { key: 'return_zip_code', label: '반품지 우편번호' },
      { key: 'return_address', label: '반품지 주소' },
      { key: 'contact_number', label: '연락처' },
    ],
  },
  {
    scope: 'market:11st',
    label: '11번가 셀러오피스',
    hint: '셀러오피스 → API 관리에서 오픈API 키를 발급받으세요. 발송지/반품지는 셀러오피스에 먼저 등록한 뒤 순번을 입력합니다.',
    keys: [
      { key: 'api_key', label: 'API Key (openapikey)' },
      { key: 'default_category_code', label: '기본 전시카테고리번호', help: 'dispCtgrNo' },
      { key: 'outbound_address_seq', label: '발송지 주소순번' },
      { key: 'return_address_seq', label: '반품지 주소순번' },
      { key: 'delivery_company_code', label: '택배사 코드', help: '기본 00034' },
    ],
  },
  {
    scope: 'market:esmplus',
    label: 'ESM Plus (옥션 + G마켓) — 엑셀 전용',
    hint: '옥션·G마켓은 일반 판매자용 등록 API가 없어 대량등록 엑셀로만 등록합니다. '
      + '아래 값은 엑셀 생성 시 각 열에 채워집니다. (상품 화면 → 엑셀 내보내기 → ESM Plus)',
    keys: [
      { key: 'default_category_code', label: '기본 카테고리코드' },
      { key: 'site_type', label: '등록 사이트', help: 'A=옥션, G=G마켓, AG=동시 (기본 AG)' },
      { key: 'outbound_address', label: '발송지 주소' },
      { key: 'return_address', label: '반품지 주소' },
      { key: 'delivery_fee', label: '기본 배송비' },
      { key: 'return_fee', label: '반품 배송비' },
      { key: 'origin', label: '원산지', help: '기본 중국' },
    ],
  },
]

export default function Settings() {
  const queryClient = useQueryClient()
  const [drafts, setDrafts] = useState<Record<string, Record<string, string>>>({})

  const credentials = useQuery({ queryKey: ['credentials'], queryFn: api.credentials })
  const plugins = useQuery({ queryKey: ['plugins'], queryFn: api.plugins })
  const complianceRules = useQuery({ queryKey: ['complianceRules'], queryFn: api.complianceRules })

  const [newKeyword, setNewKeyword] = useState('')
  const [newSeverity, setNewSeverity] = useState('Block')

  const save = useMutation({
    mutationFn: ({ scope, secrets }: { scope: string; secrets: Record<string, string> }) =>
      api.saveCredentials(scope, secrets),
    onSuccess: (_, variables) => {
      setDrafts({ ...drafts, [variables.scope]: {} })
      queryClient.invalidateQueries({ queryKey: ['credentials'] })
      queryClient.invalidateQueries({ queryKey: ['plugins'] })
    },
  })

  const addRule = useMutation({
    mutationFn: () => api.addComplianceRule({ keyword: newKeyword, severity: newSeverity }),
    onSuccess: () => {
      setNewKeyword('')
      queryClient.invalidateQueries({ queryKey: ['complianceRules'] })
    },
  })

  const deleteRule = useMutation({
    mutationFn: (id: string) => api.deleteComplianceRule(id),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['complianceRules'] }),
  })

  const allPlugins = [
    ...(plugins.data?.suppliers ?? []).map((p) => ({ ...p, kind: '공급처' })),
    ...(plugins.data?.marketplaces ?? []).map((p) => ({ ...p, kind: '마켓' })),
    ...(plugins.data?.aiProviders ?? []).map((p) => ({ ...p, kind: 'AI' })),
  ]

  return (
    <Page title="설정" desc="API 자격증명과 금지어 규칙을 관리합니다. 값은 저장 후 화면에 다시 표시되지 않습니다.">
      <div className="card">
        <h2 className="card-title">플러그인 상태</h2>
        <div className="table-wrap">
          <table>
            <thead>
              <tr><th>구분</th><th>플러그인</th><th>코드</th><th>버전</th><th>연동</th><th>사용 가능</th></tr>
            </thead>
            <tbody>
              {allPlugins.map((p) => (
                <tr key={`${p.kind}-${p.code}`}>
                  <td className="muted">{p.kind}</td>
                  <td style={{ fontWeight: 500 }}>{p.displayName}</td>
                  <td className="mono muted">{p.code}</td>
                  <td className="muted">{p.version}</td>
                  <td>
                    <span className={`badge ${p.isLive ? 'badge-info' : 'badge-dim'}`}>
                      {p.isLive ? '실연동' : '시뮬레이션'}
                    </span>
                  </td>
                  <td>
                    <span className={`badge ${p.isAvailable ? 'badge-ok' : 'badge-warn'}`}>
                      {p.isAvailable ? '준비됨' : '자격증명 필요'}
                    </span>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </div>

      <ErrorBox error={save.error} />

      {SCOPES.map((scopeDef) => {
        const registered = credentials.data?.[scopeDef.scope] ?? []
        const draft = drafts[scopeDef.scope] ?? {}
        return (
          <div className="card" key={scopeDef.scope}>
            <div className="row-between" style={{ marginBottom: 4 }}>
              <h2 className="card-title" style={{ margin: 0 }}>{scopeDef.label}</h2>
              <span className="mono muted">{scopeDef.scope}</span>
            </div>
            <p className="muted" style={{ fontSize: 12, margin: '0 0 12px' }}>{scopeDef.hint}</p>

            <div className="grid" style={{ gridTemplateColumns: 'repeat(auto-fit, minmax(230px, 1fr))' }}>
              {scopeDef.keys.map((keyDef) => (
                <div key={keyDef.key}>
                  <label>
                    {keyDef.label}
                    {registered.includes(keyDef.key) && (
                      <span className="badge badge-ok" style={{ marginLeft: 6 }}>등록됨</span>
                    )}
                  </label>
                  <input
                    type="password"
                    autoComplete="off"
                    placeholder={registered.includes(keyDef.key) ? '••••••  (변경 시에만 입력)' : keyDef.help ?? ''}
                    value={draft[keyDef.key] ?? ''}
                    onChange={(e) =>
                      setDrafts({
                        ...drafts,
                        [scopeDef.scope]: { ...draft, [keyDef.key]: e.target.value },
                      })
                    }
                  />
                  {keyDef.help && <div className="muted" style={{ fontSize: 11, marginTop: 3 }}>{keyDef.help}</div>}
                </div>
              ))}
            </div>

            <div style={{ marginTop: 12 }}>
              <button
                className="sm"
                onClick={() => save.mutate({ scope: scopeDef.scope, secrets: draft })}
                disabled={Object.values(draft).every((v) => !v) || save.isPending}
              >
                저장
              </button>
            </div>
          </div>
        )
      })}

      {/* 배송지 관리는 자격증명 스코프와 무관한 별도 카드다 (스코프마다 반복 렌더되면 안 된다) */}
      <ShippingPlaces />

      <div className="card">
        <h2 className="card-title">금지어 / 컴플라이언스 규칙 ({complianceRules.data?.length ?? 0})</h2>
        <div className="row" style={{ marginBottom: 14 }}>
          <input
            placeholder="금지어 입력"
            value={newKeyword}
            onChange={(e) => setNewKeyword(e.target.value)}
            style={{ width: 200 }}
          />
          <select value={newSeverity} onChange={(e) => setNewSeverity(e.target.value)} style={{ width: 110 }}>
            <option value="Block">차단</option>
            <option value="Warn">경고</option>
          </select>
          <button className="sm" onClick={() => addRule.mutate()} disabled={!newKeyword || addRule.isPending}>
            추가
          </button>
        </div>

        {complianceRules.data?.length === 0 && <Empty>등록된 금지어가 없습니다.</Empty>}
        {complianceRules.data && complianceRules.data.length > 0 && (
          <div className="table-wrap">
            <table>
              <thead>
                <tr><th>키워드</th><th>등급</th><th>사유</th><th style={{ width: 60 }} /></tr>
              </thead>
              <tbody>
                {complianceRules.data.map((rule) => (
                  <tr key={rule.id}>
                    <td style={{ fontWeight: 500 }}>{rule.keyword}</td>
                    <td>
                      <span className={`badge ${rule.severity === 'Block' ? 'badge-danger' : 'badge-warn'}`}>
                        {rule.severity === 'Block' ? '차단' : '경고'}
                      </span>
                    </td>
                    <td className="muted" style={{ fontSize: 12 }}>{rule.reason ?? '—'}</td>
                    <td>
                      <button className="danger sm" onClick={() => deleteRule.mutate(rule.id)}>삭제</button>
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
