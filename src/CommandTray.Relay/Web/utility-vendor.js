/* Generated bundle. @noble/hashes 2.4.0 and Project Nayuki QR Code generator v1.8.0. */
/*
The MIT License (MIT)

Copyright (c) 2022 Paul Miller (https://paulmillr.com)

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the “Software”), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in
all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED “AS IS”, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
THE SOFTWARE.
*/
/*
 * QR Code generator library (TypeScript)
 *
 * Copyright (c) Project Nayuki. (MIT License)
 * https://www.nayuki.io/page/qr-code-generator-library
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy of
 * this software and associated documentation files (the "Software"), to deal in
 * the Software without restriction, including without limitation the rights to
 * use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of
 * the Software, and to permit persons to whom the Software is furnished to do so,
 * subject to the following conditions:
 * - The above copyright notice and this permission notice shall be included in
 *   all copies or substantial portions of the Software.
 * - The Software is provided "as is", without warranty of any kind, express or
 *   implied, including but not limited to the warranties of merchantability,
 *   fitness for a particular purpose and noninfringement. In no event shall the
 *   authors or copyright holders be liable for any claim, damages or other
 *   liability, whether in an action of contract, tort or otherwise, arising from,
 *   out of or in connection with the Software or the use or other dealings in the
 *   Software.
 */
