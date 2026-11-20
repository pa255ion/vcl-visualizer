// The Zig parser compiled to wasm (viewer/vcl.wasm, built by `zig build`).
// Used when no server is connected, so lessons still run from a static host.
export async function loadParser(url = 'vcl.wasm') {
  const res = await fetch(url);
  if (!res.ok) throw new Error(`could not load ${url}`);
  const { instance } = await WebAssembly.instantiate(await res.arrayBuffer(), {});
  const x = instance.exports;
  const enc = new TextEncoder(), dec = new TextDecoder();
  let mode = 'vcl';
  return {
    reset(m = 'vcl') { mode = m; x.vcl_reset(m === 'mixed' ? 1 : 0); },
    get mode() { return mode; },
    /** The keys of the `data` commands in a text (their results are figs/<key>.data.json). */
    dataKeys(text, m = mode) {
      const bytes = enc.encode(text);
      const n = Math.max(1, bytes.length);
      const p = x.vcl_alloc(n);
      new Uint8Array(x.memory.buffer, p, bytes.length).set(bytes);
      const rc = x.vcl_data_keys(p, bytes.length, m === 'mixed' ? 1 : 0);
      x.vcl_free(p, n);
      if (rc !== 0) return [];
      return dec.decode(new Uint8Array(x.memory.buffer, x.vcl_out_ptr(), x.vcl_out_len())).split('\n').filter(Boolean);
    },
    /** Hand in a `data` command's results (computed ahead of time). */
    putData(key, json) {
      const k = enc.encode(key), j = enc.encode(json);
      const kp = x.vcl_alloc(Math.max(1, k.length)), jp = x.vcl_alloc(Math.max(1, j.length));
      new Uint8Array(x.memory.buffer, kp, k.length).set(k);
      new Uint8Array(x.memory.buffer, jp, j.length).set(j);
      x.vcl_put_data(kp, k.length, jp, j.length);
      x.vcl_free(kp, Math.max(1, k.length));
      x.vcl_free(jp, Math.max(1, j.length));
    },
    /** Parse a chunk; returns IR objects for every completed line. */
    feed(text, finish = true) {
      const bytes = enc.encode(text);
      const n = Math.max(1, bytes.length);
      const p = x.vcl_alloc(n);
      new Uint8Array(x.memory.buffer, p, bytes.length).set(bytes);
      const rc = x.vcl_feed(p, bytes.length, finish);
      x.vcl_free(p, n);
      if (rc !== 0) throw new Error('parser failed');
      const out = dec.decode(new Uint8Array(x.memory.buffer, x.vcl_out_ptr(), x.vcl_out_len()));
      return out.split('\n').filter(Boolean).map((l) => JSON.parse(l));
    },
  };
}
