import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    proxy: {
      // 백엔드 API 프록시 — 프론트는 항상 상대 경로로 호출한다
      '/api': { target: 'http://localhost:5080', changeOrigin: true },
    },
  },
})
