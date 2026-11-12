// WebView2 运行时太旧时（没有 ICoreWebView2Settings9），页面里的 app-region: drag 不起作用，窗口拖不动。
// 宿主（WebViewCompat.cs）这时会在页面加载前设 window.__flyknitDragFallback = true，
// 这里接管：在拖动区域按下鼠标就请宿主拖动窗口；双击照样最大化 / 还原。新运行时上什么都不做。

/** 往上找第一个明确写了 drag / no-drag 的元素（有的 Chromium 版本里 app-region 不继承） */
export function inDragRegion(start: Element | null): boolean {
  for (let el = start; el; el = el.parentElement) {
    const cs = getComputedStyle(el)
    const v = cs.getPropertyValue('-webkit-app-region') || cs.getPropertyValue('app-region')
    if (v === 'drag') return true
    if (v === 'no-drag') return false
  }
  return false
}

const DoubleClickMs = 450
const DoubleClickPx = 6

/** 系统的双击时间一般是 500 ms；两次按下离得很近才算双击 */
export function isDoubleClick(prev: { t: number; x: number; y: number } | null, now: { t: number; x: number; y: number }): boolean {
  return !!prev && now.t - prev.t <= DoubleClickMs && Math.abs(now.x - prev.x) <= DoubleClickPx && Math.abs(now.y - prev.y) <= DoubleClickPx
}

export function installDragFallback(startDrag: () => unknown, onDoubleClick?: () => unknown): boolean {
  if (!(window as unknown as { __flyknitDragFallback?: boolean }).__flyknitDragFallback) return false
  let last: { t: number; x: number; y: number } | null = null
  window.addEventListener('mousedown', (e) => {
    if (e.button !== 0 || !inDragRegion(e.target as Element | null)) return
    const now = { t: e.timeStamp, x: e.screenX, y: e.screenY }
    // 交给系统拖动后收不到 mouseup，浏览器自己的双击（dblclick）也就不会触发：这里自己数
    if (onDoubleClick && isDoubleClick(last, now)) {
      last = null
      e.preventDefault()
      void onDoubleClick()
      return
    }
    last = now
    e.preventDefault()
    void startDrag()
  }, true)
  return true
}
