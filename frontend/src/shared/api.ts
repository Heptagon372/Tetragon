// ── API 타입 (백엔드 응답과 1:1) ─────────────────────────────────────────

export type ProductStatus =
  | 'Draft' | 'Normalized' | 'Enriched' | 'Priced'
  | 'Ready' | 'Blocked' | 'Listed' | 'Failed'

export interface Money { amount: number; currency: string }

export interface ProductSummary {
  id: string
  name: string
  originalName?: string
  status: ProductStatus
  supplier: string
  sourceUrl: string
  mainImage?: string
  imageCount: number
  basePrice: Money
  salePrice?: number
  variantCount: number
  totalStock: number
  createdAt: string
  updatedAt: string
}

export interface PriceStep {
  ruleCode: string
  description: string
  before: number
  beforeCurrency: string
  after: number
}

export interface PriceCalculation {
  variantId: string
  sourceCost: Money
  finalPrice: number
  exchangeRate: number
  steps: PriceStep[]
  calculatedAt: string
}

export interface ComplianceHit {
  keyword: string
  field: string
  severity: 'Warn' | 'Block'
  reason?: string
}

export interface ProductDetail extends Omit<ProductSummary, 'mainImage' | 'imageCount' | 'originalName'> {
  names: Record<string, string>
  description: string
  source: { supplierCode: string; sourceProductId: string; url: string }
  sourceCategory: string[]
  images: string[]
  attributes: Record<string, string>
  logistics: ProductLogistics
  optionGroups: {
    name: string
    originalName: string
    values: { id: string; name: string; originalName: string; imageUrl?: string }[]
  }[]
  variants: {
    id: string
    sku: string
    options: Record<string, string>
    sourcePrice: Money
    calculatedPrice?: { amount: number }
    stock: number
  }[]
  priceCalculations: PriceCalculation[]
  compliance?: { verdict: 'Pass' | 'Warn' | 'Block'; hits: ComplianceHit[]; checkedAt: string }
  listings: Listing[]
}

export interface Job {
  id: string
  url: string
  supplierCode?: string
  productId?: string
  stage: string
  stageIndex: number
  state: 'Pending' | 'Running' | 'Succeeded' | 'Failed' | 'DeadLettered' | 'Blocked'
  attempts: number
  lastError?: string
  createdAt: string
  updatedAt: string
}

export interface Listing {
  id: string
  productId: string
  marketCode: string
  status: 'Pending' | 'Registered' | 'Failed' | 'Suspended'
  marketItemId?: string
  listedPrice?: number
  lastError?: string
  syncLogs: { action: string; success: boolean; message?: string; at: string }[]
  createdAt: string
  updatedAt: string
}

export interface PriceRuleSpec {
  ruleCode: string
  displayName: string
  parameters: { key: string; label: string; defaultValue: number }[]
}

export interface PricingPolicy {
  id: string
  name: string
  isDefault: boolean
  rules: { ruleCode: string; order: number; parameters: Record<string, number> }[]
  createdAt: string
}

export interface PluginInfo {
  code: string
  displayName: string
  version: string
  isLive: boolean
  isAvailable: boolean
  capabilities?: string[]
}

export interface DashboardSummary {
  products: { total: number; byStatus: Record<string, number> }
  pipeline: {
    running: number; succeeded: number; failed: number; blocked: number
    byStage: Record<string, number>
    todayCollected: number
  }
  listings: {
    total: number; registered: number; failed: number; suspended: number
    byMarket: Record<string, { total: number; registered: number }>
  }
  orders: { total: number; revenue: number; byStatus: Record<string, number> }
}

export interface Order {
  id: string
  marketCode: string
  marketOrderId: string
  productName?: string
  optionName?: string
  quantity: number
  paidAmount: number
  ordererName?: string
  status: string
  trackingNo?: string
  orderedAt: string
}

export interface PipelineEvent {
  jobId: string
  stage: string
  state: string
  productId?: string
  message?: string
  at: string
}

export interface CategoryCrawlerInfo {
  supplierCode: string
  displayName: string
  isLive: boolean
  isAvailable: boolean
}

export interface SupplierCategory {
  code: string
  name: string
  parentCode?: string
  hasChildren: boolean
  fullPath?: string
  /** false면 이 카테고리로는 바로 조회할 수 없다 (도매꾹 대분류 등) — 하위를 선택해야 한다. */
  isSelectable: boolean
}

