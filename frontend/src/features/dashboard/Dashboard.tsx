import { useQuery } from '@tanstack/react-query'
import { Link } from 'react-router-dom'
import { api } from '../../shared/api'
import { Empty, ErrorBox, Page } from '../../shared/ui'
import './dashboard.css'

/**
 * 파이프라인 단계는 순서가 있는 척도이므로 단일 hue 순차(ordinal) 램프를 쓴다.
 * 다크 서피스(#171a21) 기준 검증 완료 — 가장 밝은 끝 2:1 이상, 단조 감소.
 */
const STAGE_RAMP = ['#cde2fb', '#9ec5f4', '#6da7ec', '#3987e5', '#256abf', '#1c5cab', '#184f95']

const STAGE_LABELS: Record<string, string> = {
  Queued: '대기', Collecting: '수집', Normalizing: '표준화', Enriching: '번역',
  Pricing: '가격계산', Compliance: '검사', ReadyToList: '등록대기',
  Listing: '등록중', Completed: '완료',
}
const STAGE_ORDER = Object.keys(STAGE_LABELS)

const STATUS_LABELS: Record<string, string> = {
  Draft: '수집됨', Normalized: '표준화', Enriched: '번역완료', Priced: '가격산정',
  Ready: '등록대기', Listed: '등록됨', Blocked: '차단', Failed: '실패',
}
const STATUS_ORDER = ['Draft', 'Normalized', 'Enriched', 'Priced', 'Ready', 'Listed', 'Blocked', 'Failed']
/** 상태 색: 진행 단계는 순차 램프, 오류 상태는 예약된 status 색(라벨과 항상 동반). */
const STATUS_COLOR = (status: string, index: number) =>
  status === 'Blocked' || status === 'Failed' ? '#d03b3b' : STAGE_RAMP[Math.min(index, STAGE_RAMP.length - 1)]

const compact = (n: number) =>
  n >= 1_000_000 ? `${(n / 1_000_000).toFixed(1)}M`
    : n >= 10_000 ? `${(n / 1_000).toFixed(1)}K`
      : n.toLocaleString('ko-KR')

