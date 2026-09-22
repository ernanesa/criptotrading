import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import tailwindcss from '@tailwindcss/vite';

// https://vite.dev/config/
export default defineConfig({
  plugins: [
    tailwindcss(),
    react(),
  ],
  server: {
    port: 5173,
    proxy: {
      '/health': {
        target: 'http://127.0.0.1:58081',
        changeOrigin: true,
      },
      '/operational': {
        target: 'http://127.0.0.1:58081',
        changeOrigin: true,
      },
      '/metrics': {
        target: 'http://127.0.0.1:58081',
        changeOrigin: true,
      },
    },
  },
});