export interface CrawledProductRef {
  sourceProductId: string
  url: string
  title?: string
  price?: number
  currency?: string
  thumbnailUrl?: string
}

export interface CategoryJob {
  id: string
  supplierCode: string
  categoryCode?: string
  categoryName?: string
  keyword?: string
  state: 'Pending' | 'Crawling' | 'Completed' | 'Failed'
  maxProducts: number
  pagesCrawled: number
  foundCount: number
  queuedCount: number
  skippedCount: number
  lastError?: string
  createdAt: string
  updatedAt: string
}

export interface ProductLogistics {
  supplierName?: string
  supplierPhone?: string
  /** 마켓 주소록에 실제로 등록되는 출고지 (우편번호와 짝이 맞는 주소) */
  outboundAddress?: string
  outboundZipcode?: string
  /** 공급사 사업장 주소 — 출고지와 다를 수 있다 */
  supplierBusinessAddress?: string
  /** 출고지 우편번호가 없어 반품지 주소를 출고지로 쓴 경우 true */
  outboundUsesReturnAddress: boolean
  returnAddress?: string
  returnZipcode?: string
  returnPhone?: string
  returnFee?: number
  deliveryFee?: number
  jejuExtraFee?: number
  islandExtraFee?: number
  minOrderQty?: number
  isOverseasPurchase: boolean
  averageOutboundDays?: number
  outboundShippingDays: number
  detailImagesAllowed: boolean
  hasDetailHtml: boolean
}

/** 링크 미리보기 — 저장하기 전에 "쿠팡에 뭐가 올라가는지" 확인용 */
export interface LinkPreview {
  url: string
  supplierCode: string
  supplierDisplayName: string
  sourceProductId: string
  name: string
  imageUrls: string[]
  optionGroups: { name: string; values: string[] }[]
  /** 실제로 팔리는 옵션 조합 — 조합마다 공급가·재고가 다르다 */
  variants: {
    variantId: string
    options: Record<string, string>
    sourceCost: Money
    salePrice?: Money | null
    stock: number
  }[]
  variantCount: number
  totalStock: number
  sourceCost: Money
  salePrice: Money
  marketFeePct: number
  policyName?: string
  margin?: {
    level: 'Loss' | 'Thin' | 'Healthy'
    message: string
    salePrice: number
    costKrw: number
    supplierShippingFee: number
    marketFee: number
    netRevenue: number
    profit: number
    marginPct: number
    breakEvenPrice: number
  } | null
  logistics: ProductLogistics
  searchKeywords?: string
  existingProductId?: string | null
  existingStatus?: string | null
  warnings: string[]
}

export type QuickListOutcome = 'Queued' | 'ExistingProduct' | 'NeedsAttention' | 'Rejected'

export interface QuickListEntry {
  url: string
  supplierCode?: string
  jobId?: string | null
  productId?: string | null
  outcome: QuickListOutcome
  message: string
}

export interface QuickListResponse {
  markets: string[]
  queued: number
  reused: number
  entries: QuickListEntry[]
}

// ── CS 자동화 ──────────────────────────────────────────────────────────

export interface AutomationPolicy {
  enabled: boolean
  /** 켜져 있으면 발주 직전까지만 하고 실제 결제는 하지 않는다 */
  dryRun: boolean
  autoCollectOrders: boolean
  autoPurchase: boolean
  autoUploadTracking: boolean
  autoCollectCs: boolean
  maxOrderAmount: number
  dailyLimit: number
  consecutiveFailureLimit: number
  intervalMinutes: number
  consecutiveFailures: number
  lastRunAt?: string | null
  lastRunSummary?: string | null
  /** 값이 있으면 자동으로 멈춘 상태 — 사람이 확인하고 재개해야 한다 */
  haltedReason?: string | null
  spentToday: number
  spentDate: string
  canRun: boolean
}

export interface AutomationRun {
  dryRun: boolean
  startedAt: string
  finishedAt?: string | null
  summary: string
  ordersCollected: number
  ordersLinked: number
  purchased: number
  purchasedAmount: number
  trackingUploaded: number
  csCollected: number
  csAutoCancelled: number
  wouldPurchase: string[]
  purchaseSkipped: string[]
  trackingSkipped: string[]
  /** 그 마켓이 원래 지원하지 않는 기능 — 오류가 아니다 */
  unsupported: string[]
  errors: string[]
  haltedReason?: string | null
  skipReason?: string | null
}

