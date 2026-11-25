import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'

// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],
  // When this bundle was built — shown under the sidebar status line so a deploy that did not
  // take effect is visible at a glance (AP, 2026-11-24).
  define: { __BUILD_TIME__: JSON.stringify(new Date().toISOString()) },
  server: {
    // Dev server default; proxy /api to the ASP.NET Core backend (MgtOcr.Api, see Program.cs)
    // so the React app can call same-origin relative paths ("/api/...") in both dev and prod.
    port: 5173,
    proxy: {
      // Dev: point /api at the LOCAL backend (MgtOcr.Api runs on http://localhost:8091, see
      // App:Port in appsettings.json / UseUrls in Program.cs) so the dev server tests the code
      // running on this machine. Switch back to the deployed server
      // (https://apiocr.megachem.co.th/) only to test against production.
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
