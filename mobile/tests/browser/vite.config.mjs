import { fileURLToPath } from 'node:url';
import { defineConfig } from 'vite';

// 僅此測試伺服器替換原生 bridge，正式建置仍使用 Capacitor。
export default defineConfig({
  resolve: {
    alias: [{
      find: /^@capacitor\/(core|app|browser)$/,
      replacement: fileURLToPath(new URL('./native-bridge.mjs', import.meta.url)),
    }],
  },
  server: { host: '127.0.0.1', port: 5174, strictPort: true },
});