export interface CsTicket {
  id: string
  marketCode: string
  marketOrderId: string
  marketTicketId: string
  kind: 'Cancel' | 'Return' | 'Exchange'
  status: 'Open' | 'Resolved' | 'Dismissed'
  productName?: string
  reason?: string
  quantity: number
  requestedAt: string
  supplierActionRequired: boolean
  supplierActionDone: boolean
  handledNote?: string | null
  handledAt?: string | null
  needsAttention: boolean
  orderId?: string | null
  order?: {
    status: string
    supplierOrderNo?: string | null
    supplierUrl?: string | null
    supplierPaid?: number | null
    trackingNo?: string | null
  } | null
}

export interface DetailHtmlResponse {
  allowed: boolean
  html?: string | null
  images: string[]
  reason?: string | null
}

export interface PurchaseOrderSheet {
  orderId: string
  marketCode: string
  marketOrderId: string
  status: string
  productName?: string
  optionName?: string
  quantity: number
  supplierCode?: string
  supplierName?: string
  supplierPhone?: string
  supplierUrl?: string
  supplierUnitCost?: number
  minOrderQty?: number
  receiverName?: string
  receiverPhone?: string
  receiverZipcode?: string
  receiverAddress?: string
  deliveryMessage?: string
  paidAmount: number
  estimatedCost?: number
  estimatedMargin?: number
  supplierOrderNo?: string
  shippingLine: string
  warnings: string[]
}

export interface Wallet {
  id: string
  currency: string
  balance: number
  reserved: number
  available: number
  lowBalanceThreshold: number
  isLow: boolean
  updatedAt: string
}

export interface WalletTransaction {
  id: string
  type: 'Deposit' | 'Withdraw' | 'Reserve' | 'Purchase' | 'Release'
  typeLabel: string
  amount: number
  balanceAfter: number
  orderId?: string
  memo?: string
  occurredAt: string
}

export interface PurchasePreflight {
  orderId: string
  canAutoOrder: boolean
  blockers: string[]
  warnings: string[]
  estimatedAmount: number
  walletAvailable: number
  walletShortfall: number
  capabilityReason?: string
  capabilityHowToEnable?: string
  supplierUrl?: string
}

export interface SupplierShippingStatus {
  supplierName: string
  outboundAddress?: string
  returnAddress?: string
  returnZipcode?: string
  returnPhone?: string
  outboundCode?: string
  returnCenterCode?: string
  outboundReady: boolean
  returnReady: boolean
  registerLine: string
  formPlaceName: string
  formAddress: string
  formAddressDetail: string
  formPhone: string
}

export interface ShippingPlaceAudit {
  marketCode: string
  supported: boolean
  message?: string
  missingReturnCount: number
  suppliers: SupplierShippingStatus[]
}

export interface StockAuditEntry {
  productId: string
  productName: string
  supplierCode: string
  supplierName?: string
  sourceUrl: string
  status: string
  previousStock: number
  currentStock: number
  previousCost: number
  currentCost: number
  costChangePct: number
  error?: string
}

export interface StockAuditResult {
  checkedCount: number
  lowStockThreshold: number
  soldOut: StockAuditEntry[]
  lowStock: StockAuditEntry[]
  priceChanged: StockAuditEntry[]
  failed: StockAuditEntry[]
  riskCount: number
}

export interface ProductRef {
  productId: string
  productName: string
  supplierCode: string
  supplierName?: string
  status: string
  sourceUrl: string
}

export interface DuplicateGroup {
  kind: string
  key: string
  reason: string
  products: ProductRef[]
}

export interface ItemWinnerRisk {
  productId: string
  productName: string
  supplierName?: string
  sourceUrl: string
  level: string
  signals: string[]
}

export interface DuplicateAuditResult {
  scannedCount: number
  duplicates: DuplicateGroup[]
  itemWinnerRisks: ItemWinnerRisk[]
}

export interface ExcelTemplateInfo {
  marketCode: string
  displayName: string
  uploadGuide: string
  columnCount: number
  requiredColumns: string[]
}

// ── HTTP 클라이언트 ──────────────────────────────────────────────────────

