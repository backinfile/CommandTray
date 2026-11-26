"use strict";

importScripts("/qr-scan-vendor.js");

self.onmessage = event => {
  const {width, height, pixels} = event.data;
  try {
    const result = self.jsQR(new Uint8ClampedArray(pixels), width, height);
    self.postMessage({text: result?.data ?? null});
  } catch {
    self.postMessage({error: true});
  }
};
