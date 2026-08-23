import { useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api } from '../../shared/api'
import { Empty, ErrorBox, Page, shortDate, won } from '../../shared/ui'

/**
 * 금고 — 공급처 발주에 쓸 운전자금.
 *
 * 위탁판매는 마켓 정산금이 들어오기 전에 공급처에 먼저 돈을 내야 한다.
 * 그 사이를 메우는 자금을 여기에 넣어두고, 발주할 때마다 차감한다.
 */
export default function WalletPage() {
  const queryClient = useQueryClient()
  const [amount, setAmount] = useState('')
  const [memo, setMemo] = useState('')
  const [threshold, setThreshold] = useState('')

  const wallet = useQuery({ queryKey: ['wallet'], queryFn: api.wallet, refetchInterval: 5000 })
  const history = useQuery({ queryKey: ['walletTx'], queryFn: () => api.walletTransactions(60) })

  const refresh = () => {
    queryClient.invalidateQueries({ queryKey: ['wallet'] })
    queryClient.invalidateQueries({ queryKey: ['walletTx'] })
  }

  const deposit = useMutation({
    mutationFn: () => api.walletDeposit(Number(amount), memo || undefined),
    onSuccess: () => { setAmount(''); setMemo(''); refresh() },
  })

  const withdraw = useMutation({
    mutationFn: () => api.walletWithdraw(Number(amount), memo || undefined),
    onSuccess: () => { setAmount(''); setMemo(''); refresh() },
  })

  const saveThreshold = useMutation({
    mutationFn: () => api.walletThreshold(Number(threshold)),
    onSuccess: () => { setThreshold(''); refresh() },
  })

  const w = wallet.data
  const numericAmount = Number(amount)
  const canSubmit = numericAmount > 0 && !deposit.isPending && !withdraw.isPending

  return (
    <Page
      title="금고"
      desc="공급처 발주에 쓸 자금입니다. 잔액이 모자라면 발주가 막힙니다."
    >
      <ErrorBox error={wallet.error} />

      {w && (
        <div className="grid grid-2">
          <div className="card">
            <h2 className="card-title">잔액</h2>
            <div style={{ display: 'flex', gap: 28, alignItems: 'flex-end', marginBottom: 6 }}>
              <div>
                <label>사용 가능</label>
                <div style={{
                  fontSize: 30, fontWeight: 700,
                  color: w.isLow ? 'var(--danger)' : 'var(--accent)',
                }}>
                  {won(w.available)}
                </div>
              </div>
              <div>
                <label>총 잔액</label>
                <div style={{ fontSize: 17 }}>{won(w.balance)}</div>
              </div>
              <div>
                <label>발주 예약</label>
                <div style={{ fontSize: 17 }} className="muted">{won(w.reserved)}</div>
              </div>
            </div>

            {w.isLow && (
              <div className="error-box" style={{ marginTop: 12 }}>
                잔액이 경고선({won(w.lowBalanceThreshold)}) 아래입니다 — 충전하지 않으면 발주가 막힐 수 있습니다.
              </div>
            )}

            <p className="muted" style={{ fontSize: 12, marginTop: 14, marginBottom: 0 }}>
              <strong>발주 예약</strong>은 발주가 진행 중이라 묶여 있는 금액입니다.
              실제로 쓸 수 있는 돈은 <strong>총 잔액 − 예약</strong>입니다.
            </p>
          </div>

          <div className="card">
            <h2 className="card-title">충전 · 출금</h2>
            <div className="field">
              <label>금액 (원)</label>
              <input
                type="number"
                value={amount}
                onChange={(e) => setAmount(e.target.value)}
                placeholder="예: 500000"
              />
            </div>
            <div className="row" style={{ gap: 6, marginBottom: 12 }}>
              {[100_000, 500_000, 1_000_000].map((preset) => (
                <button key={preset} className="ghost sm" onClick={() => setAmount(String(preset))}>
                  +{(preset / 10_000).toLocaleString()}만
                </button>
              ))}
            </div>
            <div className="field">
              <label>메모 (선택)</label>
              <input value={memo} onChange={(e) => setMemo(e.target.value)} placeholder="예: 12월 운영자금" />
            </div>
            <div className="row">
              <button onClick={() => deposit.mutate()} disabled={!canSubmit}>
                {deposit.isPending ? '처리 중…' : '충전'}
              </button>
              <button className="ghost" onClick={() => withdraw.mutate()} disabled={!canSubmit}>
                출금
              </button>
            </div>
            <ErrorBox error={deposit.error} />
            <ErrorBox error={withdraw.error} />

            <div style={{ marginTop: 18, paddingTop: 14, borderTop: '1px solid var(--border)' }}>
              <label>잔액 경고선 — 현재 {won(w.lowBalanceThreshold)}</label>
              <div className="row" style={{ gap: 8 }}>
                <input
                  type="number"
                  value={threshold}
                  onChange={(e) => setThreshold(e.target.value)}
                  placeholder="예: 100000"
                  style={{ flex: 1 }}
                />
                <button
                  className="ghost sm"
                  onClick={() => saveThreshold.mutate()}
                  disabled={!threshold || saveThreshold.isPending}
                >
                  저장
                </button>
              </div>
              <ErrorBox error={saveThreshold.error} />
            </div>
          </div>
        </div>
      )}

      <div className="card">
        <h2 className="card-title">입출금 이력</h2>
        <ErrorBox error={history.error} />
        {history.data?.items.length === 0 && <Empty>아직 거래가 없습니다.</Empty>}
        {history.data && history.data.items.length > 0 && (
          <div className="table-wrap">
            <table>
              <thead>
                <tr>
                  <th>구분</th>
                  <th style={{ textAlign: 'right' }}>금액</th>
                  <th style={{ textAlign: 'right' }}>잔액</th>
                  <th>메모</th>
                  <th>시각</th>
                </tr>
              </thead>
              <tbody>
                {history.data.items.map((t) => (
                  <tr key={t.id}>
                    <td><TxBadge type={t.type} label={t.typeLabel} /></td>
                    <td style={{
                      textAlign: 'right', fontWeight: 600,
                      color: t.amount >= 0 ? 'var(--ok)' : 'var(--danger)',
                    }}>
                      {t.amount >= 0 ? '+' : ''}{won(t.amount)}
                    </td>
                    <td style={{ textAlign: 'right' }} className="muted">{won(t.balanceAfter)}</td>
                    <td className="muted" style={{ fontSize: 12.5, maxWidth: 340 }}>{t.memo ?? ''}</td>
                    <td className="muted" style={{ fontSize: 12 }}>{shortDate(t.occurredAt)}</td>
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

function TxBadge({ type, label }: { type: string; label: string }) {
  const cls = type === 'Deposit' ? 'badge-ok'
    : type === 'Purchase' || type === 'Withdraw' ? 'badge-danger'
    : type === 'Reserve' ? 'badge-warn'
    : 'badge-dim'
  return <span className={`badge ${cls}`}>{label}</span>
}
