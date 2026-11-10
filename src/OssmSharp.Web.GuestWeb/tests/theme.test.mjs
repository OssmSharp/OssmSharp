import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { test } from "node:test";
import { createContext, SourceTextModule } from "node:vm";

const common = new URL("../../OssmSharp.Web.Common/wwwroot/", import.meta.url);
const css = await readFile(new URL("controls.css", common), "utf8");
const source = await readFile(new URL("theme.js", common), "utf8");

function tokens(selector) {
  const start = css.indexOf(`${selector} {`);
  assert.notEqual(start, -1, selector);
  const block = css.slice(start, css.indexOf("}", start));
  return Object.fromEntries(Array.from(block.matchAll(/(--[\w-]+):\s*([^;]+);/g), ([, name, value]) => [name, value]));
}

function resolve(palette, token) {
  const value = palette[token];
  assert.ok(value, `Missing ${token}`);
  const alias = /^var\((--[\w-]+)\)$/.exec(value);
  return alias ? resolve(palette, alias[1]) : value;
}

function luminance(hex) {
  assert.match(hex, /^#[\da-f]{6}$/i);
  return hex.slice(1).match(/../g).map((value) => {
    const channel = parseInt(value, 16) / 255;
    return channel <= .04045 ? channel / 12.92 : ((channel + .055) / 1.055) ** 2.4;
  }).reduce((sum, value, index) => sum + value * [.2126, .7152, .0722][index], 0);
}

function contrast(palette, foreground, background, minimum, theme) {
  const values = [foreground, background].map((token) => luminance(resolve(palette, token))).sort((a, b) => a - b);
  const ratio = (values[1] + .05) / (values[0] + .05);
  assert.ok(ratio >= minimum, `${theme} ${foreground} on ${background}: ${ratio.toFixed(2)}:1 < ${minimum}:1`);
}

test("shared palettes meet AA text and control contrast in light and dark themes", () => {
  const light = tokens(":root");
  const dark = { ...light, ...tokens('[data-theme="dark"]') };
  for (const [theme, palette] of [["light", light], ["dark", dark]]) {
    for (const background of ["--background", "--surface", "--field"]) {
      for (const foreground of ["--text", "--muted", "--accent", "--success", "--warning", "--danger", "--neutral"])
        contrast(palette, foreground, background, 4.5, theme);
      for (const foreground of ["--border", "--line", "--focus"])
        contrast(palette, foreground, background, 3, theme);
    }
    for (const state of ["success", "warning", "danger", "neutral"]) {
      contrast(palette, "--text", `--${state}-bg`, 4.5, theme);
      contrast(palette, `--${state}`, `--${state}-bg`, 3, theme);
    }
    for (const state of ["success", "danger"])
      contrast(palette, "--status-ink", `--${state}`, 4.5, theme);
    contrast(palette, "--warning-ink", "--warning-fill", 4.5, theme);
    contrast(palette, "--accent-ink", "--accent-fill", 4.5, theme);
    assert.equal(resolve(palette, "--radius"), "0");
  }
});

async function themeHarness(storage) {
  let click;
  const root = { dataset: {} };
  class Element {
    constructor(toggle) { this.toggle = toggle; }
    closest(selector) {
      assert.equal(selector, "[data-theme-toggle]");
      return this.toggle ? this : null;
    }
  }
  const context = createContext({
    Element, localStorage: storage,
    document: {
      documentElement: root,
      addEventListener: (name, callback) => { assert.equal(name, "click"); click = callback; },
    },
  });
  const module = new SourceTextModule(source, { context });
  await module.link(() => assert.fail("Unexpected theme import"));
  await module.evaluate();
  module.namespace.initializeTheme();
  return { root, toggle: () => click({ target: new Element(true) }), unrelated: () => click({ target: new Element(false) }) };
}

test("shared theme restores toggles and persists the selected mode", async () => {
  for (const saved of ["light", "dark", null, "invalid"]) {
    const storage = new Map([["ossm-dashboard-theme", saved]]);
    const h = await themeHarness({
      getItem: (key) => storage.get(key),
      setItem: (key, value) => storage.set(key, value),
    });
    const initial = saved === "light" ? "light" : "dark";
    assert.equal(h.root.dataset.theme, initial);
    h.unrelated();
    assert.equal(h.root.dataset.theme, initial);
    h.toggle();
    const next = initial === "light" ? "dark" : "light";
    assert.equal(h.root.dataset.theme, next);
    assert.equal(storage.get("ossm-dashboard-theme"), next);
    h.toggle();
    assert.equal(h.root.dataset.theme, initial);
    assert.equal(storage.get("ossm-dashboard-theme"), initial);
  }
});

test("shared theme remains usable when browser storage is unavailable", async () => {
  const h = await themeHarness({
    getItem() { throw new Error("Storage unavailable"); },
    setItem() { throw new Error("Storage unavailable"); },
  });
  assert.equal(h.root.dataset.theme, "dark");
  h.toggle();
  assert.equal(h.root.dataset.theme, "light");
  h.toggle();
  assert.equal(h.root.dataset.theme, "dark");
});
