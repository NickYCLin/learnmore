export const Capacitor = { isNativePlatform: () => true };
export const CapacitorHttp = {
  async request({ url, method, headers, data }) {
    const response = await fetch(url, {
      method, headers, body: data === undefined ? undefined : JSON.stringify(data),
    });
    return { status: response.status, data: response.status === 204 ? null : await response.json() };
  },
};

function addListener(name, listener) {
  const receive = event => listener(event.detail);
  window.addEventListener(`test:${name}`, receive);
  return Promise.resolve({ remove: () => window.removeEventListener(`test:${name}`, receive) });
}

export const App = { addListener };
export const Browser = {
  addListener,
  async open({ url }) { window.testLoginURL = url; },
  async close() {},
};
