import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],
  server: {
    watch: {
      // The vendored pdf.js worker is a large static asset that can be locked by
      // the browser/OS; watching it intermittently throws EBUSY and crashes the
      // dev server. It never changes at runtime, so exclude it from the watcher.
      ignored: ['**/public/pdf.worker.min.js'],
    },
  },
})
