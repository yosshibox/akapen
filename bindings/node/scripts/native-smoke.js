'use strict';

const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');

const platformTag = {
  darwin: 'darwin',
  linux: 'linux',
  win32: 'win32',
}[process.platform];
const artifactPattern = platformTag
  ? new RegExp(
    `^akapen-core-node\\.${platformTag}-${process.arch}` +
    (platformTag === 'linux' ? '-(?:gnu|musl)' : platformTag === 'win32' ? '-msvc' : '') +
    '\\.node$',
  )
  : null;
const packageDir = path.join(__dirname, '..');
const artifact = artifactPattern && fs.readdirSync(packageDir)
  .map((name) => path.join(packageDir, name))
  .find((candidate) => artifactPattern.test(path.basename(candidate)));

if (!artifact) {
  console.log(`SKIP: no local ${process.platform}/${process.arch} Akapen .node artifact`);
  process.exit(0);
}

const addon = require(artifact);
assert.equal(typeof addon.AkapenSession, 'function', 'native addon must export AkapenSession');
const Session = addon.AkapenSession;
const session = new Session(64, 48);
assert.equal(session.width, 64);
assert.equal(session.height, 48);

session.pointer(8, 8, 1, 0, 0);
session.pointer(24, 20, 0.25, 0, 1);
session.pointer(40, 32, 0.8, 0, 2);

const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'akapen-node-smoke-'));
try {
  session.exportToDir(dir, 'smoke');
  for (const file of ['smoke.review.png', 'smoke.strokes.png', 'smoke.strokes.json']) {
    assert.ok(fs.statSync(path.join(dir, file)).isFile(), `missing ${file}`);
  }
  console.log(`PASS: loaded ${path.basename(artifact)}, drew varying pressure, exported three files`);
} finally {
  fs.rmSync(dir, { recursive: true, force: true });
}
