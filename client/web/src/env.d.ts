/// <reference types="vite/client" />
declare module '*.vue' {
  import type { DefineComponent } from 'vue'
  const component: DefineComponent<object, object, unknown>
  export default component
}

interface WebView2Bridge {
  postMessage(message: unknown): void
  postMessageWithAdditionalObjects(message: unknown, additionalObjects: ArrayLike<unknown>): void
  addEventListener(type: 'message', listener: (e: { data: unknown }) => void): void
}

interface Window {
  chrome?: { webview?: WebView2Bridge }
}