export default function Dashboard() {
  const summary = useQuery({
    queryKey: ['dashboard'],
    queryFn: api.dashboard,
    refetchInterval: 5000,
  })

  if (summary.isLoading) return <Empty>불러오는 중…</Empty>
  if (summary.error) return <ErrorBox error={summary.error} />
  const d = summary.data
  if (!d) return null

  const stageRows = STAGE_ORDER
    .map((stage, index) => ({ stage, label: STAGE_LABELS[stage], count: d.pipeline.byStage[stage] ?? 0, index }))
    .filter((row) => row.count > 0)
  const stageMax = Math.max(...stageRows.map((r) => r.count), 1)

  const statusRows = STATUS_ORDER
    .map((status, index) => ({ status, label: STATUS_LABELS[status], count: d.products.byStatus[status] ?? 0, index }))
    .filter((row) => row.count > 0)
  const statusMax = Math.max(...statusRows.map((r) => r.count), 1)

  const markets = Object.entries(d.listings.byMarket)

  return (
    <Page title="대시보드" desc="파이프라인 진행 상황과 등록·주문 현황 (5초마다 자동 갱신)">
      {/* 헤드라인 숫자 — 차트가 아니라 stat tile */}
      <div className="grid grid-4">
        <div className="stat">
          <div className="stat-label">전체 상품</div>
          <div className="stat-value">{compact(d.products.total)}</div>
          <div className="stat-sub">오늘 {d.pipeline.todayCollected}건 수집</div>
        </div>
        <div className="stat">
          <div className="stat-label">파이프라인 진행중</div>
          <div className="stat-value">{compact(d.pipeline.running)}</div>
          <div className="stat-sub">
            성공 {d.pipeline.succeeded}
            {d.pipeline.failed > 0 && <span style={{ color: 'var(--danger)' }}> · 실패 {d.pipeline.failed}</span>}
            {d.pipeline.blocked > 0 && <span style={{ color: 'var(--warn)' }}> · 차단 {d.pipeline.blocked}</span>}
          </div>
        </div>
        <div className="stat">
          <div className="stat-label">마켓 등록 완료</div>
          <div className="stat-value">{compact(d.listings.registered)}</div>
          <div className="stat-sub">
            전체 {d.listings.total}건
            {d.listings.failed > 0 && <span style={{ color: 'var(--danger)' }}> · 실패 {d.listings.failed}</span>}
          </div>
        </div>
        <div className="stat">
          <div className="stat-label">누적 매출</div>
          <div className="stat-value">{compact(d.orders.revenue)}원</div>
          <div className="stat-sub">주문 {d.orders.total}건</div>
        </div>
      </div>

      <div className="grid grid-2" style={{ marginTop: 14 }}>
        <div className="card">
          <h2 className="card-title">파이프라인 단계별 체류</h2>
          {stageRows.length === 0
            ? <Empty>진행 중인 작업이 없습니다.</Empty>
            : (
              <div className="bars">
                {stageRows.map((row) => (
                  <div className="bar-row" key={row.stage}>
                    <span className="bar-label">{row.label}</span>
                    <div className="bar-track">
                      <div
                        className="bar-fill"
                        style={{
                          width: `${Math.max((row.count / stageMax) * 100, 2)}%`,
                          background: STAGE_RAMP[Math.min(row.index, STAGE_RAMP.length - 1)],
                        }}
                      />
                    </div>
                    <span className="bar-value">{row.count.toLocaleString()}</span>
                  </div>
                ))}
              </div>
            )}
        </div>

        <div className="card">
          <h2 className="card-title">상품 상태 분포</h2>
          {statusRows.length === 0
            ? <Empty>상품이 없습니다.</Empty>
            : (
              <div className="bars">
                {statusRows.map((row) => (
                  <div className="bar-row" key={row.status}>
                    <span className="bar-label">{row.label}</span>
                    <div className="bar-track">
                      <div
                        className="bar-fill"
                        style={{
                          width: `${Math.max((row.count / statusMax) * 100, 2)}%`,
                          background: STATUS_COLOR(row.status, row.index),
                        }}
                      />
                    </div>
                    <span className="bar-value">{row.count.toLocaleString()}</span>
                  </div>
                ))}
              </div>
            )}
        </div>
      </div>

      <div className="grid grid-2" style={{ marginTop: 14 }}>
        <div className="card">
          <h2 className="card-title">마켓별 등록 현황</h2>
          {markets.length === 0
            ? <Empty>등록된 마켓이 없습니다.</Empty>
            : (
              <div className="bars">
                {markets.map(([code, stats]) => (
                  <div className="bar-row" key={code}>
                    <span className="bar-label">{code}</span>
                    {/* 비율 대비 — 트랙은 같은 램프의 밝은 단계 */}
                    <div className="bar-track meter">
                      <div
                        className="bar-fill"
                        style={{
                          width: `${(stats.registered / Math.max(stats.total, 1)) * 100}%`,
                          background: '#3987e5',
                        }}
                      />
                    </div>
                    <span className="bar-value">
                      {stats.registered}<span className="muted"> / {stats.total}</span>
                    </span>
                  </div>
                ))}
              </div>
            )}
        </div>

        <div className="card">
          <h2 className="card-title">바로가기</h2>
          <div className="row">
            <Link to="/collect"><button className="ghost sm">URL 수집하기</button></Link>
            <Link to="/products?status=Ready"><button className="ghost sm">등록 대기 상품</button></Link>
            <Link to="/listings"><button className="ghost sm">등록 현황</button></Link>
            <Link to="/settings"><button className="ghost sm">API 키 설정</button></Link>
          </div>
          {d.pipeline.failed > 0 && (
            <div className="error-box" style={{ marginTop: 14, marginBottom: 0 }}>
              실패한 작업이 {d.pipeline.failed}건 있습니다.{' '}
              <Link to="/collect" style={{ textDecoration: 'underline' }}>수집 페이지</Link>에서 재시도할 수 있습니다.
            </div>
          )}
          {d.pipeline.blocked > 0 && (
            <div className="ok-box" style={{ marginTop: 10, marginBottom: 0, background: 'rgba(240,180,41,0.1)', borderColor: 'rgba(240,180,41,0.3)', color: 'var(--warn)' }}>
              컴플라이언스 차단 {d.pipeline.blocked}건 — 확인 후 등록을 재개하세요.
            </div>
          )}
        </div>
      </div>
    </Page>
  )
}