// ── 주의 원장 (확장 08 §1) ────────────────────────────────────────────────

export type AttentionSeverity = 'Info' | 'Warn' | 'Critical'
export type AttentionState = 'Open' | 'Snoozed' | 'Done' | 'Dismissed'

export interface AttentionItem {
  id: string
  kind: string
  kindLabel: string
  subjectType: string
  subjectId: string
  severity: AttentionSeverity
  /** 이걸 처리하면 얼마가 걸려 있나 (월 환산, 원). 목록 정렬의 기준. */
  impactKrw: number
  /** 그 숫자의 근거 문장. 화면에 항상 같이 보여준다 — 근거 없는 숫자로 줄을 세우지 않는다. */
  impactBasis: string
  title: string
  detail: Record<string, string>
  suggestedAction?: string
  state: AttentionState
  snoozeUntil?: string
  firstSeenAt: string
  lastSeenAt: string
  seenCount: number
  resolvedAt?: string
  resolvedBy?: 'human' | 'auto'
  resolutionNote?: string
}

export interface AttentionSummary {
  open: number
  impactKrw: number
  kinds: { kind: string; label: string; count: number; impactKrw: number }[]
}

export interface AttentionScanResult {
  opened: number
  touched: number
  folded: number
  autoClosed: number
  /** 상한에 걸려 싣지 못한 것 — 조용히 자르지 않고 그대로 보여준다. */
  notes: string[]
}

// ── CSV 임포트 프레임 (확장 08 §3) ────────────────────────────────────────

export interface ImportFieldSpec {
  name: string
  label: string
  type: 'Text' | 'Integer' | 'Decimal' | 'Date'
  required: boolean
}

export interface ImportInspection {
  channel: string
  purpose: string
  fingerprint: string
  encoding: string
  rowCount: number
  /** false면 columnMap은 확정이 아니라 제안이다. 사람이 확정해야 파싱된다. */
  profileExists: boolean
  header: string[]
  normalizedHeader: string[]
  fields: ImportFieldSpec[]
  columnMap: Record<string, string>
  missingRequired: string[]
}

export interface ImportProfile {
  id: string
  channel: string
  purpose: string
  fingerprint: string
  columnMap: Record<string, string>
  sampleHeader: string[]
  createdBy: string
  createdAt: string
}

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await fetch(`/api/v1${path}`, {
    ...init,
    headers: { 'Content-Type': 'application/json', ...init?.headers },
  })
  if (!response.ok) {
    const text = await response.text()
    let message = text
    try {
      message = JSON.parse(text).error ?? text
    } catch { /* 원문 유지 */ }
    throw new Error(message || `요청 실패 (HTTP ${response.status})`)
  }
  if (response.status === 204) return undefined as T
  return response.json() as Promise<T>
}

