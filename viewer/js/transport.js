// Live link to `vcl serve`: receives IR over a WebSocket, reconnects forever.
export class Transport {
  constructor({ url, onCmd, onStatus = () => {} }) {
    this.url = url;
    this.onCmd = onCmd;
    this.onStatus = onStatus;
    this.ws = null;
    this.delay = 500;
    this.everOpened = false;
    this.failures = 0;
  }

  get connected() { return this.ws?.readyState === WebSocket.OPEN; }

  connect() {
    this.onStatus('connecting');
    const ws = new WebSocket(this.url);
    this.ws = ws;
    ws.onopen = () => { this.delay = 500; this.everOpened = true; this.onStatus('live'); };
    ws.onmessage = (ev) => {
      for (const line of String(ev.data).split('\n')) {
        if (!line.trim()) continue;
        try { this.onCmd(JSON.parse(line)); } catch (e) { console.error('bad message', line, e); }
      }
    };
    ws.onclose = () => {
      this.onStatus('offline');
      // No server here at all (e.g. a static host): stop trying, run locally.
      if (!this.everOpened && ++this.failures >= 2) return;
      setTimeout(() => this.connect(), this.delay);
      this.delay = Math.min(this.delay * 2, 8000);
    };
  }

  send(text) { this.ws.send(text); }
}
