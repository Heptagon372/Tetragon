import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api, type AutomationPolicy, type AutomationRun, type CsTicket } from '../../shared/api'
import { Empty, ErrorBox, Page, shortTime, won } from '../../shared/ui'

/**
 * CS 관리 — 주문이 들어온 뒤의 일을 자동으로 돌린다.
 *
 *   구매자 주문 → 수집 → 공급처 발주 → 송장을 마켓에 반영
 *                              ↑
 *                    취소·반품이 오면 여기서 끊는다
 *
 * 이 화면의 절반은 '안전장치'다. 자동화가 사람 확인 없이 돈을 쓰기 때문에
 * 무엇을 얼마까지 살 수 있는지, 지금 시험인지 실전인지가 항상 보여야 한다.
 */
export default function Cs() {
  const queryClient = useQueryClient()
  const [lastRun, setLastRun] = useState<AutomationRun | null>(null)

  const policy = useQuery({ queryKey: ['automation'], queryFn: api.automation })
  const tickets = useQuery({ queryKey: ['csTickets'], queryFn: () => api.csTickets() })

  const save = useMutation({
    mutationFn: (body: Partial<AutomationPolicy>) => api.saveAutomation(body),
    onSuccess: (p) => queryClient.setQueryData(['automation'], p),
  })

  const resume = useMutation({
    mutationFn: () => api.resumeAutomation(),
    onSuccess: (p) => queryClient.setQueryData(['automation'], p),
  })

  const run = useMutation({
    mutationFn: () => api.runAutomation(),
    onSuccess: (r) => {
      setLastRun(r)
      queryClient.invalidateQueries({ queryKey: ['automation'] })
      queryClient.invalidateQueries({ queryKey: ['csTickets'] })
      queryClient.invalidateQueries({ queryKey: ['orders'] })
    },
  })

  const p = policy.data

  return (
    <Page title="CS 관리" desc="주문 수집 → 공급처 발주 → 송장 반영 → 취소·반품 처리를 자동으로 돌립니다.">
      {p?.haltedReason && (
        <div className="card" style={{ borderColor: 'var(--danger)' }}>
          <div className="row-between">
            <div>
              <strong style={{ color: 'var(--danger)' }}>자동화가 멈춰 있습니다</strong>
              <div className="muted" style={{ fontSize: 12.5, marginTop: 4 }}>{p.haltedReason}</div>
            </div>
            <button onClick={() => resume.mutate()} disabled={resume.isPending}>
              원인 확인함 · 재개
            </button>
          </div>
        </div>
      )}

      <div className="card">
        <div className="row-between" style={{ marginBottom: 10 }}>
          <h2 className="card-title" style={{ margin: 0 }}>자동 이행</h2>
          <div className="row" style={{ gap: 8 }}>
            <button className="ghost" onClick={() => run.mutate()} disabled={run.isPending}>
              {run.isPending ? '실행 중…' : '지금 한 번 실행'}
            </button>
          </div>
        </div>

        <ErrorBox error={policy.error} />
        <ErrorBox error={save.error} />
        <ErrorBox error={run.error} />

        {p && (
          <>
            <div className={p.dryRun ? 'warn-box' : 'ok-box'} style={{ marginBottom: 14, fontSize: 12.5 }}>
              {p.dryRun
                ? '시험 모드 — 발주 직전까지만 실행하고 실제 결제는 하지 않습니다. 무엇이 처리될지 먼저 확인하세요.'
                : '실전 모드 — 조건을 통과한 주문은 실제로 공급처에서 결제합니다.'}
            </div>

            <div className="row" style={{ gap: 22, flexWrap: 'wrap', marginBottom: 16 }}>
              <Toggle label="자동화 켜기" checked={p.enabled}
                onChange={(v) => save.mutate({ enabled: v })} strong />
              <Toggle label="시험 모드" checked={p.dryRun}
                onChange={(v) => save.mutate({ dryRun: v })} />
              <Toggle label="주문 수집" checked={p.autoCollectOrders}
                onChange={(v) => save.mutate({ autoCollectOrders: v })} />
              <Toggle label="자동 발주" checked={p.autoPurchase}
                onChange={(v) => save.mutate({ autoPurchase: v })} />
              <Toggle label="송장 반영" checked={p.autoUploadTracking}
                onChange={(v) => save.mutate({ autoUploadTracking: v })} />
              <Toggle label="CS 수집" checked={p.autoCollectCs}
                onChange={(v) => save.mutate({ autoCollectCs: v })} />
            </div>

            <h3 style={{ fontSize: 13, margin: '0 0 8px' }}>한도 — 잘못될 때 손실을 끊는 장치</h3>
            <div className="row" style={{ gap: 12, alignItems: 'flex-end', flexWrap: 'wrap' }}>
              <NumberField label="1건 최대" value={p.maxOrderAmount} suffix="원"
                onCommit={(v) => save.mutate({ maxOrderAmount: v })} />
              <NumberField label="하루 최대" value={p.dailyLimit} suffix="원"
                onCommit={(v) => save.mutate({ dailyLimit: v })} />
              <NumberField label="연속 실패 허용" value={p.consecutiveFailureLimit} suffix="회"
                onCommit={(v) => save.mutate({ consecutiveFailureLimit: v })} />
              <NumberField label="실행 주기" value={p.intervalMinutes} suffix="분"
                onCommit={(v) => save.mutate({ intervalMinutes: v })} />
            </div>

            <div className="row" style={{ gap: 20, fontSize: 12.5, marginTop: 14, flexWrap: 'wrap' }}>
              <span><span className="muted">오늘 사용 </span><strong>{won(p.spentToday)}</strong>
                <span className="muted"> / {won(p.dailyLimit)}</span></span>
              <span><span className="muted">연속 실패 </span>{p.consecutiveFailures}회</span>
              {p.lastRunAt && (
                <span><span className="muted">최근 실행 </span>{shortTime(p.lastRunAt)} · {p.lastRunSummary}</span>
              )}
            </div>
          </>
        )}
      </div>

      {lastRun && <RunResult run={lastRun} />}

      <TicketList
        tickets={tickets.data?.items ?? []}
        needsAttention={tickets.data?.needsAttention ?? 0}
        loading={tickets.isLoading}
        error={tickets.error}
        onChanged={() => queryClient.invalidateQueries({ queryKey: ['csTickets'] })}
      />
    </Page>
  )
}

