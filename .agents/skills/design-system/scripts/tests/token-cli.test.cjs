const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { test } = require('node:test');
const { runInNewContext } = require('node:vm');

const scripts = path.resolve(__dirname, '..');
const starter = fs.readFileSync(path.join(scripts, '../templates/design-tokens-starter.json'), 'utf8');

function execute(script, args, filesystem, paths = path, cwd = '/') {
  const output = [];
  let exitCode = 0;
  try {
    runInNewContext(fs.readFileSync(path.join(scripts, script), 'utf8'), {
      require: name => name === 'fs' ? filesystem : paths,
      process: {
        argv: ['node', script, ...args],
        cwd: () => cwd,
        exit(code) { exitCode = code; throw new Error('CLI exit'); },
      },
      console: { log: value => output.push(value), error: value => output.push(value) },
    }, { timeout: 1000 });
  } catch (error) {
    if (error.message !== 'CLI exit') throw error;
  }
  return { output: output.join('\n'), exitCode };
}

for (const [platform, paths, cwd] of [
  ['Windows', path.win32, 'C:\\project\\nested'],
  ['POSIX', path.posix, '/project/nested'],
]) {
  test(`missing tokens terminate at the ${platform} root`, () => {
    const result = execute('embed-tokens.cjs', [], { existsSync: () => false }, paths, cwd);
    assert.equal(result.exitCode, 1);
    assert.match(result.output, /Could not find assets/);
  });
  test(`tokens at the ${platform} root are found`, () => {
    const root = paths.parse(cwd).root;
    const result = execute('embed-tokens.cjs', [], {
      existsSync: file => file === paths.join(root, 'assets', 'design-tokens.css'),
      readFileSync: () => ':root { --color-primary: green; }',
    }, paths, cwd);
    assert.equal(result.exitCode, 0);
    assert.match(result.output, /--color-primary: green/);
  });
}

test('component aliases follow semantic dark-theme overrides', () => {
  const result = execute('generate-tokens.cjs', ['--config', 'tokens.json'], {
    existsSync: () => true,
    readFileSync: () => starter,
  });
  assert.equal(result.exitCode, 0);
  assert.match(result.output, /--input-bg: var\(--color-background\);/);
  assert.match(result.output, /--input-border: var\(--color-border\);/);
  assert.match(result.output, /\.dark\s*\{[^}]*--color-background: var\(--primitive-color-gray-950\);/);
});

test('Tailwind compound color keys remain usable as utility names', () => {
  const result = execute('generate-tokens.cjs', ['--config', 'tokens.json', '--format', 'tailwind'], {
    existsSync: () => true,
    readFileSync: () => starter,
  });
  const context = { module: { exports: {} } };
  runInNewContext(result.output, context);
  assert.equal(context.module.exports.colors['primary-foreground'], 'var(--color-primary-foreground)');
  assert.equal(context.module.exports.colors['primary-hover'], 'var(--color-primary-hover)');
  assert.equal(context.module.exports.colors['primary.foreground'], undefined);
});
