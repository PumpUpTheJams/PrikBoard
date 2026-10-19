import react from '@vitejs/plugin-react'
import { defineConfig, type ProxyOptions } from 'vite'

// The API runs separately (dotnet run in src/PrikBoard.Api); proxy its routes so the browser sees one origin.
// changeOrigin stays off so the API sees Host: localhost:5173 and builds OAuth callback URLs (and the OpenAPI
// server URL) for the address the browser actually uses.
const api: ProxyOptions = { target: process.env.PRIKBOARD_API_URL ?? 'http://localhost:5080', changeOrigin: false }

// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],
  server: {
    // As in nginx/default.conf.template: no other site may frame the app.
    headers: { 'X-Frame-Options': 'DENY', 'Content-Security-Policy': "frame-ancestors 'none'" },
    proxy: {
      // ws: the live-updates hub (/api/live) upgrades to a WebSocket.
      '/api': { ...api, ws: true },
      '/openapi': api,
      // The MCP endpoint, so agents can be pointed at the dev server too.
      '/mcp': api,
      // Through the proxy so signing in from the API reference uses the same callback URL as the app.
      '/scalar': api,
    },
  },
})