function RunResult({ run }: { run: AutomationRun }) {
  return (
    <div className="card">
      <div className="row-between" style={{ marginBottom: 8 }}>
        <h2 className="card-title" style={{ margin: 0 }}>
          실행 결과 {run.dryRun && <span className="badge badge-warn">시험</span>}
        </h2>
        <span className="muted" style={{ fontSize: 12 }}>{shortTime(run.startedAt)}</span>
      </div>

      <div style={{ fontSize: 13, marginBottom: 10 }}>{run.skipReason ?? run.summary}</div>

      <div className="row" style={{ gap: 18, fontSize: 12.5, flexWrap: 'wrap', marginBottom: 10 }}>
        <Stat label="주문 수집" n={run.ordersCollected} />
        <Stat label="공급처 연결" n={run.ordersLinked} />
        <Stat label="발주" n={run.purchased} />
        <Stat label="송장 반영" n={run.trackingUploaded} />
        <Stat label="CS 수집" n={run.csCollected} />
        <Stat label="자동 취소" n={run.csAutoCancelled} />
      </div>

      <Lines title="실전이면 발주했을 주문" items={run.wouldPurchase} tone="warn" />
      <Lines title="발주 보류 — 사람이 봐야 합니다" items={run.purchaseSkipped} tone="muted" />
      <Lines title="송장 반영 건너뜀" items={run.trackingSkipped} tone="muted" />
      <Lines title="이 마켓이 지원하지 않는 기능 (정상)" items={run.unsupported} tone="muted" />
      <Lines title="오류" items={run.errors} tone="danger" />
    </div>
  )
}

function Lines({ title, items, tone }: { title: string; items: string[]; tone: string }) {
  if (items.length === 0) return null
  const color = tone === 'danger' ? 'var(--danger)' : tone === 'warn' ? 'var(--warn)' : 'var(--muted)'
  return (
    <div style={{ marginTop: 10 }}>
      <h3 style={{ fontSize: 12.5, margin: '0 0 4px', color }}>{title} ({items.length})</h3>
      <div style={{ maxHeight: 180, overflowY: 'auto', fontSize: 12 }}>
        {items.map((line, i) => (
          <div key={i} className="muted" style={{ padding: '1px 0' }}>· {line}</div>
        ))}
      </div>
    </div>
  )
}