export const api = {
  // 수집
  collect: (urls: string[], pricingPolicyId?: string) =>
    request<{ jobIds: string[]; accepted: number; rejected: string[] }>('/products/collect', {
      method: 'POST',
      body: JSON.stringify({ urls, pricingPolicyId }),
    }),

  // 빠른 등록 — 링크 → 마켓
  previewLink: (url: string, pricingPolicyId?: string) =>
    request<LinkPreview>('/products/preview', {
      method: 'POST',
      body: JSON.stringify({ url, pricingPolicyId }),
    }),
  quickList: (body: {
    urls: string[]
    marketCodes: string[]
    pricingPolicyId?: string
    reuseExisting?: boolean
  }) =>
    request<QuickListResponse>('/products/quick-list', {
      method: 'POST',
      body: JSON.stringify(body),
    }),

  // 상품
  products: (params: { status?: string; keyword?: string; page?: number; pageSize?: number } = {}) => {
    const query = new URLSearchParams()
    if (params.status) query.set('status', params.status)
    if (params.keyword) query.set('keyword', params.keyword)
    query.set('page', String(params.page ?? 1))
    query.set('pageSize', String(params.pageSize ?? 50))
    return request<{ items: ProductSummary[]; total: number; page: number }>(`/products?${query}`)
  },
  product: (id: string) => request<ProductDetail>(`/products/${id}`),
  productDetailHtml: (id: string) => request<DetailHtmlResponse>(`/products/${id}/detail-html`),
  editProduct: (id: string, body: { name?: string; description?: string }) =>
    request<ProductSummary>(`/products/${id}`, { method: 'PATCH', body: JSON.stringify(body) }),
  resumeProduct: (id: string) =>
    request<{ resumed: boolean }>(`/products/${id}/resume`, { method: 'POST' }),

  // Job
  jobs: (limit = 100) => request<{ items: Job[]; stages: string[] }>(`/jobs?limit=${limit}`),
  retryJob: (id: string) => request<Job>(`/jobs/${id}/retry`, { method: 'POST' }),

  // 가격
  priceRules: () => request<PriceRuleSpec[]>('/pricing-policies/rules'),
  policies: () => request<PricingPolicy[]>('/pricing-policies'),
  createPolicy: (body: unknown) =>
    request<PricingPolicy>('/pricing-policies', { method: 'POST', body: JSON.stringify(body) }),
  updatePolicy: (id: string, body: unknown) =>
    request<PricingPolicy>(`/pricing-policies/${id}`, { method: 'PUT', body: JSON.stringify(body) }),
  simulate: (policyId: string, productId: string) =>
    request<{ calculations: PriceCalculation[] }>(`/pricing-policies/${policyId}/simulate`, {
      method: 'POST',
      body: JSON.stringify({ productId }),
    }),

  // 등록
  listings: (params: { market?: string; status?: string } = {}) => {
    const query = new URLSearchParams()
    if (params.market) query.set('market', params.market)
    if (params.status) query.set('status', params.status)
    return request<{ items: Listing[] }>(`/listings?${query}`)
  },
  requestListing: (productIds: string[], marketCodes: string[]) =>
    request<{ requested: number; skipped: { id: string; reason: string }[] }>('/listings', {
      method: 'POST',
      body: JSON.stringify({ productIds, marketCodes }),
    }),
  checkInventory: (productIds: string[]) =>
    request<{ requested: number }>('/inventory/check', {
      method: 'POST',
      body: JSON.stringify(productIds),
    }),

  // 컴플라이언스
  complianceRules: () =>
    request<{ id: string; keyword: string; severity: string; reason?: string; marketCode?: string }[]>(
      '/compliance/rules'),
  addComplianceRule: (body: { keyword: string; severity: string; reason?: string }) =>
    request<{ id: string }>('/compliance/rules', { method: 'POST', body: JSON.stringify(body) }),
  deleteComplianceRule: (id: string) =>
    request<void>(`/compliance/rules/${id}`, { method: 'DELETE' }),

  // 주문
  orders: (status?: string) =>
    request<{ items: Order[] }>(`/orders${status ? `?status=${status}` : ''}`),
  fetchOrders: (market?: string) =>
    request<{ imported: number; errors: unknown[] }>(
      `/orders/fetch${market ? `?market=${market}` : ''}`, { method: 'POST' }),
  purchaseOrder: (id: string) => request<Order>(`/orders/${id}/purchase`, { method: 'POST' }),
  setTracking: (id: string, trackingNo: string) =>
    request<Order>(`/orders/${id}/tracking`, { method: 'POST', body: JSON.stringify({ trackingNo }) }),

  // 카테고리 수집
  categoryCrawlers: () => request<CategoryCrawlerInfo[]>('/categories/suppliers'),
  categories: (supplierCode: string, parent?: string) =>
    request<SupplierCategory[]>(`/categories/${supplierCode}${parent ? `?parent=${parent}` : ''}`),
  previewCategory: (supplierCode: string, body: {
    categoryCode?: string; keyword?: string; pageSize?: number
  }) =>
    request<{ items: CrawledProductRef[]; totalCount: number; hasMore: boolean }>(
      `/categories/${supplierCode}/preview`, { method: 'POST', body: JSON.stringify(body) }),
  collectCategory: (supplierCode: string, body: {
    categoryCode?: string; categoryName?: string; keyword?: string
    maxProducts?: number; pricingPolicyId?: string
  }) =>
    request<CategoryJob>(`/categories/${supplierCode}/collect`,
      { method: 'POST', body: JSON.stringify(body) }),
  categoryJobs: (limit = 50) =>
    request<{ items: CategoryJob[] }>(`/categories/jobs?limit=${limit}`),

  // 위탁판매 발주
  purchaseSheet: (orderId: string) =>
    request<PurchaseOrderSheet>(`/orders/${orderId}/purchase-sheet`),
  markPurchased: (orderId: string, body: { supplierOrderNo?: string; supplierPaidAmount?: number }) =>
    request(`/orders/${orderId}/purchase`, { method: 'POST', body: JSON.stringify(body) }),

  // 금고
  wallet: () => request<Wallet>('/wallet'),
  walletTransactions: (limit = 50) =>
    request<{ items: WalletTransaction[] }>(`/wallet/transactions?limit=${limit}`),
  walletDeposit: (amount: number, memo?: string) =>
    request<Wallet>('/wallet/deposit', { method: 'POST', body: JSON.stringify({ amount, memo }) }),
  walletWithdraw: (amount: number, memo?: string) =>
    request<Wallet>('/wallet/withdraw', { method: 'POST', body: JSON.stringify({ amount, memo }) }),
  walletThreshold: (threshold: number) =>
    request<Wallet>('/wallet/threshold', { method: 'PUT', body: JSON.stringify({ threshold }) }),

  // 자동 발주
  purchasePreflight: (orderId: string) =>
    request<PurchasePreflight>(`/orders/${orderId}/purchase-preflight`),
  autoPurchase: (orderId: string) =>
    request<{ success: boolean; supplierOrderNo?: string; paidAmount?: number; walletAvailableAfter?: number }>(
      `/orders/${orderId}/auto-purchase`, { method: 'POST' }),

  // 배송지 현황
  shippingPlaces: (marketCode: string) =>
    request<ShippingPlaceAudit>(`/settings/shipping-places/${marketCode}`),

  // 점검
  stockAudit: (limit = 50, threshold = 10, listedOnly = false) =>
    request<StockAuditResult>(
      `/audit/stock?limit=${limit}&threshold=${threshold}&listedOnly=${listedOnly}`, { method: 'POST' }),
  duplicateAudit: (limit = 1000) =>
    request<DuplicateAuditResult>(`/audit/duplicates?limit=${limit}`),

  // 엑셀
  excelTemplates: () => request<ExcelTemplateInfo[]>('/excel/templates'),

  // CS 자동화
  automation: () => request<AutomationPolicy>('/cs/automation'),
  saveAutomation: (body: Partial<AutomationPolicy>) =>
    request<AutomationPolicy>('/cs/automation', { method: 'PUT', body: JSON.stringify(body) }),
  resumeAutomation: () =>
    request<AutomationPolicy>('/cs/automation/resume', { method: 'POST' }),
  runAutomation: () =>
    request<AutomationRun>('/cs/automation/run', { method: 'POST' }),
  csTickets: (params: { status?: string; kind?: string } = {}) => {
    const query = new URLSearchParams()
    if (params.status) query.set('status', params.status)
    if (params.kind) query.set('kind', params.kind)
    return request<{ items: CsTicket[]; needsAttention: number }>(`/cs/tickets?${query}`)
  },
  csSupplierDone: (id: string, note?: string) =>
    request<CsTicket>(`/cs/tickets/${id}/supplier-done`, {
      method: 'POST', body: JSON.stringify({ note }),
    }),
  csResolve: (id: string, note?: string) =>
    request<CsTicket>(`/cs/tickets/${id}/resolve`, {
      method: 'POST', body: JSON.stringify({ note }),
    }),
  csDismiss: (id: string, note?: string) =>
    request<CsTicket>(`/cs/tickets/${id}/dismiss`, {
      method: 'POST', body: JSON.stringify({ note }),
    }),

  // 주의 원장 (확장 08 §1)
  attention: (params: { state?: string; kind?: string; limit?: number } = {}) => {
    const query = new URLSearchParams()
    if (params.state) query.set('state', params.state)
    if (params.kind) query.set('kind', params.kind)
    if (params.limit) query.set('limit', String(params.limit))
    return request<{ items: AttentionItem[] }>(`/attention?${query}`)
  },
  attentionSummary: () => request<AttentionSummary>('/attention/summary'),
  attentionSnooze: (id: string, untilDays: number) =>
    request<{ snoozed: boolean }>(`/attention/${id}/snooze`, {
      method: 'POST', body: JSON.stringify({ untilDays }),
    }),
  attentionDone: (id: string, note?: string) =>
    request<{ done: boolean }>(`/attention/${id}/done`, {
      method: 'POST', body: JSON.stringify({ note }),
    }),
  attentionDismiss: (id: string, note?: string) =>
    request<{ dismissed: boolean }>(`/attention/${id}/dismiss`, {
      method: 'POST', body: JSON.stringify({ note }),
    }),
  attentionBulk: (ids: string[], action: 'snooze' | 'done' | 'dismiss', untilDays?: number, note?: string) =>
    request<{ applied: number }>('/attention/bulk', {
      method: 'POST', body: JSON.stringify({ ids, action, untilDays, note }),
    }),
  attentionScan: () => request<AttentionScanResult>('/attention/scan', { method: 'POST' }),

  // CSV 임포트 프레임 (확장 08 §3)
  importFields: (purpose: string) =>
    request<{ purpose: string; fields: ImportFieldSpec[] }>(`/imports/fields/${purpose}`),
  importProfiles: (channel?: string, purpose?: string) => {
    const query = new URLSearchParams()
    if (channel) query.set('channel', channel)
    if (purpose) query.set('purpose', purpose)
    return request<{ items: ImportProfile[] }>(`/imports/profiles?${query}`)
  },
  saveImportProfile: (body: {
    channel: string
    purpose: string
    fingerprint: string
    columnMap: Record<string, string>
    sampleHeader?: string[]
  }) => request<ImportProfile>('/imports/profiles', { method: 'PUT', body: JSON.stringify(body) }),
  deleteImportProfile: (id: string) =>
    request<void>(`/imports/profiles/${id}`, { method: 'DELETE' }),

  // 기타
  dashboard: () => request<DashboardSummary>('/dashboard/summary'),
  plugins: () => request<{
    suppliers: PluginInfo[]
    marketplaces: PluginInfo[]
    aiProviders: PluginInfo[]
  }>('/plugins'),
  credentials: () => request<Record<string, string[]>>('/settings/credentials'),
  saveCredentials: (scope: string, secrets: Record<string, string>) =>
    request<{ saved: boolean }>('/settings/credentials', {
      method: 'PUT',
      body: JSON.stringify({ scope, secrets }),
    }),
}

