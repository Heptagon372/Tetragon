import { NavLink, Navigate, Route, Routes } from 'react-router-dom'
import { useQuery } from '@tanstack/react-query'
import { api } from './shared/api'
import Attention from './features/attention/Attention'
import Dashboard from './features/dashboard/Dashboard'
import Collect from './features/collect/Collect'
import QuickList from './features/quick/QuickList'
import Categories from './features/categories/Categories'
import StockScan from './features/stockscan/StockScan'
import Products from './features/products/Products'
import ProductDetail from './features/products/ProductDetail'
import Pricing from './features/pricing/Pricing'
import Listings from './features/listings/Listings'
import Orders from './features/orders/Orders'
import WalletPage from './features/wallet/Wallet'
import Cs from './features/cs/Cs'
import Audit from './features/audit/Audit'
import Settings from './features/settings/Settings'

// '할 일'이 대시보드보다 위에 있는 것은 의도다 (확장 08 §1.6) —
// 유한한 자원은 등록 슬롯이 아니라 운영자의 주의이고, 하루는 여기서 시작해야 한다.
const NAV = [
  { to: '/attention', label: '할 일' },
  { to: '/dashboard', label: '대시보드' },
  { to: '/quick', label: '빠른 등록' },
  { to: '/collect', label: 'URL 수집' },
  { to: '/categories', label: '카테고리 수집' },
  { to: '/stock-scan', label: '품절 스캔' },
  { to: '/products', label: '상품' },
  { to: '/audit', label: '상품 점검' },
  { to: '/pricing', label: '가격 정책' },
  { to: '/listings', label: '등록 현황' },
  { to: '/orders', label: '주문' },
  { to: '/cs', label: 'CS 관리' },
  { to: '/wallet', label: '금고' },
  { to: '/settings', label: '설정' },
]

export default function App() {
  return (
    <div className="app">
      <aside className="sidebar">
        <div className="logo">
          TETRA<span>GON</span>
          <small>위탁판매 자동화</small>
        </div>
        <nav className="nav">
          {NAV.map((item) => (
            <NavLink key={item.to} to={item.to} className={({ isActive }) => (isActive ? 'active' : '')}>
              {item.label}
              {item.to === '/attention' && <AttentionBadge />}
            </NavLink>
          ))}
        </nav>
      </aside>

      <main className="main">
        <Routes>
          <Route path="/" element={<Navigate to="/dashboard" replace />} />
          <Route path="/attention" element={<Attention />} />
          <Route path="/dashboard" element={<Dashboard />} />
          <Route path="/quick" element={<QuickList />} />
          <Route path="/collect" element={<Collect />} />
          <Route path="/categories" element={<Categories />} />
          <Route path="/stock-scan" element={<StockScan />} />
          <Route path="/products" element={<Products />} />
          <Route path="/products/:id" element={<ProductDetail />} />
          <Route path="/audit" element={<Audit />} />
          <Route path="/pricing" element={<Pricing />} />
          <Route path="/listings" element={<Listings />} />
          <Route path="/orders" element={<Orders />} />
          <Route path="/cs" element={<Cs />} />
          <Route path="/wallet" element={<WalletPage />} />
          <Route path="/settings" element={<Settings />} />
        </Routes>
      </main>
    </div>
  )
}

/** 열린 항목 수. 0이면 아무것도 그리지 않는다 — 항상 떠 있는 배지는 곧 안 보이는 배지가 된다. */
function AttentionBadge() {
  const summary = useQuery({
    queryKey: ['attentionSummary'],
    queryFn: api.attentionSummary,
    refetchInterval: 60_000,
  })
  const open = summary.data?.open ?? 0
  if (open === 0) return null

  return (
    <span className="badge badge-danger" style={{ marginLeft: 6 }}>
      {open > 99 ? '99+' : open}
    </span>
  )
}
