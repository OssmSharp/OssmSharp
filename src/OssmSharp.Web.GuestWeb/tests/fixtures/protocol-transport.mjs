import assert from "node:assert/strict";
import { createHash, randomBytes } from "node:crypto";
import { EventEmitter } from "node:events";
import { request } from "node:http";

const guid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

/** Reads a bounded HTTP response, or an actual WebSocket upgrade with custom headers. */
export function exchange(origin, path, { method = "GET", headers = {}, body, upgrade = false } = {}) {
  return new Promise((resolve, reject) => {
    const key = randomBytes(16).toString("base64");
    const req = request(new URL(path, origin), {
      method,
      agent: false,
      headers: upgrade
        ? { ...headers, Connection: "Upgrade", Upgrade: "websocket", "Sec-WebSocket-Version": "13", "Sec-WebSocket-Key": key }
        : headers,
    });
    const deadline = setTimeout(() => req.destroy(new Error("HTTP adapter deadline exceeded")), 5_000);
    req.on("error", reject);
    req.on("close", () => clearTimeout(deadline));
    req.on("response", (response) => {
      let body = "";
      response.setEncoding("utf8");
      response.on("data", (chunk) => {
        body += chunk;
        if (body.length > 2_000_000) req.destroy(new Error("HTTP adapter response exceeds fixture limit"));
      });
      response.on("error", reject);
      response.on("end", () => resolve({ status: response.statusCode, headers: response.headers, body }));
    });
    req.on("upgrade", (response, socket, head) => {
      clearTimeout(deadline);
      try {
        assert.equal(response.headers["sec-websocket-accept"], createHash("sha1").update(key + guid).digest("base64"));
        resolve({ status: response.statusCode, headers: response.headers, socket, head });
      } catch (error) {
        socket.destroy();
        reject(error);
      }
    });
    req.end(body);
  });
}

/** Accepts the fake API collaborator's WebSocket handshake, not web-host middleware. */
export function acceptUpgrade(req, socket) {
  const accept = createHash("sha1").update(req.headers["sec-websocket-key"] + guid).digest("base64");
  socket.write(`HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: ${accept}\r\n\r\n`);
}

/** Owns RFC 6455 text/binary framing for the bounded SignalR fixture traffic. */
export class WireSocket extends EventEmitter {
  constructor(socket, { masked = false, head = Buffer.alloc(0) } = {}) {
    super();
    this.socket = socket;
    this.masked = masked;
    this.buffer = Buffer.alloc(0);
    this.fragments = [];
    this.closing = false;
    this.closed = new Promise((resolve) => socket.once("close", resolve));
    socket.on("data", (data) => this.consume(data));
    socket.on("error", (error) => {
      if (!this.closing || error.code !== "ECONNRESET") this.emit("fault", error);
    });
    if (head.length) queueMicrotask(() => this.consume(head));
  }

  send(data, opcode = typeof data === "string" ? 1 : 2) {
    const payload = Buffer.from(data);
    const extended = payload.length < 126 ? 0 : payload.length <= 65535 ? 2 : 8;
    const header = Buffer.alloc(2 + extended + (this.masked ? 4 : 0));
    header[0] = 0x80 | opcode;
    header[1] = (this.masked ? 0x80 : 0) | (extended === 0 ? payload.length : extended === 2 ? 126 : 127);
    if (extended === 2) header.writeUInt16BE(payload.length, 2);
    if (extended === 8) header.writeBigUInt64BE(BigInt(payload.length), 2);
    if (this.masked) {
      const mask = randomBytes(4);
      mask.copy(header, header.length - 4);
      for (let i = 0; i < payload.length; i++) payload[i] ^= mask[i % 4];
    }
    this.socket.write(Buffer.concat([header, payload]));
  }

  consume(data) {
    this.buffer = Buffer.concat([this.buffer, data]);
    while (this.buffer.length >= 2) {
      const first = this.buffer[0];
      const masked = (this.buffer[1] & 0x80) !== 0;
      let length = this.buffer[1] & 0x7f;
      let offset = 2;
      if (length === 126) {
        if (this.buffer.length < 4) return;
        length = this.buffer.readUInt16BE(2);
        offset = 4;
      } else if (length === 127) {
        if (this.buffer.length < 10) return;
        const wideLength = this.buffer.readBigUInt64BE(2);
        if (wideLength > 2_000_000n) {
          this.emit("fault", new Error("WebSocket payload exceeds fixture limit"));
          this.socket.destroy();
          return;
        }
        length = Number(wideLength);
        offset = 10;
      }
      const maskOffset = offset;
      if (masked) offset += 4;
      if (this.buffer.length < offset + length) return;
      const payload = Buffer.from(this.buffer.subarray(offset, offset + length));
      if (masked) for (let i = 0; i < length; i++) payload[i] ^= this.buffer[maskOffset + i % 4];
      this.buffer = this.buffer.subarray(offset + length);
      const opcode = first & 0x0f;
      if (opcode === 8) {
        this.closing = true;
        this.send(payload, 8);
        this.socket.end();
        return;
      }
      if (opcode === 9) {
        this.send(payload, 10);
        continue;
      }
      if (opcode === 10) continue;
      this.fragments.push(payload);
      if (first & 0x80) {
        this.emit("message", Buffer.concat(this.fragments));
        this.fragments = [];
      }
    }
  }

