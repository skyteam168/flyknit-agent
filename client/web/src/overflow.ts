import { nextTick, onBeforeUnmount, onMounted, reactive, watch, type Ref, type WatchSource } from 'vue'

/**
 * 工具栏放不下时把次要按钮收进「…」菜单，而不是换行（换行后输入框底部很难看）。
 *
 * 用法：工具栏里可收起的元素带 data-ov="key"；keys() 按「最后才收」到「最先收」的顺序给出这一刻可能出现的 key。
 * 收起的元素仍然留在 DOM 里（加 ov-hide 类、不占位），这样技能 / 连接器的弹出面板还能从「…」菜单里打开。
 *
 * 宽度：可收起的元素每次可见时记下宽度，收起后用记下的值判断放不放得回去；
 * 其他子元素（模式、模型、发送按钮…）按实际宽度算，弹性占位的 .spacer 不算。
 */
export function useOverflow(bar: Ref<HTMLElement | undefined>, keys: () => string[], deps: WatchSource[] = [], moreWidth = 34) {
  const hidden = reactive(new Set<string>())
  const widths = new Map<string, number>()
  // 能缩的元素（data-ov-shrink="名字"）见过的最大宽度：缩了以后仍按原本要多宽来算，避免来回跳
  const natural = new Map<string, number>()
  let frame = 0
  let ro: ResizeObserver | null = null

  function compute() {
    frame = 0
    const el = bar.value
    if (!el) return
    const style = getComputedStyle(el)
    const gap = parseFloat(style.columnGap) || 0
    // 留 2px 余量：offsetWidth 取整、字体渲染差一点就会挤到能缩的元素
    const available = el.clientWidth - (parseFloat(style.paddingLeft) || 0) - (parseFloat(style.paddingRight) || 0) - 2

    let fixed = 0
    let fixedCount = 0
    let more = moreWidth
    for (const child of Array.from(el.children) as HTMLElement[]) {
      ro?.observe(child)
      const key = child.dataset.ov
      if (key) {
        if (!child.classList.contains('ov-hide') && child.offsetWidth > 0) widths.set(key, child.offsetWidth)
        continue
      }
      if (child.classList.contains('ov-more')) {
        if (child.offsetWidth > 0) more = child.offsetWidth
        continue
      }
      if (child.classList.contains('spacer')) {
        // 弹性占位自己不占宽度，但两边各有一个间距
        fixedCount++
        continue
      }
      if (child.offsetWidth === 0 && getComputedStyle(child).display === 'none') continue
      // 能缩的（模型名，CSS 里允许省略号）按完整宽度算：别的都收完了还放不下才轮到它缩，算法不会因为它缩了就来回跳
      let width = child.offsetWidth
      const shrinkKey = child.dataset.ovShrink
      if (shrinkKey !== undefined) {
        child.querySelectorAll<HTMLElement>('.label').forEach((l) => (width += Math.max(0, l.scrollWidth - l.clientWidth)))
        width = Math.max(width, natural.get(shrinkKey) ?? 0)
        natural.set(shrinkKey, width)
      }
      fixed += width
      fixedCount++
    }

    const list = keys()
    let used = fixed + Math.max(0, fixedCount - 1) * gap
    const next = new Set<string>()
    let full = false
    list.forEach((key, i) => {
      const w = widths.get(key)
      if (full || w === undefined) {
        // 没量过的（刚出现、还没显示过）先放出来，下一帧量到宽度再算
        if (full) next.add(key)
        return
      }
      const rest = i < list.length - 1
      // 后面还有没放的就要给「…」留位置
      if (used + gap + w + (rest ? gap + more : 0) <= available + 0.5) {
        used += gap + w
      } else {
        full = true
        next.add(key)
      }
    })
    // 收起了一部分时，「…」按钮本身也要占位：放不下就再收一个
    if (next.size > 0) {
      while (used + gap + more > available + 0.5) {
        const kept = list.filter((k) => !next.has(k) && widths.has(k))
        const last = kept[kept.length - 1]
        if (!last) break
        next.add(last)
        used -= gap + (widths.get(last) ?? 0)
      }
    }
    if (next.size !== hidden.size || [...next].some((k) => !hidden.has(k))) {
      hidden.clear()
      next.forEach((k) => hidden.add(k))
    }
  }

  function schedule() {
    if (!frame) frame = requestAnimationFrame(compute)
  }

  onMounted(() => {
    if (typeof ResizeObserver !== 'undefined') ro = new ResizeObserver(schedule)
    if (bar.value) ro?.observe(bar.value)
    schedule()
  })
  onBeforeUnmount(() => {
    ro?.disconnect()
    if (frame) cancelAnimationFrame(frame)
  })
  // 模式切换、语言切换、按钮增减：等 DOM 更新完再算；语言变了宽度都要重新量
  watch([keys, ...deps], () => {
    natural.clear()
    void nextTick(schedule)
  })

  /** 文案变了（换语言）时调用：清掉记下的宽度，全部放出来重新量 */
  function remeasure() {
    widths.clear()
    natural.clear()
    hidden.clear()
    void nextTick(schedule)
  }

  return { hidden, remeasure, isHidden: (key: string) => hidden.has(key) }
}
