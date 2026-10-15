export function createDiagnosticController({ bridge, suspendYouTube, render }) {
  let intent = 0, revision = -1, disposed = false;
  let queue = Promise.resolve(), listener;
  const accept = state => {
    if (!disposed && state.revision > revision) { revision = state.revision; render(state); }
  };
  const ready = bridge.addListener('stateChange', accept).then(handle => {
    if (disposed) return handle.remove();
    listener = handle;
  });
  const enqueue = action => {
    const result = queue.then(action);
    queue = result.catch(() => {});
    return result;
  };
  return {
    async start() {
      if (disposed) throw new Error('診斷介面已關閉');
      const current = ++intent;
      suspendYouTube();
      return enqueue(async () => {
        await ready;
        if (disposed || current !== intent) return;
        accept(await bridge.start({ testId: 'tone-220-180s' }));
      });
    },
    pause() { return enqueue(async () => { accept(await bridge.pause()); }); },
    seek(seconds) { return enqueue(async () => { accept(await bridge.seek({ seconds })); }); },
    refresh() { return enqueue(async () => { await ready; accept(await bridge.getState()); }); },
    stop() { intent++; return enqueue(async () => { accept(await bridge.stop()); }); },
    async dispose() {
      disposed = true; intent++;
      try { await enqueue(() => bridge.stop()); }
      finally { await ready; await listener?.remove(); }
    },
  };
}