/** 파일 다운로드 — 엑셀은 JSON이 아니라 blob이라 별도 처리한다. */
export async function downloadFile(
  path: string,
  init?: RequestInit,
): Promise<{ ok: true } | { ok: false; error: string }> {
  const response = await fetch(`/api/v1${path}`, {
    ...init,
    headers: { 'Content-Type': 'application/json', ...init?.headers },
  })
  if (!response.ok) {
    const text = await response.text()
    try {
      return { ok: false, error: JSON.parse(text).error ?? text }
    } catch {
      return { ok: false, error: text || `다운로드 실패 (HTTP ${response.status})` }
    }
  }

  // Content-Disposition에서 서버가 지정한 파일명을 꺼낸다
  const disposition = response.headers.get('Content-Disposition') ?? ''
  const match = /filename\*?=(?:UTF-8'')?"?([^";]+)"?/i.exec(disposition)
  const fileName = match ? decodeURIComponent(match[1]) : 'tetragon.xlsx'

  const blob = await response.blob()
  const url = URL.createObjectURL(blob)
  const anchor = document.createElement('a')
  anchor.href = url
  anchor.download = fileName
  document.body.appendChild(anchor)
  anchor.click()
  anchor.remove()
  URL.revokeObjectURL(url)
  return { ok: true }
}

/** 엑셀 업로드 (multipart). */
export async function uploadExcel<T>(path: string, file: File): Promise<T> {
  const form = new FormData()
  form.append('file', file)
  const response = await fetch(`/api/v1${path}`, { method: 'POST', body: form })
  const text = await response.text()
  if (!response.ok) {
    try {
      throw new Error(JSON.parse(text).error ?? text)
    } catch (e) {
      throw e instanceof Error ? e : new Error(text)
    }
  }
  return JSON.parse(text) as T
}

/** SSE 파이프라인 이벤트 구독. 반환값 호출로 해제. */
export function subscribePipeline(onEvent: (e: PipelineEvent) => void): () => void {
  const source = new EventSource('/api/v1/stream')
  source.onmessage = (message) => {
    try {
      onEvent(JSON.parse(message.data) as PipelineEvent)
    } catch { /* 파싱 실패 무시 */ }
  }
  return () => source.close()
}
