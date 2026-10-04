'use strict';
(() => {
  if (typeof Terminal !== 'function' || typeof ResizeObserver !== 'function' ||
      typeof TextDecoder !== 'function' || typeof Promise.allSettled !== 'function' ||
      typeof Array.prototype.flatMap !== 'function' || typeof String.prototype.matchAll !== 'function' ||
      typeof Element.prototype.replaceChildren !== 'function') {
    RemoteSsh.unavailable(); return;
  }
  const terminal = new Terminal({
    cursorBlink:true, scrollback:2000, fontSize:15, fontFamily:'monospace',
    disableStdin:true, allowProposedApi:true, screenReaderMode:true,
    theme:{background:'#10151d', foreground:'#e7edf5'},
    // Remote OSC8 links are terminal content, never browser navigation.
    linkHandler:{activate:() => {}, hover:() => {}, leave:() => {}}
  });
  const fit = new FitAddon.FitAddon();
  terminal.loadAddon(fit);
  terminal.open(document.getElementById('terminal'));
  window.addEventListener('error', () => RemoteSsh.unavailable());
  window.addEventListener('unhandledrejection', () => RemoteSsh.unavailable());
  // Never let remote OSC52 read or write the Android clipboard.
  terminal.parser.registerOscHandler(52, () => true);
  let ctrl = false;
  let generation = 0;
  terminal.onData(data => {
    if (terminal.options.disableStdin) return;
    if (ctrl && data.length === 1) {
      const code = data.toUpperCase().charCodeAt(0);
      if (code >= 64 && code <= 95) data = String.fromCharCode(code - 64);
      else if (data === ' ') data = '\x00';
      else if (data === '?') data = '\x7f';
    }
    ctrl = false;
    RemoteSsh.ctrl(false);
    RemoteSsh.input(data, generation);
  });
  const resize = () => {
    if (document.body.clientWidth < 10 || document.body.clientHeight < 10) return;
    fit.fit();
    RemoteSsh.resize(terminal.cols, terminal.rows);
  };
  terminal.onResize(({cols,rows}) => RemoteSsh.resize(cols,rows));
  new ResizeObserver(resize).observe(document.getElementById('terminal'));
  window.sshOutput = (base64, sequence) => {
    const value = atob(base64);
    const bytes = new Uint8Array(value.length);
    for (let i=0; i<value.length; i++) bytes[i] = value.charCodeAt(i);
    terminal.write(bytes, () => RemoteSsh.ack(sequence));
  };
  window.sshEnabled = (enabled, sessionGeneration) => {
    generation = sessionGeneration;
    ctrl = false;
    RemoteSsh.ctrl(false);
    terminal.options.disableStdin = !enabled;
    if (enabled) terminal.focus();
  };
  window.sshKey = key => {
    if (key === 'keyboard') { terminal.focus(); return; }
    if (terminal.options.disableStdin) return;
    if (key === 'ctrl') { ctrl = !ctrl; RemoteSsh.ctrl(ctrl); terminal.focus(); return; }
    const cursor = terminal.modes.applicationCursorKeysMode ? '\x1bO' : '\x1b[';
    const keys = {tab:'\t', esc:'\x1b', up:cursor+'A', down:cursor+'B', right:cursor+'C', left:cursor+'D'};
    if (keys[key]) RemoteSsh.input(keys[key], generation);
    terminal.focus();
  };
  window.sshCopy = () => {
    const selected = terminal.getSelection() || window.getSelection().toString();
    if (selected) { RemoteSsh.copy(selected); return; }
    // Android WebView native selection handles vary. Offer a native selectable
    // copy dialog for the visible terminal rows without altering the live PTY.
    const buffer = terminal.buffer.active;
    const lines = [];
    for (let y=buffer.viewportY; y<Math.min(buffer.length,buffer.viewportY+terminal.rows); y++)
      lines.push(buffer.getLine(y).translateToString(true));
    RemoteSsh.chooseCopy(lines.join('\n'));
  };
  window.sshPaste = base64 => {
    if (terminal.options.disableStdin) return;
    const raw = atob(base64);
    const bytes = new Uint8Array(raw.length);
    for (let i=0; i<raw.length; i++) bytes[i] = raw.charCodeAt(i);
    terminal.paste(new TextDecoder('utf-8').decode(bytes));
  };
  resize();
  RemoteSsh.ready();
})();
