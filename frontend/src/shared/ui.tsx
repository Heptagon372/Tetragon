import type { ReactNode } from 'react'
import type { Job, ProductStatus } from './api'

export function Page({ title, desc, actions, children }: {
  title: string
  desc?: string
  actions?: ReactNode
  children: ReactNode
}) {
  return (
    <>
      <div className="row-between" style={{ marginBottom: 20 }}>
        <div>
          <h1 className="page-title">{title}</h1>
          {desc && <p className="page-desc" style={{ margin: 0 }}>{desc}</p>}
        </div>
        {actions && <div className="row">{actions}</div>}
      </div>
      {children}
    </>
  )
}

export function StatusBadge({ status }: { status: ProductStatus | string }) {
  const map: Record<string, { cls: string; label: string }> = {
    Draft: { cls: 'badge-dim', label: '수집됨' },
    Normalized: { cls: 'badge-dim', label: '표준화' },
    Enriched: { cls: 'badge-info', label: '번역완료' },
    Priced: { cls: 'badge-info', label: '가격산정' },
    Ready: { cls: 'badge-ok', label: '등록대기' },
    Listed: { cls: 'badge-ok', label: '등록됨' },
    Blocked: { cls: 'badge-danger', label: '차단' },
    Failed: { cls: 'badge-danger', label: '실패' },
    Registered: { cls: 'badge-ok', label: '등록됨' },
    Pending: { cls: 'badge-dim', label: '대기' },
    Suspended: { cls: 'badge-warn', label: '판매중지' },
    Succeeded: { cls: 'badge-ok', label: '성공' },
    Running: { cls: 'badge-info', label: '진행중' },
    DeadLettered: { cls: 'badge-danger', label: 'DLQ' },
    Pass: { cls: 'badge-ok', label: '통과' },
    Warn: { cls: 'badge-warn', label: '경고' },
    Block: { cls: 'badge-danger', label: '차단' },
  }
  const { cls, label } = map[status] ?? { cls: 'badge-dim', label: status }
  return <span className={`badge ${cls}`}>{label}</span>
}

const STAGE_LABELS: Record<string, string> = {
  Queued: '대기',
  Collecting: '수집',
  Normalizing: '표준화',
  Enriching: '번역',
  Pricing: '가격',
  Compliance: '검사',
  Completed: '완료',
}

/** 파이프라인 진행 스텝퍼 (설계서 6.1 단계와 1:1). */
export function PipelineStepper({ job, stages }: { job: Job; stages: string[] }) {
  const isError = job.state === 'Failed' || job.state === 'DeadLettered'
  const isBlocked = job.state === 'Blocked'
  const current = job.stageIndex

  return (
    <div className="stepper">
      {stages.map((stage, index) => {
        const done = index < current || job.state === 'Succeeded'
        const active = index === current && job.state === 'Running'
        const errored = index === current && (isError || isBlocked)
        const cls = errored ? 'error' : active ? 'active' : done ? 'done' : ''
        return (
          <div key={stage} style={{ display: 'flex', alignItems: 'center' }}>
            {index > 0 && <div className="step-line" />}
            <div className={`step ${cls}`} title={stage}>
              <span className="step-dot" />
              {STAGE_LABELS[stage] ?? stage}
            </div>
          </div>
        )
      })}
    </div>
  )
}

export function Empty({ children }: { children: ReactNode }) {
  return <div className="empty">{children}</div>
}

export function ErrorBox({ error }: { error: unknown }) {
  if (!error) return null
  const message = error instanceof Error ? error.message : String(error)
  return <div className="error-box">{message}</div>
}

export const won = (value?: number | null) =>
  value == null ? '—' : `${Math.round(value).toLocaleString('ko-KR')}원`

export const shortTime = (iso: string) =>
  new Date(iso).toLocaleTimeString('ko-KR', { hour: '2-digit', minute: '2-digit', second: '2-digit' })

export const shortDate = (iso: string) =>
  new Date(iso).toLocaleString('ko-KR', { month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit' })
