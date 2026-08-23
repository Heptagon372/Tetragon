import { useEffect, useState } from 'react'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api, type PriceCalculation, type PricingPolicy } from '../../shared/api'
import { Empty, ErrorBox, Page, won } from '../../shared/ui'

interface RuleDraft {
  ruleCode: string
  order: number
  parameters: Record<string, number>
}

export default function Pricing() {
  const queryClient = useQueryClient()
  const rules = useQuery({ queryKey: ['priceRules'], queryFn: api.priceRules })
  const policies = useQuery({ queryKey: ['policies'], queryFn: api.policies })
  const products = useQuery({ queryKey: ['products', '', ''], queryFn: () => api.products({ pageSize: 30 }) })

  const [editingId, setEditingId] = useState<string | null>(null)
  const [name, setName] = useState('')
  const [isDefault, setIsDefault] = useState(false)
  const [draft, setDraft] = useState<RuleDraft[]>([])
  const [simProductId, setSimProductId] = useState('')
  const [simResult, setSimResult] = useState<PriceCalculation | null>(null)

  // 첫 정책을 자동 로드
  useEffect(() => {
    if (!editingId && policies.data?.length) loadPolicy(policies.data[0])
  }, [policies.data]) // eslint-disable-line react-hooks/exhaustive-deps

  function loadPolicy(policy: PricingPolicy) {
    setEditingId(policy.id)
    setName(policy.name)
    setIsDefault(policy.isDefault)
    setDraft(policy.rules.map((r) => ({ ...r, parameters: { ...r.parameters } })))
    setSimResult(null)
  }

  function newPolicy() {
    setEditingId(null)
    setName('새 정책')
    setIsDefault(false)
    setDraft([])
    setSimResult(null)
  }

  function addRule(ruleCode: string) {
    const spec = rules.data?.find((r) => r.ruleCode === ruleCode)
    if (!spec || draft.some((d) => d.ruleCode === ruleCode)) return
    const parameters: Record<string, number> = {}
    spec.parameters.forEach((p) => { parameters[p.key] = p.defaultValue })
    setDraft([...draft, { ruleCode, order: draft.length + 1, parameters }])
  }

  function move(index: number, delta: number) {
    const next = [...draft]
    const target = index + delta
    if (target < 0 || target >= next.length) return
    ;[next[index], next[target]] = [next[target], next[index]]
    setDraft(next.map((r, i) => ({ ...r, order: i + 1 })))
  }

  const save = useMutation({
    mutationFn: () => {
      const body = { name, isDefault, rules: draft.map((r, i) => ({ ...r, order: i + 1 })) }
      return editingId ? api.updatePolicy(editingId, body) : api.createPolicy(body)
    },
    onSuccess: (policy) => {
      queryClient.invalidateQueries({ queryKey: ['policies'] })
      setEditingId(policy.id)
    },
  })

  const simulate = useMutation({
    mutationFn: () => api.simulate(editingId!, simProductId),
    onSuccess: (data) => setSimResult(data.calculations[0] ?? null),
  })

  return (
    <Page
      title="가격 정책"
      desc="Rule을 순서대로 조합해 판매가를 계산합니다. 각 Rule은 이전 결과를 입력으로 받습니다."
      actions={<button className="ghost sm" onClick={newPolicy}>새 정책</button>}
    >
      <div className="grid grid-2">
        <div className="card">
          <h2 className="card-title">정책 목록</h2>
          {policies.data?.length === 0 && <Empty>정책이 없습니다.</Empty>}
          <table>
            <tbody>
              {policies.data?.map((policy) => (
                <tr key={policy.id} style={{ cursor: 'pointer' }} onClick={() => loadPolicy(policy)}>
                  <td style={{ fontWeight: editingId === policy.id ? 650 : 400 }}>
                    {policy.name}
                    {policy.isDefault && <span className="badge badge-info" style={{ marginLeft: 6 }}>기본</span>}
                  </td>
                  <td className="muted" style={{ textAlign: 'right' }}>{policy.rules.length}개 Rule</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>

        <div className="card">
          <h2 className="card-title">가격 시뮬레이터</h2>
          {!editingId && <div className="muted" style={{ fontSize: 12.5, marginBottom: 10 }}>정책을 저장하면 시뮬레이션할 수 있습니다.</div>}
          <div className="field">
            <label>대상 상품</label>
            <select value={simProductId} onChange={(e) => setSimProductId(e.target.value)}>
              <option value="">선택…</option>
              {products.data?.items.map((p) => (
                <option key={p.id} value={p.id}>{p.name.slice(0, 40)}</option>
              ))}
            </select>
          </div>
          <button
            className="sm"
            onClick={() => simulate.mutate()}
            disabled={!editingId || !simProductId || simulate.isPending}
          >
            {simulate.isPending ? '계산 중…' : '시뮬레이션'}
          </button>
          <ErrorBox error={simulate.error} />

          {simResult && (
            <table className="price-steps" style={{ marginTop: 12 }}>
              <tbody>
                {simResult.steps.map((step, index) => (
                  <tr key={index}>
                    <td style={{ width: '48%' }}>{step.description}</td>
                    <td style={{ textAlign: 'right' }} className="mono">{Math.round(step.before).toLocaleString()}</td>
                    <td style={{ textAlign: 'center', width: 22 }} className="muted">→</td>
                    <td style={{ textAlign: 'right' }} className="mono">{Math.round(step.after).toLocaleString()}</td>
                  </tr>
                ))}
                <tr>
                  <td>최종 판매가</td>
                  <td colSpan={3} style={{ textAlign: 'right' }}>{won(simResult.finalPrice)}</td>
                </tr>
              </tbody>
            </table>
          )}
        </div>
      </div>

      <div className="card">
        <div className="row-between" style={{ marginBottom: 14 }}>
          <h2 className="card-title" style={{ margin: 0 }}>정책 편집</h2>
          <div className="row">
            <label className="row" style={{ margin: 0, gap: 5, cursor: 'pointer' }}>
              <input type="checkbox" checked={isDefault} onChange={(e) => setIsDefault(e.target.checked)} />
              <span style={{ color: 'var(--text)', fontSize: 12.5 }}>기본 정책으로 설정</span>
            </label>
            <button className="sm" onClick={() => save.mutate()} disabled={draft.length === 0 || save.isPending}>
              {save.isPending ? '저장 중…' : editingId ? '수정' : '생성'}
            </button>
          </div>
        </div>

        <ErrorBox error={save.error} />
        {save.isSuccess && <div className="ok-box">정책이 저장되었습니다.</div>}

        <div className="field" style={{ maxWidth: 320 }}>
          <label>정책 이름</label>
          <input value={name} onChange={(e) => setName(e.target.value)} />
        </div>

        <label>Rule 추가</label>
        <div className="row" style={{ marginBottom: 16 }}>
          {rules.data?.map((spec) => (
            <button
              key={spec.ruleCode}
              className="ghost sm"
              onClick={() => addRule(spec.ruleCode)}
              disabled={draft.some((d) => d.ruleCode === spec.ruleCode)}
            >
              + {spec.displayName}
            </button>
          ))}
        </div>

        {draft.length === 0 && <Empty>Rule을 추가하세요. 적용 순서대로 계산됩니다.</Empty>}

        {draft.map((rule, index) => {
          const spec = rules.data?.find((r) => r.ruleCode === rule.ruleCode)
          return (
            <div key={rule.ruleCode} className="card" style={{ background: 'var(--surface-2)', marginBottom: 8 }}>
              <div className="row-between" style={{ marginBottom: spec?.parameters.length ? 10 : 0 }}>
                <div className="row">
                  <span className="badge badge-info">{index + 1}</span>
                  <strong style={{ fontSize: 13 }}>{spec?.displayName ?? rule.ruleCode}</strong>
                  <span className="mono muted">{rule.ruleCode}</span>
                </div>
                <div className="row">
                  <button className="ghost sm" onClick={() => move(index, -1)} disabled={index === 0}>↑</button>
                  <button className="ghost sm" onClick={() => move(index, 1)} disabled={index === draft.length - 1}>↓</button>
                  <button className="danger sm" onClick={() => setDraft(draft.filter((_, i) => i !== index))}>삭제</button>
                </div>
              </div>

              {spec && spec.parameters.length > 0 && (
                <div className="row" style={{ gap: 14 }}>
                  {spec.parameters.map((param) => (
                    <div key={param.key} style={{ width: 150 }}>
                      <label>{param.label}</label>
                      <input
                        type="number"
                        step="any"
                        value={rule.parameters[param.key] ?? param.defaultValue}
                        onChange={(e) => {
                          const next = [...draft]
                          next[index] = {
                            ...rule,
                            parameters: { ...rule.parameters, [param.key]: Number(e.target.value) },
                          }
                          setDraft(next)
                        }}
                      />
                    </div>
                  ))}
                </div>
              )}
            </div>
          )
        })}
      </div>
    </Page>
  )
}
