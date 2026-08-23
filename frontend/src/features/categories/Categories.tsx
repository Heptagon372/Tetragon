import { useEffect, useRef, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api, downloadFile, uploadExcel, type CategoryJob } from '../../shared/api'
import { Empty, ErrorBox, Page, shortDate } from '../../shared/ui'

export default function Categories() {
  const queryClient = useQueryClient()
  const fileInput = useRef<HTMLInputElement>(null)

  const [supplier, setSupplier] = useState('')
  const [parentCode, setParentCode] = useState<string | undefined>(undefined)
  const [selected, setSelected] = useState<{ code: string; name: string } | null>(null)
  const [keyword, setKeyword] = useState('')
  const [maxProducts, setMaxProducts] = useState(50)
  const [policyId, setPolicyId] = useState('')

  const crawlers = useQuery({ queryKey: ['categoryCrawlers'], queryFn: api.categoryCrawlers })
  const policies = useQuery({ queryKey: ['policies'], queryFn: api.policies })

  // 사용 가능한 첫 공급처를 자동 선택
  useEffect(() => {
    if (!supplier && crawlers.data?.length) {
      const available = crawlers.data.find((c) => c.isAvailable) ?? crawlers.data[0]
      setSupplier(available.supplierCode)
    }
  }, [crawlers.data, supplier])

  const categories = useQuery({
    queryKey: ['categories', supplier, parentCode],
    queryFn: () => api.categories(supplier, parentCode),
    enabled: Boolean(supplier),
  })

  const jobs = useQuery({
    queryKey: ['categoryJobs'],
    queryFn: () => api.categoryJobs(30),
    refetchInterval: 2500,
  })

  const preview = useMutation({
    mutationFn: () => api.previewCategory(supplier, {
      categoryCode: selected?.code,
      keyword: keyword || undefined,
      pageSize: 12,
    }),
  })

  const collect = useMutation({
    mutationFn: () => api.collectCategory(supplier, {
      categoryCode: selected?.code,
      categoryName: selected?.name,
      keyword: keyword || undefined,
      maxProducts,
      pricingPolicyId: policyId || undefined,
    }),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['categoryJobs'] }),
  })

  const exportCategories = useMutation({
    mutationFn: async () => {
      const result = await downloadFile(`/excel/categories/${supplier}`)
      if (!result.ok) throw new Error(result.error)
    },
  })

  const importCategories = useMutation({
    mutationFn: (file: File) =>
      uploadExcel<{ started: number; jobs: unknown[]; failed: unknown[] }>(
        '/excel/categories/import', file),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['categoryJobs'] }),
  })

  const currentCrawler = crawlers.data?.find((c) => c.supplierCode === supplier)
  const canCollect = Boolean(supplier) && (Boolean(selected) || Boolean(keyword))

  return (
    <Page
      title="카테고리 수집"
      desc="카테고리를 통째로 훑어 상품을 대량 수집합니다. 이미 수집한 상품은 자동으로 제외됩니다."
    >
      {currentCrawler && !currentCrawler.isAvailable && (
        <div className="error-box">
          {currentCrawler.displayName}는 자격증명이 필요합니다. 설정 화면에서 API 키를 등록하세요.
        </div>
      )}

      <div className="grid grid-2">
        <div className="card">
          <h2 className="card-title">1. 공급처와 카테고리 선택</h2>

          <div className="field">
            <label>공급처</label>
            <select value={supplier} onChange={(e) => {
              setSupplier(e.target.value); setParentCode(undefined); setSelected(null)
            }}>
              {crawlers.data?.map((c) => (
                <option key={c.supplierCode} value={c.supplierCode}>
                  {c.displayName}{c.isAvailable ? '' : ' (키 필요)'}
                </option>
              ))}
            </select>
          </div>

          <div className="row-between" style={{ marginBottom: 8 }}>
            <label style={{ margin: 0 }}>카테고리</label>
            {parentCode && (
              <button className="ghost sm" onClick={() => { setParentCode(undefined); setSelected(null) }}>
                ← 상위로
              </button>
            )}
          </div>

          <ErrorBox error={categories.error} />
          {categories.isLoading && <div className="muted" style={{ fontSize: 12.5 }}>불러오는 중…</div>}
          {categories.data?.length === 0 && !categories.isLoading && (
            <Empty>카테고리가 없습니다. 검색어로 수집할 수 있습니다.</Empty>
          )}

          <div style={{ maxHeight: 220, overflowY: 'auto', marginBottom: 12 }}>
            {categories.data?.map((category) => {
              // 조회 불가 카테고리(도매꾹 대분류 등)는 선택 대신 하위 탐색만 허용한다
              const drillDown = () => { setParentCode(category.code); setSelected(null) }
              return (
                <div
                  key={category.code}
                  className="row-between"
                  style={{
                    padding: '7px 10px',
                    borderRadius: 6,
                    cursor: 'pointer',
                    background: selected?.code === category.code ? 'var(--accent-dim)' : 'transparent',
                    opacity: category.isSelectable ? 1 : 0.75,
                  }}
                  onClick={() => category.isSelectable
                    ? setSelected({ code: category.code, name: category.name })
                    : drillDown()}
                  title={category.isSelectable ? undefined : '이 분류로는 바로 조회할 수 없습니다. 하위를 선택하세요.'}
                >
                  <span style={{ fontSize: 13 }}>
                    {category.name}
                    <span className="mono muted" style={{ marginLeft: 8, fontSize: 11 }}>{category.code}</span>
                    {!category.isSelectable && (
                      <span className="badge badge-dim" style={{ marginLeft: 6 }}>하위 선택 필요</span>
                    )}
                  </span>
                  {category.hasChildren && (
                    <button className="ghost sm" onClick={(e) => { e.stopPropagation(); drillDown() }}>
                      하위 →
                    </button>
                  )}
                </div>
              )
            })}
          </div>

          <div className="field">
            <label>검색어 (선택 — 카테고리와 함께 쓰거나 단독 사용)</label>
            <input value={keyword} onChange={(e) => setKeyword(e.target.value)} placeholder="예: 무선 이어폰" />
          </div>

          <div className="row" style={{ gap: 12 }}>
            <div style={{ flex: 1 }}>
              <label>최대 수집 개수</label>
              <input
                type="number" min={1} max={1000}
                value={maxProducts}
                onChange={(e) => setMaxProducts(Math.min(Number(e.target.value) || 1, 1000))}
              />
            </div>
            <div style={{ flex: 1 }}>
              <label>가격 정책</label>
              <select value={policyId} onChange={(e) => setPolicyId(e.target.value)}>
                <option value="">기본 정책</option>
                {policies.data?.map((p) => (
                  <option key={p.id} value={p.id}>{p.name}</option>
                ))}
              </select>
            </div>
          </div>

          <div className="row" style={{ marginTop: 14 }}>
            <button className="ghost" onClick={() => preview.mutate()} disabled={!canCollect || preview.isPending}>
              {preview.isPending ? '조회 중…' : '미리보기'}
            </button>
            <button onClick={() => collect.mutate()} disabled={!canCollect || collect.isPending}>
              {collect.isPending ? '시작 중…' : `${maxProducts}건 수집 시작`}
            </button>
          </div>

          <ErrorBox error={collect.error} />
          {collect.isSuccess && (
            <div className="ok-box" style={{ marginTop: 10 }}>
              카테고리 수집을 시작했습니다. 아래 진행 상황에서 확인하세요.
            </div>
          )}
        </div>

        <div className="card">
          <h2 className="card-title">2. 미리보기</h2>
          <ErrorBox error={preview.error} />
          {!preview.data && <Empty>미리보기를 실행하면 어떤 상품이 수집될지 확인할 수 있습니다.</Empty>}
          {preview.data && (
            <>
              <div className="muted" style={{ fontSize: 12.5, marginBottom: 10 }}>
                이 조건에 총 {preview.data.totalCount.toLocaleString()}건 · 아래는 상위 {preview.data.items.length}건
              </div>
              <div style={{ maxHeight: 380, overflowY: 'auto' }}>
                <table>
                  <tbody>
                    {preview.data.items.map((item) => (
                      <tr key={item.sourceProductId}>
                        <td style={{ width: 46 }}>
                          {item.thumbnailUrl && <img className="thumb" src={item.thumbnailUrl} alt="" loading="lazy" />}
                        </td>
                        <td style={{ fontSize: 12.5 }}>{item.title ?? item.sourceProductId}</td>
                        <td style={{ textAlign: 'right', whiteSpace: 'nowrap' }} className="mono">
                          {item.price?.toLocaleString()} {item.currency}
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            </>
          )}
        </div>
      </div>

      <div className="card">
        <h2 className="card-title">엑셀로 카테고리 대량 수집</h2>
        <p className="muted" style={{ fontSize: 12.5, marginTop: 0 }}>
          카테고리 목록을 엑셀로 받아 수집할 항목에 <strong>Y</strong>를 표시하고 최대 수량을 적은 뒤
          다시 업로드하면, 표시한 카테고리들이 순서대로 대량 수집됩니다.
        </p>
        <div className="row">
          <button className="ghost" onClick={() => exportCategories.mutate()} disabled={!supplier || exportCategories.isPending}>
            {exportCategories.isPending ? '생성 중…' : '① 카테고리 엑셀 내려받기'}
          </button>
          <button onClick={() => fileInput.current?.click()} disabled={importCategories.isPending}>
            {importCategories.isPending ? '업로드 중…' : '② 표시한 엑셀 업로드'}
          </button>
          <input
            ref={fileInput}
            type="file"
            accept=".xlsx"
            style={{ display: 'none' }}
            onChange={(e) => {
              const file = e.target.files?.[0]
              if (file) importCategories.mutate(file)
              e.target.value = ''
            }}
          />
        </div>
        <ErrorBox error={exportCategories.error} />
        <ErrorBox error={importCategories.error} />
        {importCategories.data && (
          <div className="ok-box" style={{ marginTop: 10 }}>
            {importCategories.data.started}개 카테고리 수집을 시작했습니다.
            {importCategories.data.failed.length > 0 && ` (${importCategories.data.failed.length}건 제외)`}
          </div>
        )}
      </div>

      <div className="card">
        <h2 className="card-title">카테고리 수집 진행 상황</h2>
        {jobs.data?.items.length === 0 && <Empty>아직 카테고리 수집 작업이 없습니다.</Empty>}
        {jobs.data && jobs.data.items.length > 0 && (
          <div className="table-wrap">
            <table>
              <thead>
                <tr>
                  <th>공급처</th>
                  <th>대상</th>
                  <th>상태</th>
                  <th style={{ textAlign: 'right' }}>발견</th>
                  <th style={{ textAlign: 'right' }}>수집요청</th>
                  <th style={{ textAlign: 'right' }}>중복제외</th>
                  <th>비고</th>
                  <th>시각</th>
                </tr>
              </thead>
              <tbody>
                {jobs.data.items.map((job) => (
                  <tr key={job.id}>
                    <td className="muted">{job.supplierCode}</td>
                    <td>
                      {job.categoryName ?? job.categoryCode ?? '—'}
                      {job.keyword && <span className="muted"> · “{job.keyword}”</span>}
                    </td>
                    <td><CategoryStateBadge state={job.state} /></td>
                    <td style={{ textAlign: 'right' }} className="muted">{job.foundCount}</td>
                    <td style={{ textAlign: 'right', fontWeight: 600 }}>{job.queuedCount}</td>
                    <td style={{ textAlign: 'right' }} className="muted">{job.skippedCount}</td>
                    <td className="muted" style={{ fontSize: 12, maxWidth: 260 }}>{job.lastError ?? ''}</td>
                    <td className="muted" style={{ fontSize: 12 }}>{shortDate(job.updatedAt)}</td>
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

function CategoryStateBadge({ state }: { state: CategoryJob['state'] }) {
  const map = {
    Pending: { cls: 'badge-dim', label: '대기' },
    Crawling: { cls: 'badge-info', label: '훑는 중' },
    Completed: { cls: 'badge-ok', label: '완료' },
    Failed: { cls: 'badge-danger', label: '실패' },
  }
  const { cls, label } = map[state]
  return <span className={`badge ${cls}`}>{label}</span>
}
