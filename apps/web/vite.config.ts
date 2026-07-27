import { defineConfig } from 'vitest/config'
import react from '@vitejs/plugin-react'


const apiProxyTarget = process.env.MISTCHESS_API_PROXY_TARGET ?? 'http://localhost:5052'
export default defineConfig({
  plugins: [react()],
  server: {
    proxy: {
      '/api': {
        target: apiProxyTarget,
        changeOrigin: true,
      },
      '/hubs': {
        target: apiProxyTarget,
        changeOrigin: true,
        ws: true,
      },
    },
  },
  test: {
    environment: 'jsdom',
    setupFiles: ['./src/test/setup.ts'],
    css: true,
  },
})
