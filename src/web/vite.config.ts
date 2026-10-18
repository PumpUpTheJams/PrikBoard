import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'

// The API runs separately (dotnet run in src/PrikBoard.Api); proxy its routes so the browser sees one origin.
const apiTarget = process.env.PRIKBOARD_API_URL ?? 'http://localhost:5080'

// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],
  server: {
    proxy: {
      '/api': apiTarget,
      '/openapi': apiTarget,
    },
  },
})