function TicketList({
  tickets, needsAttention, loading, error, onChanged,
}: {
  tickets: CsTicket[]
  needsAttention: number
  loading: boolean
  error: unknown
  onChanged: () => void
}) {
  const supplierDone = useMutation({
    mutationFn: (id: string) => api.csSupplierDone(id),
    onSuccess: onChanged,
  })
  const resolve = useMutation({
    mutationFn: (id: string) => api.csResolve(id),
    onSuccess: onChanged,
  })
  const dismiss = useMutation({
    mutationFn: (id: string) => api.csDismiss(id),
    onSuccess: onChanged,
  })

  const kindLabel: Record<string, string> = { Cancel: '취소', Return: '반품', Exchange: '교환' }

  return (
    <div className="card">
      <div className="row-between" style={{ marginBottom: 8 }}>
        <h2 className="card-title" style={{ margin: 0 }}>
          취소 · 반품 요청
          {needsAttention > 0 && (
            <span className="badge badge-danger" style={{ marginLeft: 8 }}>처리 필요 {needsAttention}</span>
          )}
        </h2>
      </div>

      <p className="muted" style={{ fontSize: 12.5, marginTop: 0 }}>
        위탁판매는 물건이 우리 손을 거치지 않습니다. 마켓만 취소하고 공급처를 놔두면
        <strong> 우리 돈으로 산 물건이 그대로 나갑니다.</strong> 그래서 발주가 나간 건은 공급처 조치를 따로 표시합니다.
      </p>

      <ErrorBox error={error} />
      <ErrorBox error={resolve.error} />
      {loading && <div className="muted" style={{ fontSize: 12.5 }}>불러오는 중…</div>}
      {!loading && tickets.length === 0 && <Empty>접수된 취소·반품 요청이 없습니다.</Empty>}

      {tickets.length > 0 && (
        <div className="table-wrap" style={{ maxHeight: 460 }}>
          <table>
            <thead>
              <tr>
                <th style={{ width: 58 }}>종류</th>
                <th>상품 / 사유</th>
                <th style={{ width: 130 }}>주문</th>
                <th style={{ width: 150 }}>공급처 조치</th>
                <th style={{ width: 170 }} />
              </tr>
            </thead>
            <tbody>
              {tickets.map((t) => (
                <tr key={t.id} style={{ opacity: t.status === 'Open' ? 1 : 0.55 }}>
                  <td>
                    <span className={`badge ${t.kind === 'Return' ? 'badge-warn' : 'badge-dim'}`}>
                      {kindLabel[t.kind]}
                    </span>
                  </td>
                  <td style={{ maxWidth: 300 }}>
                    <div style={{ fontSize: 12.5 }}>{t.productName ?? '—'}</div>
                    <div className="muted" style={{ fontSize: 11.5 }}>{t.reason}</div>
                  </td>
                  <td className="mono muted" style={{ fontSize: 11.5 }}>
                    {t.marketOrderId}
                    {t.order && <div>{t.order.status}</div>}
                  </td>
                  <td style={{ fontSize: 12 }}>
                    {!t.supplierActionRequired
                      ? <span className="muted">불필요 (발주 전)</span>
                      : t.supplierActionDone
                        ? <span style={{ color: 'var(--ok)' }}>완료</span>
                        : <span style={{ color: 'var(--danger)' }}>필요 — 공급처에서 취소하세요</span>}
                    {t.order?.supplierOrderNo && (
                      <div className="muted" style={{ fontSize: 11 }}>공급처 {t.order.supplierOrderNo}</div>
                    )}
                  </td>
                  <td>
                    {t.status === 'Open' && (
                      <div className="row" style={{ gap: 5 }}>
                        {t.supplierActionRequired && !t.supplierActionDone && (
                          <>
                            {t.order?.supplierUrl && (
                              <a className="ghost sm" href={t.order.supplierUrl}
                                target="_blank" rel="noreferrer"
                                style={{ padding: '3px 7px', fontSize: 11.5 }}>
                                공급처 열기
                              </a>
                            )}
                            <button className="ghost sm" onClick={() => supplierDone.mutate(t.id)}>
                              조치함
                            </button>
                          </>
                        )}
                        <button className="sm" onClick={() => resolve.mutate(t.id)}>완료</button>
                        <button className="ghost sm" onClick={() => dismiss.mutate(t.id)}>무시</button>
                      </div>
                    )}
                    {t.status !== 'Open' && (
                      <span className="muted" style={{ fontSize: 11.5 }}>
                        {t.status === 'Resolved' ? '처리됨' : '무시됨'}
                      </span>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  )
}

function Toggle({
  label, checked, onChange, strong,
}: {
  label: string; checked: boolean; onChange: (v: boolean) => void; strong?: boolean
}) {
  return (
    <label className="row" style={{ gap: 6, fontSize: 13, fontWeight: strong ? 600 : 400 }}>
      <input type="checkbox" checked={checked} onChange={(e) => onChange(e.target.checked)} />
      {label}
    </label>
  )
}

function NumberField({
  label, value, suffix, onCommit,
}: {
  label: string; value: number; suffix: string; onCommit: (v: number) => void
}) {
  const [draft, setDraft] = useState(String(value))
  return (
    <div style={{ width: 130 }}>
      <label>{label} ({suffix})</label>
      <input
        type="number"
        value={draft}
        onChange={(e) => setDraft(e.target.value)}
        onBlur={() => {
          const n = Number(draft)
          if (n > 0 && n !== value) onCommit(n)
          else setDraft(String(value))
        }}
      />
    </div>
  )
}

function Stat({ label, n }: { label: string; n: number }) {
  return (
    <span>
      <span className="muted">{label} </span>
      <strong style={{ color: n > 0 ? 'var(--accent)' : undefined }}>{n}</strong>
    </span>
  )
}
