import * as Core from '@capacitor/core';
import { App } from '@capacitor/app';
import type { PluginListenerHandle } from '@capacitor/core';
import { createDiagnosticController } from './diagnostic-controller.mjs';

type AudioState = { revision: number; status: string; seconds: number; duration: number; rate: number; error: string };
interface DiagnosticBridge {
  start(options: { testId: 'tone-220-180s' }): Promise<AudioState>;
  pause(): Promise<AudioState>;
  stop(): Promise<AudioState>;
  seek(options: { seconds: number }): Promise<AudioState>;
  getState(): Promise<AudioState>;
  addListener(event: 'stateChange', listener: (state: AudioState) => void): Promise<PluginListenerHandle>;
}

let controller: ReturnType<typeof createDiagnosticController> | undefined;
export const diagnosticAudio = { stop: () => controller?.stop() };

export function initializeDiagnosticAudio(suspendYouTube: () => void) {
  if (import.meta.env.VITE_NATIVE_AUDIO_DIAGNOSTIC !== '1') return;
  const panel = document.createElement('section');
  panel.id = 'audio-diagnostic'; panel.setAttribute('aria-label', '原生背景音訊診斷');
  panel.innerHTML = '<h2>原生背景音訊診斷（非曲庫）</h2><p>僅播放 App 自產的 180 秒低音量測試訊號，不下載音樂。請先調低裝置音量。</p><p role="status" data-status></p><button data-play>播放／恢復測試訊號</button><button data-pause>暫停</button><button data-stop>停止並清理</button><label>原生進度 <input data-seek type="range" min="0" max="180" step="0.1" value="0" disabled></label>';
  document.querySelector('main')!.append(panel);
  const status = panel.querySelector<HTMLElement>('[data-status]')!;
  const seek = panel.querySelector<HTMLInputElement>('[data-seek]')!;
  const supported = Core.Capacitor.isNativePlatform() && Core.Capacitor.getPlatform?.() === 'ios';
  panel.querySelectorAll<HTMLButtonElement>('button').forEach(button => { button.disabled = !supported; });
  if (!supported) { status.textContent = '僅 iOS 原生診斷建置可播放；瀏覽器不模擬背景音訊。'; return; }
  const bridge = Core.registerPlugin<DiagnosticBridge>('DiagnosticAudio');
  const report = (error: unknown) => { status.textContent = `診斷失敗：${error instanceof Error ? error.message : String(error)}`; };
  controller = createDiagnosticController({ bridge, suspendYouTube, render: (state: AudioState) => {
    status.textContent = `${state.status}：${state.seconds.toFixed(1)} / ${state.duration.toFixed(1)} 秒（原生 rate ${state.rate}）${state.error ? `：${state.error}` : ''}`;
    seek.value = String(state.seconds); seek.disabled = state.duration === 0;
  } });
  const active = controller;
  panel.querySelector<HTMLButtonElement>('[data-play]')!.onclick = () => void active.start().catch(report);
  panel.querySelector<HTMLButtonElement>('[data-pause]')!.onclick = () => void active.pause().catch(report);
  panel.querySelector<HTMLButtonElement>('[data-stop]')!.onclick = () => void active.stop().catch(report);
  seek.onchange = () => void active.seek(Number(seek.value)).catch(report);
  let disposed = false;
  const appListener = App.addListener('appStateChange', ({ isActive }) => { if (isActive && !disposed) void active.refresh().catch(report); });
  window.addEventListener('pagehide', () => {
    disposed = true; controller = undefined;
    void appListener.then(handle => handle.remove()).catch(report);
    void active.dispose().catch(report);
  }, { once: true });
  void active.refresh().catch(report);
}
