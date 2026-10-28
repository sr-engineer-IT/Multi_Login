// Injected into every page, frame and worker before any site code runs.
// CONFIG is replaced with the profile's fingerprint (see FingerprintInjector.BuildScript).
(() => {
  const cfg = /*CONFIG*/;
  const g = globalThis;

  // ---- Make patched functions look native ---------------------------------------------
  const nativeToString = Function.prototype.toString;
  const masked = new WeakMap();
  const toStringProxy = new Proxy(nativeToString, {
    apply(target, thisArg, args) {
      return masked.has(thisArg) ? masked.get(thisArg) : Reflect.apply(target, thisArg, args);
    }
  });
  masked.set(toStringProxy, nativeToString.call(nativeToString));
  Object.defineProperty(Function.prototype, 'toString', {
    ...Object.getOwnPropertyDescriptor(Function.prototype, 'toString'), value: toStringProxy
  });

  // Wraps a method; impl receives (originalFn, thisArg, argsArray).
  function patchMethod(obj, name, impl) {
    const desc = obj && Object.getOwnPropertyDescriptor(obj, name);
    if (!desc || typeof desc.value !== 'function') return null;
    const orig = desc.value;
    const p = new Proxy(orig, { apply: (t, thisArg, args) => impl(t, thisArg, args) });
    masked.set(p, nativeToString.call(orig));
    Object.defineProperty(obj, name, { ...desc, value: p });
    return orig;
  }

  // Wraps a getter; the original runs first so "Illegal invocation" errors are preserved.
  // `value` receives the real value and returns the one to report.
  function patchGetter(obj, name, value) {
    const desc = obj && Object.getOwnPropertyDescriptor(obj, name);
    if (!desc || !desc.get) return;
    const orig = desc.get;
    const p = new Proxy(orig, { apply: (t, thisArg, args) => value(Reflect.apply(t, thisArg, args)) });
    masked.set(p, nativeToString.call(orig));
    Object.defineProperty(obj, name, { ...desc, get: p });
  }

  // Deterministic per-profile hash, so the same input always gets the same noise.
  const seed = cfg.seed >>> 0;
  function hash(a, b) {
    let h = seed ^ Math.imul(a | 0, 0x9E3779B1) ^ Math.imul(b | 0, 0x85EBCA77);
    h ^= h >>> 15; h = Math.imul(h, 0x2C1B3C6D);
    h ^= h >>> 12; h = Math.imul(h, 0x297A2D39);
    h ^= h >>> 15;
    return h >>> 0;
  }

  // ---- Navigator / screen --------------------------------------------------------------
  const navProto = (g.Navigator || g.WorkerNavigator)?.prototype;
  patchGetter(navProto, 'hardwareConcurrency', () => cfg.cores);
  patchGetter(navProto, 'deviceMemory', () => cfg.memory);
  patchGetter(navProto, 'platform', () => cfg.navPlatform);

  // A page that runs before Chrome has sent its window size to the renderer sees a 0×0
  // window, which is a well-known automation tell. Report the launch size until it arrives.
  if (g.window === g && cfg.winW) {
    patchGetter(g, 'outerWidth', v => v || cfg.winW);
    patchGetter(g, 'outerHeight', v => v || cfg.winH);
  }

  if (g.Screen && cfg.screenW) {
    const sp = g.Screen.prototype;
    patchGetter(sp, 'width', () => cfg.screenW);
    patchGetter(sp, 'height', () => cfg.screenH);
    patchGetter(sp, 'availWidth', () => cfg.screenW);
    patchGetter(sp, 'availHeight', () => cfg.screenH - cfg.taskbar);
  }

  // ---- Canvas --------------------------------------------------------------------------
  // Flips the lowest bit of one channel in ~3% of non-transparent pixels. Keyed on absolute
  // pixel position so a sub-rectangle read matches the same pixels in a full read.
  function noisePixels(data, width, x0, y0) {
    for (let i = 0; i < data.length; i += 4) {
      if (data[i + 3] === 0) continue;
      const p = i >> 2;
      const h = hash(x0 + (p % width), y0 + ((p / width) | 0));
      if ((h & 31) !== 0) continue;
      data[i + ((h >>> 5) % 3)] ^= 1;
    }
  }

  if (cfg.canvas) {
    const raw2dGetImageData = {};
    for (const C of [g.CanvasRenderingContext2D, g.OffscreenCanvasRenderingContext2D]) {
      if (!C) continue;
      raw2dGetImageData[C.name] = patchMethod(C.prototype, 'getImageData', (orig, ctx, args) => {
        const img = Reflect.apply(orig, ctx, args);
        noisePixels(img.data, img.width, args[0] | 0, args[1] | 0);
        return img;
      });
    }

    // Copies the canvas, noises the copy, and lets the caller export that instead.
    const MAX_PIXELS = 4096 * 4096;
    function noisedCopy(canvas) {
      const w = canvas.width, h = canvas.height;
      if (!w || !h || w * h > MAX_PIXELS) return null;
      const copy = g.document ? g.document.createElement('canvas') : new OffscreenCanvas(w, h);
      copy.width = w; copy.height = h;
      const ctx = copy.getContext('2d');
      ctx.drawImage(canvas, 0, 0);
      const getImageData = raw2dGetImageData[ctx.constructor.name];
      const img = getImageData.call(ctx, 0, 0, w, h);
      noisePixels(img.data, w, 0, 0);
      ctx.putImageData(img, 0, 0);
      return copy;
    }

    function exportVia(orig, canvas, args) {
      let copy = null;
      try { copy = noisedCopy(canvas); } catch { /* tainted or detached: let the original throw */ }
      return Reflect.apply(orig, copy || canvas, args);
    }

    if (g.HTMLCanvasElement) {
      const proto = g.HTMLCanvasElement.prototype;
      const rawToDataURL = proto.toDataURL, rawToBlob = proto.toBlob;
      patchMethod(proto, 'toDataURL', (orig, c, args) => exportVia(rawToDataURL, c, args));
      patchMethod(proto, 'toBlob', (orig, c, args) => exportVia(rawToBlob, c, args));
    }
    if (g.OffscreenCanvas) {
      const proto = g.OffscreenCanvas.prototype;
      const rawConvert = proto.convertToBlob;
      patchMethod(proto, 'convertToBlob', (orig, c, args) => exportVia(rawConvert, c, args));
    }

    for (const C of [g.WebGLRenderingContext, g.WebGL2RenderingContext]) {
      if (!C) continue;
      patchMethod(C.prototype, 'readPixels', (orig, gl, args) => {
        const result = Reflect.apply(orig, gl, args);
        const [x, y, width, , , , pixels] = args;
        if (pixels instanceof Uint8Array && width > 0) noisePixels(pixels, width, x | 0, y | 0);
        return result;
      });
    }
  }

  // ---- WebGL / WebGPU identity ---------------------------------------------------------
  if (cfg.webgl) {
    const UNMASKED_VENDOR = 0x9245, UNMASKED_RENDERER = 0x9246, VENDOR = 0x1F00, RENDERER = 0x1F01;
    for (const C of [g.WebGLRenderingContext, g.WebGL2RenderingContext]) {
      if (!C) continue;
      patchMethod(C.prototype, 'getParameter', (orig, gl, args) => {
        const v = Reflect.apply(orig, gl, args);
        if (v == null) return v;
        switch (args[0]) {
          case UNMASKED_VENDOR: return cfg.glVendor;
          case UNMASKED_RENDERER: return cfg.glRenderer;
          // Only replace these when the browser exposes the real GPU through them.
          case VENDOR: return v === 'WebKit' ? v : cfg.glVendor;
          case RENDERER: return v === 'WebKit WebGL' ? v : cfg.glRenderer;
          default: return v;
        }
      });
    }
    if (g.GPUAdapterInfo) {
      const ap = g.GPUAdapterInfo.prototype;
      patchGetter(ap, 'vendor', () => cfg.gpuVendor);
      patchGetter(ap, 'architecture', () => cfg.gpuArch);
      patchGetter(ap, 'device', () => '');
      patchGetter(ap, 'description', () => '');
    }
  }

  // ---- Audio ---------------------------------------------------------------------------
  if (cfg.audio && g.AudioBuffer) {
    const proto = g.AudioBuffer.prototype;
    const rawGetChannelData = proto.getChannelData;
    const done = new WeakSet();
    // Adds inaudible (~1e-7) noise to a sparse set of non-silent samples, once per buffer.
    const touch = (buf) => {
      if (done.has(buf)) return;
      done.add(buf);
      for (let c = 0; c < buf.numberOfChannels; c++) {
        const d = rawGetChannelData.call(buf, c);
        for (let i = 0; i < d.length; i += 97) {
          if (d[i] !== 0) d[i] += ((hash(i, c) & 0xFFFF) / 0xFFFF - 0.5) * 2e-7;
        }
      }
    };
    patchMethod(proto, 'getChannelData', (orig, buf, args) => {
      const r = Reflect.apply(orig, buf, args);
      touch(buf);
      return r;
    });
    patchMethod(proto, 'copyFromChannel', (orig, buf, args) => {
      touch(buf);
      return Reflect.apply(orig, buf, args);
    });
    if (g.AnalyserNode) {
      patchMethod(g.AnalyserNode.prototype, 'getFloatFrequencyData', (orig, node, args) => {
        const r = Reflect.apply(orig, node, args);
        const arr = args[0];
        if (arr instanceof Float32Array) {
          for (let i = 0; i < arr.length; i++) {
            if (Number.isFinite(arr[i])) arr[i] += ((hash(i, 7) & 0xFFFF) / 0xFFFF - 0.5) * 1e-4;
          }
        }
        return r;
      });
    }
  }
})();
