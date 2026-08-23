import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api, type AttentionItem, type AttentionScanResult } from '../../shared/api'
import { Empty, ErrorBox, Page, shortDate, won } from '../../shared/ui'

/**
 * 할 일 — 주의 원장 (확장 08 §1).
 *
 * 이 화면의 규칙은 하나다: **영향 금액 순으로만 보여준다.**
 * 유한한 자원은 등록 슬롯이 아니라 운영자의 주의이고,
 * 그 주의를 어디에 쓸지는 "얼마가 걸려 있나"가 정한다.
 *
 * 그래서 금액 옆에는 항상 근거 문장이 붙어 있다.
 * 근거를 못 적는 항목은 애초에 원장에 들어오지 못한다 —
 * 그 규칙 하나가 알림 폭주를 막는다.
 */
const STATE_TABS = [
  { key: 'due', label: '지금 볼 것' },
  { key: 'Snoozed', label: '연기됨' },
  { key: 'Done', label: '처리됨' },
  { key: 'Dismissed', label: '무시함' },
] as const

export default function Attention() {
  const queryClient = useQueryClient()
  const [state, setState] = useState<string>('due')
  const [kind, setKind] = useState<string>('')
  const [selected, setSelected] = useState<Set<string>>(new Set())
  const [lastScan, setLastScan] = useState<AttentionScanResult | null>(null)

  const summary = useQuery({ queryKey: ['attentionSummary'], queryFn: api.attentionSummary })
  const list = useQuery({
    queryKey: ['attention', state, kind],
    queryFn: () => api.attention({ state, kind: kind || undefined, limit: 200 }),
  })

  const refresh = () => {
    setSelected(new Set())
    queryClient.invalidateQueries({ queryKey: ['attention'] })
    queryClient.invalidateQueries({ queryKey: ['attentionSummary'] })
  }

  const scan = useMutation({
    mutationFn: () => api.attentionScan(),
    onSuccess: (result) => { setLastScan(result); refresh() },
  })
  const bulk = useMutation({
    mutationFn: (action: 'snooze' | 'done' | 'dismiss') =>
      api.attentionBulk([...selected], action, 7),
    onSuccess: refresh,
  })

  const items = list.data?.items ?? []
  const totalImpact = items.reduce((sum, i) => sum + i.impactKrw, 0)
  const isOpenView = state === 'due' || state === 'Snoozed'

  const toggle = (id: string) => setSelected((prev) => {
    const next = new Set(prev)
    if (!next.delete(id)) next.add(id)
    return next
  })

  return (
    <Page
      title="할 일"
      desc="사람이 봐야 하는 것을 한 곳에 모아 영향 금액 순으로 세웁니다. 위에서부터 처리하면 됩니다."
      actions={
        <button className="ghost" onClick={() => scan.mutate()} disabled={scan.isPending}>
          {scan.isPending ? '확인 중…' : '지금 다시 확인'}
        </button>
      }
    >
      <SummaryStrip
        open={summary.data?.open ?? 0}
        impactKrw={summary.data?.impactKrw ?? 0}
        kinds={summary.data?.kinds ?? []}
        active={kind}
        onPick={(k) => { setKind(k === kind ? '' : k); setSelected(new Set()) }}
      />

      {lastScan && <ScanResult result={lastScan} onClose={() => setLastScan(null)} />}

      <div className="card">
        <div className="row-between" style={{ marginBottom: 10 }}>
          <div className="row" style={{ gap: 6 }}>
            {STATE_TABS.map((tab) => (
              <button
                key={tab.key}
                className={state === tab.key ? 'sm' : 'ghost sm'}
                onClick={() => { setState(tab.key); setSelected(new Set()) }}
              >
                {tab.label}
              </button>
            ))}
          </div>
          <span className="muted" style={{ fontSize: 12 }}>
            {items.length}건 · 합계 {won(totalImpact)}
          </span>
        </div>

        {/* 원장은 '사실'을 담는다 — 사실이 그대로면 닫아도 다시 열린다. 이걸 모르면 목록이 고장난 것처럼 보인다 */}
        <p className="muted" style={{ fontSize: 12, marginTop: 0 }}>
          항목은 사실이 사라지면 저절로 닫힙니다. 사실이 그대로면 <strong>무시해도 다시 올라옵니다</strong> —
          잠깐 미루려면 &lsquo;7일 뒤&rsquo;를 쓰세요.
        </p>

        <ErrorBox error={list.error} />
        <ErrorBox error={scan.error} />
        <ErrorBox error={bulk.error} />

        {selected.size > 0 && (
          <div className="row-between warn-box" style={{ marginBottom: 12, fontSize: 12.5 }}>
            <span>{selected.size}건 선택됨</span>
            <div className="row" style={{ gap: 6 }}>
              <button className="ghost sm" onClick={() => bulk.mutate('snooze')} disabled={bulk.isPending}>
                7일 연기
              </button>
              <button className="sm" onClick={() => bulk.mutate('done')} disabled={bulk.isPending}>
                처리 완료
              </button>
              <button className="ghost sm" onClick={() => bulk.mutate('dismiss')} disabled={bulk.isPending}>
                무시
              </button>
            </div>
          </div>
        )}

        {list.isLoading && <div className="muted" style={{ fontSize: 12.5 }}>불러오는 중…</div>}
        {!list.isLoading && items.length === 0 && (
          <Empty>
            {state === 'due'
              ? '지금 손댈 것이 없습니다. 사실이 사라진 항목은 스윕이 알아서 닫습니다.'
              : '해당하는 항목이 없습니다.'}
          </Empty>
        )}

        {items.length > 0 && (
          <div className="table-wrap">
            <table>
              <thead>
                <tr>
                  {isOpenView && <th style={{ width: 30 }} />}
                  <th style={{ width: 74 }}>중요도</th>
                  <th>할 일 / 근거</th>
                  <th style={{ width: 96 }}>종류</th>
                  <th style={{ width: 110 }}>영향 금액</th>
                  <th style={{ width: 120 }}>마지막 확인</th>
                  {isOpenView && <th style={{ width: 160 }} />}
                </tr>
              </thead>
              <tbody>
                {items.map((item) => (
                  <Row
                    key={item.id}
                    item={item}
                    selectable={isOpenView}
                    checked={selected.has(item.id)}
                    onToggle={() => toggle(item.id)}
                    onChanged={refresh}
                  />
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>
    </Page>
  )
}

function SummaryStrip({
  open, impactKrw, kinds, active, onPick,
}: {
  open: number
  impactKrw: number
  kinds: { kind: string; label: string; count: number; impactKrw: number }[]
  active: string
  onPick: (kind: string) => void
}) {
  return (
    <div className="card">
      <div className="row" style={{ gap: 24, marginBottom: kinds.length > 0 ? 12 : 0 }}>
        <div>
          <div className="stat-label">지금 볼 것</div>
          <div className="stat-value">{open}</div>
        </div>
        <div>
          <div className="stat-label">걸려 있는 금액 (월 환산)</div>
          <div className="stat-value">{won(impactKrw)}</div>
          <div className="stat-sub">추정입니다. 근거는 각 항목에 적혀 있습니다.</div>
        </div>
      </div>

      {kinds.length > 0 && (
        <div className="row" style={{ gap: 6 }}>
          {kinds.map((k) => (
            <button
              key={k.kind}
              className={active === k.kind ? 'sm' : 'ghost sm'}
              onClick={() => onPick(k.kind)}
              title={k.kind}
            >
              {k.label} {k.count}
              {k.impactKrw > 0 && <span className="muted"> · {won(k.impactKrw)}</span>}
            </button>
          ))}
        </div>
      )}
    </div>
  )
}

function ScanResult({ result, onClose }: { result: AttentionScanResult; onClose: () => void }) {
  return (
    <div className="card">
      <div className="row-between">
        <div style={{ fontSize: 12.5 }}>
          새로 {result.opened}건 · 갱신 {result.touched}건 · 묶임 {result.folded}건 ·
          {' '}자동으로 닫힘 {result.autoClosed}건
        </div>
        <button className="ghost sm" onClick={onClose}>닫기</button>
      </div>
      {result.notes.map((note, i) => (
        <div key={i} className="muted" style={{ fontSize: 11.5, marginTop: 4 }}>· {note}</div>
      ))}
    </div>
  )
}

const SEVERITY: Record<string, { cls: string; label: string }> = {
  Critical: { cls: 'badge-danger', label: '지금' },
  Warn: { cls: 'badge-warn', label: '곧' },
  Info: { cls: 'badge-dim', label: '참고' },
}

function Row({
  item, selectable, checked, onToggle, onChanged,
}: {
  item: AttentionItem
  selectable: boolean
  checked: boolean
  onToggle: () => void
  onChanged: () => void
}) {
  const snooze = useMutation({ mutationFn: () => api.attentionSnooze(item.id, 7), onSuccess: onChanged })
  const done = useMutation({ mutationFn: () => api.attentionDone(item.id), onSuccess: onChanged })
  const dismiss = useMutation({ mutationFn: () => api.attentionDismiss(item.id), onSuccess: onChanged })

  const severity = SEVERITY[item.severity] ?? SEVERITY.Info
  const link = subjectLink(item)

  return (
    <tr style={{ opacity: item.state === 'Open' || item.state === 'Snoozed' ? 1 : 0.55 }}>
      {selectable && (
        <td><input type="checkbox" checked={checked} onChange={onToggle} /></td>
      )}
      <td><span className={`badge ${severity.cls}`}>{severity.label}</span></td>
      <td style={{ maxWidth: 460 }}>
        <div style={{ fontSize: 12.5 }}>
          {link ? <a href={link}>{item.title}</a> : item.title}
        </div>
        {/* 근거를 항상 같이 보여준다 — 근거 없는 숫자로 사람의 하루를 정하지 않는다 */}
        <div className="muted" style={{ fontSize: 11.5, marginTop: 2 }}>{item.impactBasis}</div>
        {item.seenCount > 1 && (
          <div className="muted" style={{ fontSize: 11 }}>{item.seenCount}번째 확인됨</div>
        )}
        {item.state !== 'Open' && item.state !== 'Snoozed' && item.resolutionNote && (
          <div className="muted" style={{ fontSize: 11 }}>
            {item.resolvedBy === 'auto' ? '자동' : '직접'} · {item.resolutionNote}
          </div>
        )}
      </td>
      <td><span className="badge badge-dim">{item.kindLabel}</span></td>
      <td style={{ fontWeight: item.impactKrw > 0 ? 600 : 400 }}>
        {item.impactKrw > 0 ? won(item.impactKrw) : <span className="muted">—</span>}
      </td>
      <td className="muted" style={{ fontSize: 11.5 }}>
        {shortDate(item.lastSeenAt)}
        {item.state === 'Snoozed' && item.snoozeUntil && (
          <div>{shortDate(item.snoozeUntil)}까지 연기</div>
        )}
      </td>
      {selectable && (
        <td>
          <div className="row" style={{ gap: 5 }}>
            <button className="ghost sm" onClick={() => snooze.mutate()} disabled={snooze.isPending}>
              7일 뒤
            </button>
            <button className="sm" onClick={() => done.mutate()} disabled={done.isPending}>완료</button>
            <button className="ghost sm" onClick={() => dismiss.mutate()} disabled={dismiss.isPending}>
              무시
            </button>
          </div>
        </td>
      )}
    </tr>
  )
}

/** 대상으로 바로 갈 수 있으면 링크를 건다 — 목록에서 화면을 네 개 돌아다니게 하면 안 본다. */
function subjectLink(item: AttentionItem): string | null {
  switch (item.subjectType) {
    case 'product': return `/products/${item.subjectId}`
    case 'order': return '/orders'
    case 'listing': return '/listings'
    default: return null
  }
}
