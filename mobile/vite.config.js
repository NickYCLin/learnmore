import { defineConfig, loadEnv } from 'vite';

export default defineConfig(({ mode }) => ({
  plugins: [{ name: 'diagnostic-audio-gate', generateBundle() {
    if (loadEnv(mode, process.cwd(), 'VITE_').VITE_NATIVE_AUDIO_DIAGNOSTIC === '1') {
      this.emitFile({ type: 'asset', fileName: 'native-audio-diagnostic.json', source: '{"enabled":true}' });
    }
  } }],
  server: {
    proxy: {
      '/backend': {
        target: process.env.LEARNMORE_DEV_BACKEND || 'https://magicplus-design.serveirc.com',
        changeOrigin: true,
        rewrite: path => path.replace(/^\/backend/, '/LearnMore')
      }
    }
  }
}));
