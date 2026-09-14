export const Capacitor = { isNativePlatform: () => true };
export const CapacitorHttp = {
  async get(options) { return this.request({ ...options, method: 'GET' }); },
  async request({ url, method, headers, data, responseType }) {
    const response = await fetch(url, {
      method, headers, body: data === undefined ? undefined : JSON.stringify(data),
    });
    return { status: response.status, data: response.status === 204 ? null : responseType === 'text' ? await response.text() : await response.json() };
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
