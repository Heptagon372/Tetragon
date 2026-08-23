import { useEffect, useRef, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { Link } from 'react-router-dom'
import { api, subscribePipeline, type PipelineEvent } from '../../shared/api'
import { Empty, ErrorBox, Page, PipelineStepper, StatusBadge, shortTime } from '../../shared/ui'

export default function Collect() {
  const [urlText, setUrlText] = useState('')
  const [events, setEvents] = useState<PipelineEvent[]>([])
  const queryClient = useQueryClient()
  const feedRef = useRef<HTMLDivElement>(null)

  const policies = useQuery({ queryKey: ['policies'], queryFn: api.policies })
  const [policyId, setPolicyId] = useState('')

  const jobs = useQuery({
    queryKey: ['jobs'],
    queryFn: () => api.jobs(50),
    refetchInterval: 2000, // SSE 보조 — 폴링으로 최종 상태 보정
  })

  const collect = useMutation({
    mutationFn: (urls: string[]) => api.collect(urls, policyId || undefined),
    onSuccess: () => {
      setUrlText('')
      queryClient.invalidateQueries({ queryKey: ['jobs'] })
    },
  })

  const retry = useMutation({
    mutationFn: (id: string) => api.retryJob(id),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['jobs'] }),
  })

  // SSE 실시간 피드
  useEffect(() => {
    const unsubscribe = subscribePipeline((event) => {
      setEvents((prev) => [...prev.slice(-60), event])
      queryClient.invalidateQueries({ queryKey: ['jobs'] })
    })
    return unsubscribe
  }, [queryClient])

  useEffect(() => {
    feedRef.current?.scrollTo({ top: feedRef.current.scrollHeight })
  }, [events])

  const urls = urlText.split('\n').map((u) => u.trim()).filter(Boolean)

  return (
    <Page
      title="상품 수집"
      desc="해외 쇼핑몰 상품 URL을 붙여넣으면 수집 → 표준화 → 번역 → 가격계산 → 컴플라이언스 검사가 자동 실행됩니다."
    >
      <div className="grid grid-2">
        <div className="card">
          <h2 className="card-title">URL 대량 입력</h2>
          <ErrorBox error={collect.error} />
          {collect.data && (
            <div className="ok-box">
              {collect.data.accepted}건 수집 요청 완료
              {collect.data.rejected.length > 0 && ` (잘못된 URL ${collect.data.rejected.length}건 제외)`}
            </div>
          )}

          <div className="field">
            <label>상품 URL (한 줄에 하나씩)</label>
            <textarea
              value={urlText}
              onChange={(e) => setUrlText(e.target.value)}
              placeholder={'https://www.aliexpress.com/item/1005006....html\nhttps://item.taobao.com/item.htm?id=...\nhttps://mock.shop/item/1001.html  ← 시뮬레이션 (키 불필요)'}
              rows={7}
            />
          </div>

          <div className="field">
            <label>가격 정책</label>
            <select value={policyId} onChange={(e) => setPolicyId(e.target.value)}>
              <option value="">기본 정책 사용</option>
              {policies.data?.map((p) => (
                <option key={p.id} value={p.id}>
                  {p.name}{p.isDefault ? ' (기본)' : ''}
                </option>
              ))}
            </select>
          </div>

          <div className="row-between">
            <span className="muted" style={{ fontSize: 12 }}>{urls.length}개 URL 인식됨</span>
            <button onClick={() => collect.mutate(urls)} disabled={urls.length === 0 || collect.isPending}>
              {collect.isPending ? '요청 중…' : `${urls.length}건 수집 시작`}
            </button>
          </div>
        </div>

        <div className="card">
          <h2 className="card-title">실시간 파이프라인 이벤트 (SSE)</h2>
          <div className="live-feed" ref={feedRef}>
            {events.length === 0 && <div className="muted">이벤트 대기 중…</div>}
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

      <div className="card">
        <h2 className="card-title">수집 작업 현황</h2>
        <ErrorBox error={jobs.error} />
        {jobs.data && jobs.data.items.length === 0 && <Empty>아직 수집 작업이 없습니다.</Empty>}
        {jobs.data && jobs.data.items.length > 0 && (
          <div className="table-wrap">
            <table>
              <thead>
                <tr>
                  <th style={{ width: '28%' }}>URL</th>
                  <th>파이프라인</th>
                  <th>상태</th>
                  <th>공급처</th>
                  <th>비고</th>
                  <th />
                </tr>
              </thead>
              <tbody>
                {jobs.data.items.map((job) => (
                  <tr key={job.id}>
                    <td className="mono" style={{ maxWidth: 260, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                      {job.productId
                        ? <Link to={`/products/${job.productId}`} style={{ color: 'var(--accent)' }}>{job.url}</Link>
                        : job.url}
                    </td>
                    <td><PipelineStepper job={job} stages={jobs.data.stages} /></td>
                    <td><StatusBadge status={job.state} /></td>
                    <td className="muted">{job.supplierCode ?? '—'}</td>
                    <td className="muted" style={{ maxWidth: 220, fontSize: 12 }}>
                      {job.lastError ?? (job.attempts > 0 ? `재시도 ${job.attempts}회` : '')}
                    </td>
                    <td>
                      {(job.state === 'Failed' || job.state === 'DeadLettered') && (
                        <button className="ghost sm" onClick={() => retry.mutate(job.id)}>재시도</button>
                      )}
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
