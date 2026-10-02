import type { Command, HostMessage } from './types';
import { createMockHost } from './mock';

type Listener = (message: HostMessage) => void;

interface WebViewBridge {
  postMessage(message: unknown): void;
  addEventListener(type: 'message', listener: (event: { data: unknown }) => void): void;
}

declare global {
  interface Window {
    chrome?: { webview?: WebViewBridge };
  }
}

export interface Host {
  send(command: Command): void;
  subscribe(listener: Listener): () => void;
  /** 在守护器里运行；浏览器预览时为 false，使用模拟宿主 */
  embedded: boolean;
}

function createWebViewHost(webview: WebViewBridge): Host {
  const listeners = new Set<Listener>();
  webview.addEventListener('message', (event) => {
    const message = event.data as HostMessage;
    listeners.forEach((listener) => listener(message));
  });
  return {
    embedded: true,
    send: (command) => webview.postMessage(command),
    subscribe(listener) {
      listeners.add(listener);
      return () => listeners.delete(listener);
    },
  };
}

// ?mock 强制使用模拟宿主，方便在守护器的截图模式里检查各个场景。
const forceMock = new URLSearchParams(location.search).has('mock');

function createPreviewHost(): Host {
  const mock = createMockHost();
  const webview = window.chrome?.webview;
  return {
    ...mock,
    // 守护器截图模式里用模拟数据时，仍要告诉宿主页面已就绪，宿主才会开始截图。
    send(command) {
      if (command.name === 'ready') webview?.postMessage({ name: 'ready' });
      mock.send(command);
    },
  };
}

export const host: Host = window.chrome?.webview && !forceMock
  ? createWebViewHost(window.chrome.webview)
  : createPreviewHost();
