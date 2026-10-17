// Packaged renderer contract without browser/network, run: node --test tests/ssh_terminal_output.test.cjs
const {test} = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
test('resume resubmits one bounded chunk without duplicate output or premature acknowledgement', () => {
  const writes = [], callbacks = [], acknowledgements = [];
  class Terminal {
    constructor() { this.parser = {registerOscHandler() {}}; }
    loadAddon() {} open() {} onData() {} onResize() {}
    write(bytes, complete) { writes.push(Buffer.from(bytes).toString()); callbacks.push(complete); }
  }
  const window = {addEventListener() {}};
  const context = {
    window, Terminal, TextDecoder, Uint8Array, atob,
    ResizeObserver: class {observe() {}},
    FitAddon: {FitAddon: class {}},
    Element: class {replaceChildren() {}},
    document: {getElementById() {return {};}, body: {clientWidth:0, clientHeight:0}},
    RemoteSsh: {ready() {}, unavailable() {throw Error('Packaged renderer unavailable');}, ack(sequence) {acknowledgements.push(sequence);}}
  };
  vm.runInNewContext(fs.readFileSync(path.join(__dirname, '../src/RemoteDesk.Android/app/src/main/assets/ssh/terminal.js'), 'utf8'), context);
  const encoded = Buffer.from('中文-output').toString('base64');
  window.sshOutput(encoded, 1);
  window.sshOutput(encoded, 1); // Resume before xterm has finished parsing.
  assert.deepEqual(writes, ['中文-output']);
  assert.deepEqual(acknowledgements, []);
  callbacks.shift()();
  window.sshOutput(encoded, 1); // Resume after parsing but before Java observes the acknowledgement.
  assert.deepEqual(writes, ['中文-output']);
  assert.deepEqual(acknowledgements, [1, 1]);
  window.sshOutput(Buffer.from('next').toString('base64'), 2);
  callbacks.shift()();
  assert.deepEqual(writes, ['中文-output', 'next']);
  assert.deepEqual(acknowledgements, [1, 1, 2]);
});