  async dispose() {
    this.closing = true;
    const deadline = setTimeout(() => this.socket.destroy(), 1_000);
    try {
      if (!this.socket.destroyed && this.socket.writable) this.send(Buffer.from([0x03, 0xe8]), 8);
      await this.closed;
    } finally {
      clearTimeout(deadline);
      this.buffer = Buffer.alloc(0);
      this.fragments = [];
    }
  }
}

// Blazor's binary hub messages need only these MessagePack primitives. No DOM,
// rendering, device logic, or application calculations are simulated here.
function pack(value) {
  if (value === null) return Buffer.from([0xc0]);
  if (typeof value === "boolean") return Buffer.from([value ? 0xc3 : 0xc2]);
  if (typeof value === "number") {
    if (value >= 0 && value < 128) return Buffer.from([value]);
    const result = Buffer.alloc(9);
    result[0] = 0xcb;
    result.writeDoubleBE(value, 1);
    return result;
  }
  if (typeof value === "string") {
    const bytes = Buffer.from(value);
    const header = Buffer.alloc(3);
    header[0] = 0xda;
    header.writeUInt16BE(bytes.length, 1);
    return Buffer.concat([header, bytes]);
  }
  if (Array.isArray(value)) return Buffer.concat([Buffer.from([0x90 + value.length]), ...value.map(pack)]);
  if (Object.keys(value).length === 0) return Buffer.from([0x80]);
  throw new Error("Unsupported fixture MessagePack value");
}

function unpack(buffer) {
  let offset = 0;
  const bytes = (length) => {
    const value = buffer.subarray(offset, offset + length);
    offset += length;
    return value;
  };
  const read = () => {
    const code = buffer[offset++];
    if (code < 128) return code;
    if (code >= 224) return code - 256;
    if ((code & 0xe0) === 0xa0) return bytes(code & 31).toString();
    if ((code & 0xf0) === 0x90) return Array.from({ length: code & 15 }, read);
    if (code === 0x80) return {};
    if (code === 0xc0) return null;
    if (code === 0xc2 || code === 0xc3) return code === 0xc3;
    const widths = { 0xc4: 1, 0xc5: 2, 0xc6: 4, 0xd9: 1, 0xda: 2, 0xdb: 4, 0xdc: 2, 0xdd: 4 };
    if (widths[code]) {
      const width = widths[code];
      const length = buffer.readUIntBE(offset, width);
      offset += width;
      if (code === 0xdc || code === 0xdd) return Array.from({ length }, read);
      const value = bytes(length);
      return code >= 0xd9 ? value.toString() : value;
    }
    const numeric = {
      0xcc: [1, "readUInt8"], 0xcd: [2, "readUInt16BE"], 0xce: [4, "readUInt32BE"],
      0xcf: [8, "readBigUInt64BE"], 0xd0: [1, "readInt8"], 0xd1: [2, "readInt16BE"],
      0xd2: [4, "readInt32BE"], 0xd3: [8, "readBigInt64BE"], 0xcb: [8, "readDoubleBE"],
    }[code];
    if (!numeric) throw new Error(`Unsupported fixture MessagePack tag ${code}`);
    const result = Number(buffer[numeric[1]](offset));
    offset += numeric[0];
    return result;
  };
  return read();
}

/** Encodes the binary SignalR envelope used by a real Blazor hub. */
export function hubMessage(message) {
  const body = pack(message);
  let length = body.length;
  const prefix = [];
  do {
    const byte = length & 0x7f;
    length >>>= 7;
    prefix.push(byte | (length ? 0x80 : 0));
  } while (length);
  return Buffer.concat([Buffer.from(prefix), body]);
}

/** Decodes complete binary hub envelopes, preserving any partial final envelope. */
export function readHubMessages(buffer) {
  const messages = [];
  let offset = 0;
  while (offset < buffer.length) {
    let length = 0;
    let shift = 0;
    let end = offset;
    do {
      if (end === buffer.length) return { messages, rest: buffer.subarray(offset) };
      const byte = buffer[end++];
      length |= (byte & 0x7f) << shift;
      shift += 7;
      if (!(byte & 0x80)) break;
      if (shift > 28) throw new Error("Invalid binary hub length");
    } while (true);
    if (buffer.length < end + length) break;
    messages.push(unpack(buffer.subarray(end, end + length)));
    offset = end + length;
  }
  return { messages, rest: buffer.subarray(offset) };
}
