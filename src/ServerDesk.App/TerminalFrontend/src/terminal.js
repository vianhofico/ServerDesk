import { Terminal } from '@xterm/xterm';
import { FitAddon } from '@xterm/addon-fit';
import { SearchAddon } from '@xterm/addon-search';
import '@xterm/xterm/css/xterm.css';

const bridge = window.chrome?.webview;
if (!bridge) {
  throw new Error('ServerDesk WebView2 bridge is unavailable.');
}

const container = document.getElementById('terminal');
const searchBar = document.getElementById('search');
const searchInput = document.getElementById('searchInput');
const previousButton = document.getElementById('searchPrevious');
const nextButton = document.getElementById('searchNext');
const closeButton = document.getElementById('searchClose');

const terminal = new Terminal({
  cursorBlink: true,
  cursorStyle: 'block',
  scrollback: 10000,
  convertEol: false,
  allowTransparency: false,
  fontFamily: 'Cascadia Mono, Cascadia Code, Consolas, monospace',
  fontSize: 14,
  lineHeight: 1.1,
  theme: {
    background: '#0f1419',
    foreground: '#e6edf3',
    cursor: '#70b7ff',
    selectionBackground: '#284b70',
  },
});

const fitAddon = new FitAddon();
const searchAddon = new SearchAddon();
terminal.loadAddon(fitAddon);
terminal.loadAddon(searchAddon);
terminal.open(container);
fitAddon.fit();
terminal.focus();

function post(message) {
  bridge.postMessage(message);
}

function postResize() {
  post({ type: 'resize', columns: terminal.cols, rows: terminal.rows });
}

function showSearch() {
  searchBar.classList.add('open');
  searchInput.focus();
  searchInput.select();
  requestAnimationFrame(() => {
    fitAddon.fit();
    postResize();
  });
}

function hideSearch() {
  searchBar.classList.remove('open');
  terminal.focus();
  requestAnimationFrame(() => {
    fitAddon.fit();
    postResize();
  });
}

function findNext() {
  if (searchInput.value) {
    searchAddon.findNext(searchInput.value, { caseSensitive: false, incremental: true });
  }
}

function findPrevious() {
  if (searchInput.value) {
    searchAddon.findPrevious(searchInput.value, { caseSensitive: false });
  }
}

terminal.onData(data => post({ type: 'input', data }));
terminal.onResize(size => post({ type: 'resize', columns: size.cols, rows: size.rows }));

terminal.attachCustomKeyEventHandler(event => {
  if (event.type !== 'keydown' || !event.ctrlKey || !event.shiftKey) {
    return true;
  }

  const key = event.key.toLowerCase();
  if (key === 'f') {
    event.preventDefault();
    showSearch();
    return false;
  }

  if (key === 'c' && terminal.hasSelection()) {
    event.preventDefault();
    navigator.clipboard.writeText(terminal.getSelection()).catch(() => {});
    return false;
  }

  if (key === 'v') {
    event.preventDefault();
    navigator.clipboard.readText()
      .then(text => {
        if (text) {
          terminal.paste(text);
        }
      })
      .catch(() => {});
    return false;
  }

  return true;
});

searchInput.addEventListener('input', findNext);
searchInput.addEventListener('keydown', event => {
  if (event.key === 'Enter') {
    event.preventDefault();
    event.shiftKey ? findPrevious() : findNext();
  } else if (event.key === 'Escape') {
    event.preventDefault();
    hideSearch();
  }
});
previousButton.addEventListener('click', findPrevious);
nextButton.addEventListener('click', findNext);
closeButton.addEventListener('click', hideSearch);

const resizeObserver = new ResizeObserver(() => {
  try {
    fitAddon.fit();
  } catch {
    // Ignore transient zero-size layouts while WPF tabs are changing.
  }
});
resizeObserver.observe(container);

bridge.addEventListener('message', event => {
  const message = event.data;
  if (!message || typeof message.type !== 'string') {
    return;
  }

  switch (message.type) {
    case 'output':
      terminal.write(message.data ?? '');
      break;
    case 'focus':
      terminal.focus();
      break;
    case 'clear':
      terminal.clear();
      break;
    case 'state':
      document.title = message.state ? `ServerDesk Terminal — ${message.state}` : 'ServerDesk Terminal';
      break;
    default:
      break;
  }
});

postResize();
post({ type: 'ready', columns: terminal.cols, rows: terminal.rows });
