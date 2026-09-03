import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'

// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],
  server: {
    // Dev server default; proxy /api to the ASP.NET Core backend (MgtOcr.Api, see Program.cs)
    // so the React app can call same-origin relative paths ("/api/...") in both dev and prod.
    port: 5173,
    proxy: {
      '/api': {
        target: 'http://localhost:8091',
        changeOrigin: true,
      },
    },
  },
  build: {
    // .NET's Program.cs serves the built frontend as static files from this folder in
    // production (mirrors how frontend/ is served today) — see backend wiring notes.
    outDir: 'dist',
  },
})