var commandTrayUtilityPrimitives = (() => {
  var __defProp = Object.defineProperty;
  var __getOwnPropDesc = Object.getOwnPropertyDescriptor;
  var __getOwnPropNames = Object.getOwnPropertyNames;
  var __hasOwnProp = Object.prototype.hasOwnProperty;
  var __export = (target, all) => {
    for (var name in all)
      __defProp(target, name, { get: all[name], enumerable: true });
  };
  var __copyProps = (to, from, except, desc) => {
    if (from && typeof from === "object" || typeof from === "function") {
      for (let key of __getOwnPropNames(from))
        if (!__hasOwnProp.call(to, key) && key !== except)
          __defProp(to, key, { get: () => from[key], enumerable: !(desc = __getOwnPropDesc(from, key)) || desc.enumerable });
    }
    return to;
  };
  var __toCommonJS = (mod) => __copyProps(__defProp({}, "__esModule", { value: true }), mod);

  // entry.ts
  var entry_exports = {};
  __export(entry_exports, {
    qrcodegen: () => qrcodegen,
    sha256: () => sha256
  });

  // node_modules/@noble/hashes/_u64.js
  var fromNumH = (n) => n / 2 ** 32 | 0;
  var fromNumL = (n) => n >>> 0;
  function setU64FromNum(view, byteOffset, n, isLE) {
    const h = fromNumH(n);
    const l = fromNumL(n);
    view.setUint32(byteOffset, isLE ? l : h, isLE);
    view.setUint32(byteOffset + 4, isLE ? h : l, isLE);
  }

  // node_modules/@noble/hashes/utils.js
  function isBytes(a) {
    return a instanceof Uint8Array || ArrayBuffer.isView(a) && a.constructor.name === "Uint8Array" && "BYTES_PER_ELEMENT" in a && a.BYTES_PER_ELEMENT === 1;
  }
  var atitle = (title) => title ? `"${title}" ` : "";
  function anumber(n, title = "") {
    if (typeof n !== "number")
      throw new TypeError(atitle(title) + "expected number, got " + typeof n);
    if (!Number.isSafeInteger(n) || n < 0)
      throw new RangeError(atitle(title) + "expected integer >= 0, got " + n);
    return n;
  }
  function abytes(value, length, title = "") {
    if (isBytes(value) && (length === void 0 || value.length === length))
      return value;
    if (length !== void 0)
      anumber(length, "length");
    const bytes = isBytes(value);
    const ofLen = length !== void 0 ? ` of length ${length}` : "";
    const got = bytes ? `length=${value.length}` : `type=${typeof value}`;
    const message = atitle(title) + "expected Uint8Array" + ofLen + ", got " + got;
    if (!bytes)
      throw new TypeError(message);
    throw new RangeError(message);
  }
  var aobject = (value, label) => {
    if (value === null || typeof value !== "object" || Array.isArray(value))
      throw new TypeError((label === "object" ? "" : `"${label}" `) + "expected object, got type=" + typeof value);
  };
  var aopts = (value, label) => {
    aobject(value, label);
    const proto = Object.getPrototypeOf(value);
    if (proto !== Object.prototype && proto !== null)
      throw new TypeError(`"${label}" expected plain object`);
    if (Object.hasOwn(value, "__proto__"))
      throw new TypeError(`"${label}.__proto__" is not allowed`);
  };
  function aexists(instance, checkFinished = true) {
    if (instance.destroyed)
      throw new Error("hash was destroyed");
    if (checkFinished && instance.finished)
      throw new Error("digest() was already called");
  }
  function aoutput(out, instance) {
    abytes(out, void 0, "output");
    const min = instance.outputLen;
    if (!(out.length >= min)) {
      throw new RangeError('"output" expected length >= ' + min);
    }
  }
  function clean(...arrays) {
    for (let i = 0; i < arrays.length; i++) {
      arrays[i].fill(0);
    }
  }
  function createView(arr) {
    return new DataView(arr.buffer, arr.byteOffset, arr.byteLength);
  }
  function rotr(word, shift) {
    return word << 32 - shift | word >>> shift;
  }
  function checkOpts(defaults, opts, title = "opts") {
    aopts(defaults, "defaults");
    if (opts !== void 0)
      aopts(opts, title);
    const merged = Object.assign(/* @__PURE__ */ Object.create(null), defaults, opts);
    return merged;
  }
  function createHasher(hashCons, info = {}) {
    if (typeof hashCons !== "function")
      throw new TypeError('"hashCons" expected function, got type=' + typeof hashCons);
    info = checkOpts({}, info, "info");
    const hashC = (msg, opts) => hashCons(opts).update(msg).digest();
    const tmp = hashCons(void 0);
    hashC.outputLen = tmp.outputLen;
    hashC.blockLen = tmp.blockLen;
    hashC.canXOF = tmp.canXOF;
    hashC.create = (opts) => hashCons(opts);
    Object.assign(hashC, info);
    return Object.freeze(hashC);
  }
  var oidNist = (suffix) => ({
    // Current NIST hashAlgs suffixes used here fit in one DER subidentifier octet.
    // Larger suffix values would need base-128 OID encoding and a different length byte.
    oid: Uint8Array.from([6, 9, 96, 134, 72, 1, 101, 3, 4, 2, suffix])
  });

  // node_modules/@noble/hashes/_md.js
  function Chi(a, b, c) {
    return a & b ^ ~a & c;
  }
  function Maj(a, b, c) {
    return a & b ^ a & c ^ b & c;
  }
  var HashMD = class {
    blockLen;
    outputLen;
    canXOF = false;
    padOffset;
    isLE;
    // For partial updates less than block size
    buffer;
    view;
    finished = false;
    length = 0;
    pos = 0;
    destroyed = false;
    constructor(blockLen, outputLen, padOffset, isLE) {
      this.blockLen = blockLen;
      this.outputLen = outputLen;
      this.padOffset = padOffset;
      this.isLE = isLE;
      this.buffer = new Uint8Array(blockLen);
      this.view = createView(this.buffer);
    }
    update(data) {
      aexists(this);
      abytes(data);
      const { view, buffer, blockLen } = this;
      const len = data.length;
      let processed = false;
      for (let pos = 0; pos < len; ) {
        const take = Math.min(blockLen - this.pos, len - pos);
        if (take === blockLen) {
          const dataView = createView(data);
          for (; blockLen <= len - pos; pos += blockLen)
            this.process(dataView, pos);
          processed = true;
          continue;
        }
        buffer.set(pos === 0 && take === len ? data : data.subarray(pos, pos + take), this.pos);
        this.pos += take;
        pos += take;
        if (this.pos === blockLen) {
          this.process(view, 0);
          this.pos = 0;
          processed = true;
        }
      }
      this.length += data.length;
      if (processed)
        this.roundClean();
      return this;
    }
    digestInto(out) {
      aexists(this);
      aoutput(out, this);
      this.finished = true;
      const { buffer, view, blockLen, isLE } = this;
      let { pos } = this;
      buffer[pos++] = 128;
      buffer.fill(0, pos);
      if (this.padOffset > blockLen - pos) {
        this.process(view, 0);
        buffer.fill(0);
      }
      setU64FromNum(view, blockLen - 8, this.length * 8, isLE);
      this.process(view, 0);
      this.roundClean();
      const oview = out === buffer ? view : createView(out);
      const len = this.outputLen;
      const outLen = len / 4;
      const state = this.get();
      if (len % 4 || outLen > state.length)
        throw new Error("invalid outputLen");
      for (let i = 0; i < outLen; i++)
        oview.setUint32(4 * i, state[i], isLE);
    }
    digest() {
      const { buffer, outputLen } = this;
      this.digestInto(buffer);
      const res = buffer.slice(0, outputLen);
      this.destroy();
      return res;
    }
    _cloneIntoMeta(to) {
      const { buffer, length, finished, destroyed, pos } = this;
      to.destroyed = destroyed;
      to.finished = finished;
      to.length = length;
      to.pos = pos;
      if (pos)
        to.buffer.set(buffer);
      return to;
    }
    clone() {
      return this._cloneInto();
    }
  };
  var SHA256_IV = /* @__PURE__ */ Uint32Array.from([
    1779033703,
    3144134277,
    1013904242,
    2773480762,
    1359893119,
    2600822924,
    528734635,
    1541459225
  ]);

  // node_modules/@noble/hashes/sha2.js
  var SHA256_K = /* @__PURE__ */ Uint32Array.from([
    1116352408,
    1899447441,
    3049323471,
    3921009573,
    961987163,
    1508970993,
    2453635748,
    2870763221,
    3624381080,
    310598401,
    607225278,
    1426881987,
    1925078388,
    2162078206,
    2614888103,
    3248222580,
    3835390401,
    4022224774,
    264347078,
    604807628,
    770255983,
    1249150122,
    1555081692,
    1996064986,
    2554220882,
    2821834349,
    2952996808,
    3210313671,
    3336571891,
    3584528711,
    113926993,
    338241895,
    666307205,
    773529912,
    1294757372,
    1396182291,
    1695183700,
    1986661051,
    2177026350,
    2456956037,
    2730485921,
    2820302411,
    3259730800,
    3345764771,
    3516065817,
    3600352804,
    4094571909,
    275423344,
    430227734,
    506948616,
    659060556,
    883997877,
    958139571,
    1322822218,
    1537002063,
    1747873779,
    1955562222,
    2024104815,
    2227730452,
    2361852424,
    2428436474,
    2756734187,
    3204031479,
    3329325298
  ]);
  var SHA256_W = /* @__PURE__ */ new Uint32Array(64);
  var SHA2_32B = class extends HashMD {
    // We cannot use array here since array allows indexing by variable
    // which means optimizer/compiler cannot use registers.
    // Numeric initializers matter: starting the fields as `undefined` changes
    // V8's field representation and makes sha256 3x slower (measured).
    A = 0;
    B = 0;
    C = 0;
    D = 0;
    E = 0;
    F = 0;
    G = 0;
    H = 0;
    constructor(outputLen, IV) {
      super(64, outputLen, 8, false);
      this.A = IV[0] | 0;
      this.B = IV[1] | 0;
      this.C = IV[2] | 0;
      this.D = IV[3] | 0;
      this.E = IV[4] | 0;
      this.F = IV[5] | 0;
      this.G = IV[6] | 0;
      this.H = IV[7] | 0;
    }
    get() {
      const { A, B, C, D, E, F, G, H } = this;
      return [A, B, C, D, E, F, G, H];
    }
    // prettier-ignore
    set(A, B, C, D, E, F, G, H) {
      this.A = A | 0;
      this.B = B | 0;
      this.C = C | 0;
      this.D = D | 0;
      this.E = E | 0;
      this.F = F | 0;
      this.G = G | 0;
      this.H = H | 0;
    }
    _cloneInto(to) {
      (to ||= new this.constructor()).set(...this.get());
      return this._cloneIntoMeta(to);
    }
    process(view, offset) {
      for (let i = 0; i < 16; i++, offset += 4)
        SHA256_W[i] = view.getUint32(offset, false);
      for (let i = 16; i < 64; i++) {
        const W15 = SHA256_W[i - 15];
        const W2 = SHA256_W[i - 2];
        const s0 = rotr(W15, 7) ^ rotr(W15, 18) ^ W15 >>> 3;
        const s1 = rotr(W2, 17) ^ rotr(W2, 19) ^ W2 >>> 10;
        SHA256_W[i] = s1 + SHA256_W[i - 7] + s0 + SHA256_W[i - 16] | 0;
      }
      let { A, B, C, D, E, F, G, H } = this;
      for (let i = 0; i < 64; i++) {
        const sigma1 = rotr(E, 6) ^ rotr(E, 11) ^ rotr(E, 25);
        const T1 = H + sigma1 + Chi(E, F, G) + SHA256_K[i] + SHA256_W[i] | 0;
        const sigma0 = rotr(A, 2) ^ rotr(A, 13) ^ rotr(A, 22);
        const T2 = sigma0 + Maj(A, B, C) | 0;
        H = G;
        G = F;
        F = E;
        E = D + T1 | 0;
        D = C;
        C = B;
        B = A;
        A = T1 + T2 | 0;
      }
      A = A + this.A | 0;
      B = B + this.B | 0;
      C = C + this.C | 0;
      D = D + this.D | 0;
      E = E + this.E | 0;
      F = F + this.F | 0;
      G = G + this.G | 0;
      H = H + this.H | 0;
      this.set(A, B, C, D, E, F, G, H);
    }
    roundClean() {
      clean(SHA256_W);
    }
    destroy() {
      this.destroyed = true;
      this.set(0, 0, 0, 0, 0, 0, 0, 0);
      clean(this.buffer);
    }
  };
  var _SHA256 = class extends SHA2_32B {
    constructor() {
      super(32, SHA256_IV);
    }
  };
  var sha256 = /* @__PURE__ */ createHasher(
    () => new _SHA256(),
    /* @__PURE__ */ oidNist(1)
  );

  // qrcodegen.ts
  var qrcodegen;
  ((qrcodegen2) => {
    class QrCode {
      /*-- Constructor (low level) and fields --*/
      // Creates a new QR Code with the given version number,
      // error correction level, data codeword bytes, and mask number.
      // This is a low-level API that most users should not use directly.
      // A mid-level API is the encodeSegments() function.
      constructor(version, errorCorrectionLevel, dataCodewords, msk) {
        this.version = version;
        this.errorCorrectionLevel = errorCorrectionLevel;
        if (version < QrCode.MIN_VERSION || version > QrCode.MAX_VERSION)
          throw new RangeError("Version value out of range");
        if (msk < -1 || msk > 7)
          throw new RangeError("Mask value out of range");
        this.size = version * 4 + 17;
        let row = [];
        for (let i = 0; i < this.size; i++)
          row.push(false);
        for (let i = 0; i < this.size; i++) {
          this.modules.push(row.slice());
          this.isFunction.push(row.slice());
        }
        this.drawFunctionPatterns();
        const allCodewords = this.addEccAndInterleave(dataCodewords);
        this.drawCodewords(allCodewords);
        if (msk == -1) {
          let minPenalty = 1e9;
          for (let i = 0; i < 8; i++) {
            this.applyMask(i);
            this.drawFormatBits(i);
            const penalty = this.getPenaltyScore();
            if (penalty < minPenalty) {
              msk = i;
              minPenalty = penalty;
            }
            this.applyMask(i);
          }
        }
        assert(0 <= msk && msk <= 7);
        this.mask = msk;
        this.applyMask(msk);
        this.drawFormatBits(msk);
        this.isFunction = [];
      }
      version;
      errorCorrectionLevel;
      /*-- Static factory functions (high level) --*/
      // Returns a QR Code representing the given Unicode text string at the given error correction level.
      // As a conservative upper bound, this function is guaranteed to succeed for strings that have 738 or fewer
      // Unicode code points (not UTF-16 code units) if the low error correction level is used. The smallest possible
      // QR Code version is automatically chosen for the output. The ECC level of the result may be higher than the
      // ecl argument if it can be done without increasing the version.
      static encodeText(text, ecl) {
        const segs = qrcodegen2.QrSegment.makeSegments(text);
        return QrCode.encodeSegments(segs, ecl);
      }
      // Returns a QR Code representing the given binary data at the given error correction level.
      // This function always encodes using the binary segment mode, not any text mode. The maximum number of
      // bytes allowed is 2953. The smallest possible QR Code version is automatically chosen for the output.
      // The ECC level of the result may be higher than the ecl argument if it can be done without increasing the version.
      static encodeBinary(data, ecl) {
        const seg = qrcodegen2.QrSegment.makeBytes(data);
        return QrCode.encodeSegments([seg], ecl);
      }
      /*-- Static factory functions (mid level) --*/
      // Returns a QR Code representing the given segments with the given encoding parameters.
      // The smallest possible QR Code version within the given range is automatically
      // chosen for the output. Iff boostEcl is true, then the ECC level of the result
      // may be higher than the ecl argument if it can be done without increasing the
      // version. The mask number is either between 0 to 7 (inclusive) to force that
      // mask, or -1 to automatically choose an appropriate mask (which may be slow).
      // This function allows the user to create a custom sequence of segments that switches
      // between modes (such as alphanumeric and byte) to encode text in less space.
      // This is a mid-level API; the high-level API is encodeText() and encodeBinary().
      static encodeSegments(segs, ecl, minVersion = 1, maxVersion = 40, mask = -1, boostEcl = true) {
        if (!(QrCode.MIN_VERSION <= minVersion && minVersion <= maxVersion && maxVersion <= QrCode.MAX_VERSION) || mask < -1 || mask > 7)
          throw new RangeError("Invalid value");
        let version;
        let dataUsedBits;
        for (version = minVersion; ; version++) {
          const dataCapacityBits2 = QrCode.getNumDataCodewords(version, ecl) * 8;
          const usedBits = QrSegment.getTotalBits(segs, version);
          if (usedBits <= dataCapacityBits2) {
            dataUsedBits = usedBits;
            break;
          }
          if (version >= maxVersion)
            throw new RangeError("Data too long");
        }
        for (const newEcl of [QrCode.Ecc.MEDIUM, QrCode.Ecc.QUARTILE, QrCode.Ecc.HIGH]) {
          if (boostEcl && dataUsedBits <= QrCode.getNumDataCodewords(version, newEcl) * 8)
            ecl = newEcl;
        }
        let bb = [];
        for (const seg of segs) {
          appendBits(seg.mode.modeBits, 4, bb);
          appendBits(seg.numChars, seg.mode.numCharCountBits(version), bb);
          for (const b of seg.getData())
            bb.push(b);
        }
        assert(bb.length == dataUsedBits);
        const dataCapacityBits = QrCode.getNumDataCodewords(version, ecl) * 8;
        assert(bb.length <= dataCapacityBits);
        appendBits(0, Math.min(4, dataCapacityBits - bb.length), bb);
        appendBits(0, (8 - bb.length % 8) % 8, bb);
        assert(bb.length % 8 == 0);
        for (let padByte = 236; bb.length < dataCapacityBits; padByte ^= 236 ^ 17)
          appendBits(padByte, 8, bb);
        let dataCodewords = [];
        while (dataCodewords.length * 8 < bb.length)
          dataCodewords.push(0);
        bb.forEach((b, i) => dataCodewords[i >>> 3] |= b << 7 - (i & 7));
        return new QrCode(version, ecl, dataCodewords, mask);
      }
      /*-- Fields --*/
      // The width and height of this QR Code, measured in modules, between
      // 21 and 177 (inclusive). This is equal to version * 4 + 17.
      size;
      // The index of the mask pattern used in this QR Code, which is between 0 and 7 (inclusive).
      // Even if a QR Code is created with automatic masking requested (mask = -1),
      // the resulting object still has a mask value between 0 and 7.
      mask;
      // The modules of this QR Code (false = light, true = dark).
      // Immutable after constructor finishes. Accessed through getModule().
      modules = [];
      // Indicates function modules that are not subjected to masking. Discarded when constructor finishes.
      isFunction = [];
      /*-- Accessor methods --*/
      // Returns the color of the module (pixel) at the given coordinates, which is false
      // for light or true for dark. The top left corner has the coordinates (x=0, y=0).
      // If the given coordinates are out of bounds, then false (light) is returned.
      getModule(x, y) {
        return 0 <= x && x < this.size && 0 <= y && y < this.size && this.modules[y][x];
      }
      /*-- Private helper methods for constructor: Drawing function modules --*/
      // Reads this object's version field, and draws and marks all function modules.
      drawFunctionPatterns() {
        for (let i = 0; i < this.size; i++) {
          this.setFunctionModule(6, i, i % 2 == 0);
          this.setFunctionModule(i, 6, i % 2 == 0);
        }
        this.drawFinderPattern(3, 3);
        this.drawFinderPattern(this.size - 4, 3);
        this.drawFinderPattern(3, this.size - 4);
        const alignPatPos = this.getAlignmentPatternPositions();
        const numAlign = alignPatPos.length;
        for (let i = 0; i < numAlign; i++) {
          for (let j = 0; j < numAlign; j++) {
            if (!(i == 0 && j == 0 || i == 0 && j == numAlign - 1 || i == numAlign - 1 && j == 0))
              this.drawAlignmentPattern(alignPatPos[i], alignPatPos[j]);
          }
        }
        this.drawFormatBits(0);
        this.drawVersion();
      }
      // Draws two copies of the format bits (with its own error correction code)
      // based on the given mask and this object's error correction level field.
      drawFormatBits(mask) {
        const data = this.errorCorrectionLevel.formatBits << 3 | mask;
        let rem = data;
        for (let i = 0; i < 10; i++)
          rem = rem << 1 ^ (rem >>> 9) * 1335;
        const bits = (data << 10 | rem) ^ 21522;
        assert(bits >>> 15 == 0);
        for (let i = 0; i <= 5; i++)
          this.setFunctionModule(8, i, getBit(bits, i));
        this.setFunctionModule(8, 7, getBit(bits, 6));
        this.setFunctionModule(8, 8, getBit(bits, 7));
        this.setFunctionModule(7, 8, getBit(bits, 8));
        for (let i = 9; i < 15; i++)
          this.setFunctionModule(14 - i, 8, getBit(bits, i));
        for (let i = 0; i < 8; i++)
          this.setFunctionModule(this.size - 1 - i, 8, getBit(bits, i));
        for (let i = 8; i < 15; i++)
          this.setFunctionModule(8, this.size - 15 + i, getBit(bits, i));
        this.setFunctionModule(8, this.size - 8, true);
      }
      // Draws two copies of the version bits (with its own error correction code),
      // based on this object's version field, iff 7 <= version <= 40.
      drawVersion() {
        if (this.version < 7)
          return;
        let rem = this.version;
        for (let i = 0; i < 12; i++)
          rem = rem << 1 ^ (rem >>> 11) * 7973;
        const bits = this.version << 12 | rem;
        assert(bits >>> 18 == 0);
        for (let i = 0; i < 18; i++) {
          const color = getBit(bits, i);
          const a = this.size - 11 + i % 3;
          const b = Math.floor(i / 3);
          this.setFunctionModule(a, b, color);
          this.setFunctionModule(b, a, color);
        }
      }
      // Draws a 9*9 finder pattern including the border separator,
      // with the center module at (x, y). Modules can be out of bounds.
      drawFinderPattern(x, y) {
        for (let dy = -4; dy <= 4; dy++) {
          for (let dx = -4; dx <= 4; dx++) {
            const dist = Math.max(Math.abs(dx), Math.abs(dy));
            const xx = x + dx;
            const yy = y + dy;
            if (0 <= xx && xx < this.size && 0 <= yy && yy < this.size)
              this.setFunctionModule(xx, yy, dist != 2 && dist != 4);
          }
        }
      }
      // Draws a 5*5 alignment pattern, with the center module
      // at (x, y). All modules must be in bounds.
      drawAlignmentPattern(x, y) {
        for (let dy = -2; dy <= 2; dy++) {
          for (let dx = -2; dx <= 2; dx++)
            this.setFunctionModule(x + dx, y + dy, Math.max(Math.abs(dx), Math.abs(dy)) != 1);
        }
      }
      // Sets the color of a module and marks it as a function module.
      // Only used by the constructor. Coordinates must be in bounds.
      setFunctionModule(x, y, isDark) {
        this.modules[y][x] = isDark;
        this.isFunction[y][x] = true;
      }
      /*-- Private helper methods for constructor: Codewords and masking --*/
      // Returns a new byte string representing the given data with the appropriate error correction
      // codewords appended to it, based on this object's version and error correction level.
      addEccAndInterleave(data) {
        const ver = this.version;
        const ecl = this.errorCorrectionLevel;
        if (data.length != QrCode.getNumDataCodewords(ver, ecl))
          throw new RangeError("Invalid argument");
        const numBlocks = QrCode.NUM_ERROR_CORRECTION_BLOCKS[ecl.ordinal][ver];
        const blockEccLen = QrCode.ECC_CODEWORDS_PER_BLOCK[ecl.ordinal][ver];
        const rawCodewords = Math.floor(QrCode.getNumRawDataModules(ver) / 8);
        const numShortBlocks = numBlocks - rawCodewords % numBlocks;
        const shortBlockLen = Math.floor(rawCodewords / numBlocks);
        let blocks = [];
        const rsDiv = QrCode.reedSolomonComputeDivisor(blockEccLen);
        for (let i = 0, k = 0; i < numBlocks; i++) {
          let dat = data.slice(k, k + shortBlockLen - blockEccLen + (i < numShortBlocks ? 0 : 1));
          k += dat.length;
          const ecc = QrCode.reedSolomonComputeRemainder(dat, rsDiv);
          if (i < numShortBlocks)
            dat.push(0);
          blocks.push(dat.concat(ecc));
        }
        let result = [];
        for (let i = 0; i < blocks[0].length; i++) {
          blocks.forEach((block, j) => {
            if (i != shortBlockLen - blockEccLen || j >= numShortBlocks)
              result.push(block[i]);
          });
        }
        assert(result.length == rawCodewords);
        return result;
      }
      // Draws the given sequence of 8-bit codewords (data and error correction) onto the entire
      // data area of this QR Code. Function modules need to be marked off before this is called.
      drawCodewords(data) {
        if (data.length != Math.floor(QrCode.getNumRawDataModules(this.version) / 8))
          throw new RangeError("Invalid argument");
        let i = 0;
        for (let right = this.size - 1; right >= 1; right -= 2) {
          if (right == 6)
            right = 5;
          for (let vert = 0; vert < this.size; vert++) {
            for (let j = 0; j < 2; j++) {
              const x = right - j;
              const upward = (right + 1 & 2) == 0;
              const y = upward ? this.size - 1 - vert : vert;
              if (!this.isFunction[y][x] && i < data.length * 8) {
                this.modules[y][x] = getBit(data[i >>> 3], 7 - (i & 7));
                i++;
              }
            }
          }
        }
        assert(i == data.length * 8);
      }
      // XORs the codeword modules in this QR Code with the given mask pattern.
      // The function modules must be marked and the codeword bits must be drawn
      // before masking. Due to the arithmetic of XOR, calling applyMask() with
      // the same mask value a second time will undo the mask. A final well-formed
      // QR Code needs exactly one (not zero, two, etc.) mask applied.
      applyMask(mask) {
        if (mask < 0 || mask > 7)
          throw new RangeError("Mask value out of range");
        for (let y = 0; y < this.size; y++) {
          for (let x = 0; x < this.size; x++) {
            let invert;
            switch (mask) {
              case 0:
                invert = (x + y) % 2 == 0;
                break;
              case 1:
                invert = y % 2 == 0;
                break;
              case 2:
                invert = x % 3 == 0;
                break;
              case 3:
                invert = (x + y) % 3 == 0;
                break;
              case 4:
                invert = (Math.floor(x / 3) + Math.floor(y / 2)) % 2 == 0;
                break;
              case 5:
                invert = x * y % 2 + x * y % 3 == 0;
                break;
              case 6:
                invert = (x * y % 2 + x * y % 3) % 2 == 0;
                break;
              case 7:
                invert = ((x + y) % 2 + x * y % 3) % 2 == 0;
                break;
              default:
                throw new Error("Unreachable");
            }
            if (!this.isFunction[y][x] && invert)
              this.modules[y][x] = !this.modules[y][x];
          }
        }
      }
      // Calculates and returns the penalty score based on state of this QR Code's current modules.
      // This is used by the automatic mask choice algorithm to find the mask pattern that yields the lowest score.
      getPenaltyScore() {
        let result = 0;
        for (let y = 0; y < this.size; y++) {
          let runColor = false;
          let runX = 0;
          let runHistory = [0, 0, 0, 0, 0, 0, 0];
          for (let x = 0; x < this.size; x++) {
            if (this.modules[y][x] == runColor) {
              runX++;
              if (runX == 5)
                result += QrCode.PENALTY_N1;
              else if (runX > 5)
                result++;
            } else {
              this.finderPenaltyAddHistory(runX, runHistory);
              if (!runColor)
                result += this.finderPenaltyCountPatterns(runHistory) * QrCode.PENALTY_N3;
              runColor = this.modules[y][x];
              runX = 1;
            }
          }
          result += this.finderPenaltyTerminateAndCount(runColor, runX, runHistory) * QrCode.PENALTY_N3;
        }
        for (let x = 0; x < this.size; x++) {
          let runColor = false;
          let runY = 0;
          let runHistory = [0, 0, 0, 0, 0, 0, 0];
          for (let y = 0; y < this.size; y++) {
            if (this.modules[y][x] == runColor) {
              runY++;
              if (runY == 5)
                result += QrCode.PENALTY_N1;
              else if (runY > 5)
                result++;
            } else {
              this.finderPenaltyAddHistory(runY, runHistory);
              if (!runColor)
                result += this.finderPenaltyCountPatterns(runHistory) * QrCode.PENALTY_N3;
              runColor = this.modules[y][x];
              runY = 1;
            }
          }
          result += this.finderPenaltyTerminateAndCount(runColor, runY, runHistory) * QrCode.PENALTY_N3;
        }
        for (let y = 0; y < this.size - 1; y++) {
          for (let x = 0; x < this.size - 1; x++) {
            const color = this.modules[y][x];
            if (color == this.modules[y][x + 1] && color == this.modules[y + 1][x] && color == this.modules[y + 1][x + 1])
              result += QrCode.PENALTY_N2;
          }
        }
        let dark = 0;
        for (const row of this.modules)
          dark = row.reduce((sum, color) => sum + (color ? 1 : 0), dark);
        const total = this.size * this.size;
        const k = Math.ceil(Math.abs(dark * 20 - total * 10) / total) - 1;
        assert(0 <= k && k <= 9);
        result += k * QrCode.PENALTY_N4;
        assert(0 <= result && result <= 2568888);
        return result;
      }
      /*-- Private helper functions --*/
      // Returns an ascending list of positions of alignment patterns for this version number.
      // Each position is in the range [0,177), and are used on both the x and y axes.
      // This could be implemented as lookup table of 40 variable-length lists of integers.
      getAlignmentPatternPositions() {
        if (this.version == 1)
          return [];
        else {
          const numAlign = Math.floor(this.version / 7) + 2;
          const step = this.version == 32 ? 26 : Math.ceil((this.version * 4 + 4) / (numAlign * 2 - 2)) * 2;
          let result = [6];
          for (let pos = this.size - 7; result.length < numAlign; pos -= step)
            result.splice(1, 0, pos);
          return result;
        }
      }
      // Returns the number of data bits that can be stored in a QR Code of the given version number, after
      // all function modules are excluded. This includes remainder bits, so it might not be a multiple of 8.
      // The result is in the range [208, 29648]. This could be implemented as a 40-entry lookup table.
      static getNumRawDataModules(ver) {
        if (ver < QrCode.MIN_VERSION || ver > QrCode.MAX_VERSION)
          throw new RangeError("Version number out of range");
        let result = (16 * ver + 128) * ver + 64;
        if (ver >= 2) {
          const numAlign = Math.floor(ver / 7) + 2;
          result -= (25 * numAlign - 10) * numAlign - 55;
          if (ver >= 7)
            result -= 36;
        }
        assert(208 <= result && result <= 29648);
        return result;
      }
      // Returns the number of 8-bit data (i.e. not error correction) codewords contained in any
      // QR Code of the given version number and error correction level, with remainder bits discarded.
      // This stateless pure function could be implemented as a (40*4)-cell lookup table.
      static getNumDataCodewords(ver, ecl) {
        return Math.floor(QrCode.getNumRawDataModules(ver) / 8) - QrCode.ECC_CODEWORDS_PER_BLOCK[ecl.ordinal][ver] * QrCode.NUM_ERROR_CORRECTION_BLOCKS[ecl.ordinal][ver];
      }
      // Returns a Reed-Solomon ECC generator polynomial for the given degree. This could be
      // implemented as a lookup table over all possible parameter values, instead of as an algorithm.
      static reedSolomonComputeDivisor(degree) {
        if (degree < 1 || degree > 255)
          throw new RangeError("Degree out of range");
        let result = [];
        for (let i = 0; i < degree - 1; i++)
          result.push(0);
        result.push(1);
        let root = 1;
        for (let i = 0; i < degree; i++) {
          for (let j = 0; j < result.length; j++) {
            result[j] = QrCode.reedSolomonMultiply(result[j], root);
            if (j + 1 < result.length)
              result[j] ^= result[j + 1];
          }
          root = QrCode.reedSolomonMultiply(root, 2);
        }
        return result;
      }
      // Returns the Reed-Solomon error correction codeword for the given data and divisor polynomials.
      static reedSolomonComputeRemainder(data, divisor) {
        let result = divisor.map((_) => 0);
        for (const b of data) {
          const factor = b ^ result.shift();
          result.push(0);
          divisor.forEach((coef, i) => result[i] ^= QrCode.reedSolomonMultiply(coef, factor));
        }
        return result;
      }
      // Returns the product of the two given field elements modulo GF(2^8/0x11D). The arguments and result
      // are unsigned 8-bit integers. This could be implemented as a lookup table of 256*256 entries of uint8.
      static reedSolomonMultiply(x, y) {
        if (x >>> 8 != 0 || y >>> 8 != 0)
          throw new RangeError("Byte out of range");
        let z = 0;
        for (let i = 7; i >= 0; i--) {
          z = z << 1 ^ (z >>> 7) * 285;
          z ^= (y >>> i & 1) * x;
        }
        assert(z >>> 8 == 0);
        return z;
      }
      // Can only be called immediately after a light run is added, and
      // returns either 0, 1, or 2. A helper function for getPenaltyScore().
      finderPenaltyCountPatterns(runHistory) {
        const n = runHistory[1];
        assert(n <= this.size * 3);
        const core = n > 0 && runHistory[2] == n && runHistory[3] == n * 3 && runHistory[4] == n && runHistory[5] == n;
        return (core && runHistory[0] >= n * 4 && runHistory[6] >= n ? 1 : 0) + (core && runHistory[6] >= n * 4 && runHistory[0] >= n ? 1 : 0);
      }
      // Must be called at the end of a line (row or column) of modules. A helper function for getPenaltyScore().
      finderPenaltyTerminateAndCount(currentRunColor, currentRunLength, runHistory) {
        if (currentRunColor) {
          this.finderPenaltyAddHistory(currentRunLength, runHistory);
          currentRunLength = 0;
        }
        currentRunLength += this.size;
        this.finderPenaltyAddHistory(currentRunLength, runHistory);
        return this.finderPenaltyCountPatterns(runHistory);
      }
      // Pushes the given value to the front and drops the last value. A helper function for getPenaltyScore().
      finderPenaltyAddHistory(currentRunLength, runHistory) {
        if (runHistory[0] == 0)
          currentRunLength += this.size;
        runHistory.pop();
        runHistory.unshift(currentRunLength);
      }
      /*-- Constants and tables --*/
      // The minimum version number supported in the QR Code Model 2 standard.
      static MIN_VERSION = 1;
      // The maximum version number supported in the QR Code Model 2 standard.
      static MAX_VERSION = 40;
      // For use in getPenaltyScore(), when evaluating which mask is best.
      static PENALTY_N1 = 3;
      static PENALTY_N2 = 3;
      static PENALTY_N3 = 40;
      static PENALTY_N4 = 10;
      static ECC_CODEWORDS_PER_BLOCK = [
        // Version: (note that index 0 is for padding, and is set to an illegal value)
        //0,  1,  2,  3,  4,  5,  6,  7,  8,  9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 34, 35, 36, 37, 38, 39, 40    Error correction level
        [-1, 7, 10, 15, 20, 26, 18, 20, 24, 30, 18, 20, 24, 26, 30, 22, 24, 28, 30, 28, 28, 28, 28, 30, 30, 26, 28, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30],
        // Low
        [-1, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26, 30, 22, 22, 24, 24, 28, 28, 26, 26, 26, 26, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28, 28],
        // Medium
        [-1, 13, 22, 18, 26, 18, 24, 18, 22, 20, 24, 28, 26, 24, 20, 30, 24, 28, 28, 26, 30, 28, 30, 30, 30, 30, 28, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30],
        // Quartile
        [-1, 17, 28, 22, 16, 22, 28, 26, 26, 24, 28, 24, 28, 22, 24, 24, 30, 28, 28, 26, 28, 30, 24, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30, 30]
        // High
      ];
      static NUM_ERROR_CORRECTION_BLOCKS = [
        // Version: (note that index 0 is for padding, and is set to an illegal value)
        //0, 1, 2, 3, 4, 5, 6, 7, 8, 9,10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32, 33, 34, 35, 36, 37, 38, 39, 40    Error correction level
        [-1, 1, 1, 1, 1, 1, 2, 2, 2, 2, 4, 4, 4, 4, 4, 6, 6, 6, 6, 7, 8, 8, 9, 9, 10, 12, 12, 12, 13, 14, 15, 16, 17, 18, 19, 19, 20, 21, 22, 24, 25],
        // Low
        [-1, 1, 1, 1, 2, 2, 4, 4, 4, 5, 5, 5, 8, 9, 9, 10, 10, 11, 13, 14, 16, 17, 17, 18, 20, 21, 23, 25, 26, 28, 29, 31, 33, 35, 37, 38, 40, 43, 45, 47, 49],
        // Medium
        [-1, 1, 1, 2, 2, 4, 4, 6, 6, 8, 8, 8, 10, 12, 16, 12, 17, 16, 18, 21, 20, 23, 23, 25, 27, 29, 34, 34, 35, 38, 40, 43, 45, 48, 51, 53, 56, 59, 62, 65, 68],
        // Quartile
        [-1, 1, 1, 2, 4, 4, 4, 5, 6, 8, 8, 11, 11, 16, 16, 18, 16, 19, 21, 25, 25, 25, 34, 30, 32, 35, 37, 40, 42, 45, 48, 51, 54, 57, 60, 63, 66, 70, 74, 77, 81]
        // High
      ];
    }
    qrcodegen2.QrCode = QrCode;
    function appendBits(val, len, bb) {
      if (len < 0 || len > 31 || val >>> len != 0)
        throw new RangeError("Value out of range");
      for (let i = len - 1; i >= 0; i--)
        bb.push(val >>> i & 1);
    }
    function getBit(x, i) {
      return (x >>> i & 1) != 0;
    }
    function assert(cond) {
      if (!cond)
        throw new Error("Assertion error");
    }
    class QrSegment {
      /*-- Constructor (low level) and fields --*/
      // Creates a new QR Code segment with the given attributes and data.
      // The character count (numChars) must agree with the mode and the bit buffer length,
      // but the constraint isn't checked. The given bit buffer is cloned and stored.
      constructor(mode, numChars, bitData) {
        this.mode = mode;
        this.numChars = numChars;
        this.bitData = bitData;
        if (numChars < 0)
          throw new RangeError("Invalid argument");
        this.bitData = bitData.slice();
      }
      mode;
      numChars;
      bitData;
      /*-- Static factory functions (mid level) --*/
      // Returns a segment representing the given binary data encoded in
      // byte mode. All input byte arrays are acceptable. Any text string
      // can be converted to UTF-8 bytes and encoded as a byte mode segment.
      static makeBytes(data) {
        let bb = [];
        for (const b of data)
          appendBits(b, 8, bb);
        return new QrSegment(QrSegment.Mode.BYTE, data.length, bb);
      }
      // Returns a segment representing the given string of decimal digits encoded in numeric mode.
      static makeNumeric(digits) {
        if (!QrSegment.isNumeric(digits))
          throw new RangeError("String contains non-numeric characters");
        let bb = [];
        for (let i = 0; i < digits.length; ) {
          const n = Math.min(digits.length - i, 3);
          appendBits(parseInt(digits.substr(i, n), 10), n * 3 + 1, bb);
          i += n;
        }
        return new QrSegment(QrSegment.Mode.NUMERIC, digits.length, bb);
      }
      // Returns a segment representing the given text string encoded in alphanumeric mode.
      // The characters allowed are: 0 to 9, A to Z (uppercase only), space,
      // dollar, percent, asterisk, plus, hyphen, period, slash, colon.
      static makeAlphanumeric(text) {
        if (!QrSegment.isAlphanumeric(text))
          throw new RangeError("String contains unencodable characters in alphanumeric mode");
        let bb = [];
        let i;
        for (i = 0; i + 2 <= text.length; i += 2) {
          let temp = QrSegment.ALPHANUMERIC_CHARSET.indexOf(text.charAt(i)) * 45;
          temp += QrSegment.ALPHANUMERIC_CHARSET.indexOf(text.charAt(i + 1));
          appendBits(temp, 11, bb);
        }
        if (i < text.length)
          appendBits(QrSegment.ALPHANUMERIC_CHARSET.indexOf(text.charAt(i)), 6, bb);
        return new QrSegment(QrSegment.Mode.ALPHANUMERIC, text.length, bb);
      }
      // Returns a new mutable list of zero or more segments to represent the given Unicode text string.
      // The result may use various segment modes and switch modes to optimize the length of the bit stream.
      static makeSegments(text) {
        if (text == "")
          return [];
        else if (QrSegment.isNumeric(text))
          return [QrSegment.makeNumeric(text)];
        else if (QrSegment.isAlphanumeric(text))
          return [QrSegment.makeAlphanumeric(text)];
        else
          return [QrSegment.makeBytes(QrSegment.toUtf8ByteArray(text))];
      }
      // Returns a segment representing an Extended Channel Interpretation
      // (ECI) designator with the given assignment value.
      static makeEci(assignVal) {
        let bb = [];
        if (assignVal < 0)
          throw new RangeError("ECI assignment value out of range");
        else if (assignVal < 1 << 7)
          appendBits(assignVal, 8, bb);
        else if (assignVal < 1 << 14) {
          appendBits(2, 2, bb);
          appendBits(assignVal, 14, bb);
        } else if (assignVal < 1e6) {
          appendBits(6, 3, bb);
          appendBits(assignVal, 21, bb);
        } else
          throw new RangeError("ECI assignment value out of range");
        return new QrSegment(QrSegment.Mode.ECI, 0, bb);
      }
      // Tests whether the given string can be encoded as a segment in numeric mode.
      // A string is encodable iff each character is in the range 0 to 9.
      static isNumeric(text) {
        return QrSegment.NUMERIC_REGEX.test(text);
      }
      // Tests whether the given string can be encoded as a segment in alphanumeric mode.
      // A string is encodable iff each character is in the following set: 0 to 9, A to Z
      // (uppercase only), space, dollar, percent, asterisk, plus, hyphen, period, slash, colon.
      static isAlphanumeric(text) {
        return QrSegment.ALPHANUMERIC_REGEX.test(text);
      }
      /*-- Methods --*/
      // Returns a new copy of the data bits of this segment.
      getData() {
        return this.bitData.slice();
      }
      // (Package-private) Calculates and returns the number of bits needed to encode the given segments at
      // the given version. The result is infinity if a segment has too many characters to fit its length field.
      static getTotalBits(segs, version) {
        let result = 0;
        for (const seg of segs) {
          const ccbits = seg.mode.numCharCountBits(version);
          if (seg.numChars >= 1 << ccbits)
            return Infinity;
          result += 4 + ccbits + seg.bitData.length;
        }
        return result;
      }
      // Returns a new array of bytes representing the given string encoded in UTF-8.
      static toUtf8ByteArray(str) {
        str = encodeURI(str);
        let result = [];
        for (let i = 0; i < str.length; i++) {
          if (str.charAt(i) != "%")
            result.push(str.charCodeAt(i));
          else {
            result.push(parseInt(str.substr(i + 1, 2), 16));
            i += 2;
          }
        }
        return result;
      }
      /*-- Constants --*/
      // Describes precisely all strings that are encodable in numeric mode.
      static NUMERIC_REGEX = /^[0-9]*$/;
      // Describes precisely all strings that are encodable in alphanumeric mode.
      static ALPHANUMERIC_REGEX = /^[A-Z0-9 $%*+.\/:-]*$/;
      // The set of all legal characters in alphanumeric mode,
      // where each character value maps to the index in the string.
      static ALPHANUMERIC_CHARSET = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ $%*+-./:";
    }
    qrcodegen2.QrSegment = QrSegment;
  })(qrcodegen || (qrcodegen = {}));
  ((qrcodegen2) => {
    let QrCode;
    ((QrCode2) => {
      class Ecc {
        // The QR Code can tolerate about 30% erroneous codewords
        /*-- Constructor and fields --*/
        constructor(ordinal, formatBits) {
          this.ordinal = ordinal;
          this.formatBits = formatBits;
        }
        ordinal;
        formatBits;
        /*-- Constants --*/
        static LOW = new Ecc(0, 1);
        // The QR Code can tolerate about  7% erroneous codewords
        static MEDIUM = new Ecc(1, 0);
        // The QR Code can tolerate about 15% erroneous codewords
        static QUARTILE = new Ecc(2, 3);
        // The QR Code can tolerate about 25% erroneous codewords
        static HIGH = new Ecc(3, 2);
      }
      QrCode2.Ecc = Ecc;
    })(QrCode = qrcodegen2.QrCode || (qrcodegen2.QrCode = {}));
  })(qrcodegen || (qrcodegen = {}));
  ((qrcodegen2) => {
    let QrSegment;
    ((QrSegment2) => {
      class Mode {
        /*-- Constructor and fields --*/
        constructor(modeBits, numBitsCharCount) {
          this.modeBits = modeBits;
          this.numBitsCharCount = numBitsCharCount;
        }
        modeBits;
        numBitsCharCount;
        /*-- Constants --*/
        static NUMERIC = new Mode(1, [10, 12, 14]);
        static ALPHANUMERIC = new Mode(2, [9, 11, 13]);
        static BYTE = new Mode(4, [8, 16, 16]);
        static KANJI = new Mode(8, [8, 10, 12]);
        static ECI = new Mode(7, [0, 0, 0]);
        /*-- Method --*/
        // (Package-private) Returns the bit width of the character count field for a segment in
        // this mode in a QR Code at the given version number. The result is in the range [0, 16].
        numCharCountBits(ver) {
          return this.numBitsCharCount[Math.floor((ver + 7) / 17)];
        }
      }
      QrSegment2.Mode = Mode;
    })(QrSegment = qrcodegen2.QrSegment || (qrcodegen2.QrSegment = {}));
  })(qrcodegen || (qrcodegen = {}));
  return __toCommonJS(entry_exports);
})();
