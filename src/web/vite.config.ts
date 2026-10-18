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
    proxy: {
      '/api': api,
      '/openapi': api,
      // Through the proxy so signing in from the API reference uses the same callback URL as the app.
      '/scalar': api,
    },
  },
})
