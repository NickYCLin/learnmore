import { defineConfig } from 'vite';

export default defineConfig({
  server: {
    proxy: {
      '/backend': {
        target: process.env.LEARNMORE_DEV_BACKEND || 'https://magicplus-design.serveirc.com',
        changeOrigin: true,
        rewrite: path => path.replace(/^\/backend/, '/LearnMore')
      }
    }
  }
});
