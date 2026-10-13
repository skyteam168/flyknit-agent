import { nextTick, onBeforeUnmount, watch, type Ref } from 'vue'
import { decodeBlockCode, renderBlock, type BlockTheme } from './blocks'
import { state } from '../store'

type Painted = HTMLElement & { __dispose?: () => void }

/**
 * 把 markdown 里的 .rich-block 占位符真正画出来。
 *
 * v-html 插进来的内容 Vue 管不到，所以挂载后扫一遍容器，
 * 找到占位符交给对应的渲染器。渲染失败就退回成普通代码块，
 * 模型写错一张图不该让整条回答显示不出来。
 *
 * 流式输出时 v-html 每来一个字就换掉整棵 DOM，画好的图跟着被扔掉。为了不闪：
 * 画好的节点留着，新 DOM 里出现同一张图（同语言、同代码、同主题）的占位符时，直接把老节点换回去——
 * 图表实例还活着，不用重画。只有真正从页面上消失、也没被换回去的图才释放。
 */
export function useRichBlocks(container: Ref<HTMLElement | null | undefined>, deps: () => unknown) {
  /** 画过的节点 → 它是哪张图 */
  const painted = new Map<Painted, string>()
  // 渲染库是异步加载的（mermaid 的包不小），等待期间内容可能已经重渲染过，老的那一轮必须作废
  let generation = 0

  function currentTheme(): BlockTheme {
    const theme = state.app?.theme ?? 'system'
    if (theme === 'light') return 'light'
    if (theme === 'dark') return 'dark'
    return window.matchMedia?.('(prefers-color-scheme: dark)').matches ? 'dark' : 'light'
  }

  function dispose(el: Painted) {
    painted.delete(el)
    try {
      el.__dispose?.()
    } catch {
      // 清理失败不影响后续渲染
    }
  }

  function placeholders(root: HTMLElement) {
    return [...root.querySelectorAll<HTMLElement>('pre.rich-block[data-code]')].filter(
      (el) => !el.classList.contains('rendered') && !el.classList.contains('failed') && !el.classList.contains('rendering'),
    )
  }

  /** 新 DOM 里的占位符能用老节点的，当场换回去（同步，不闪） */
  function reuse(root: HTMLElement, theme: BlockTheme) {
    for (const el of placeholders(root)) {
      const key = `${theme}\u0000${el.dataset.lang ?? ''}\u0000${el.dataset.code ?? ''}`
      for (const [old, oldKey] of painted) {
        if (oldKey === key && !old.isConnected) {
          el.replaceWith(old)
          break
        }
      }
    }
  }

  /** 已经不在页面上、也没被换回去的图：释放 */
  function sweep() {
    for (const el of [...painted.keys()]) {
      if (!el.isConnected) dispose(el)
    }
  }

  async function paint() {
    const mine = ++generation
    const root = container.value
    if (!root) return
    const theme = currentTheme()
    reuse(root, theme)
    sweep()
    for (const el of placeholders(root)) {
      if (mine !== generation) return // 已经有新的一轮了，这一轮的结果没人要
      if (!el.isConnected) continue
      const lang = el.dataset.lang ?? ''
      const encoded = el.dataset.code ?? ''
      const code = decodeBlockCode(encoded)
      el.classList.add('rendering')
      try {
        const ok = await renderBlock(lang, code, el, theme)
        if (!ok) throw new Error(`没有 ${lang} 的渲染器`)
        el.classList.remove('rendering')
        el.classList.add('rendered')
        painted.set(el, `${theme}\u0000${lang}\u0000${encoded}`)
        // 画的过程中内容又换了一轮：节点留在 painted 里，下一轮能直接换回去
        if (mine !== generation) return
      } catch (e) {
        el.classList.remove('rendering')
        if (mine !== generation || !el.isConnected) {
          ;(el as Painted).__dispose?.()
          return
        }
        // 画不出来就老老实实显示源码，并说明原因
        el.classList.add('failed')
        el.textContent = ''
        const note = document.createElement('div')
        note.className = 'rich-error'
        note.textContent = (e as Error).message
        const pre = document.createElement('code')
        pre.textContent = code
        el.append(note, pre)
      }
    }
    sweep()
  }

  // 内容一变就先把画好的图换回去（不闪）；新图等内容停下来 120ms 再画，流式输出时不用每个字都画一遍
  let timer: ReturnType<typeof setTimeout> | null = null
  watch(
    deps,
    () => {
      void nextTick(() => {
        if (container.value) reuse(container.value, currentTheme())
      })
      if (timer) clearTimeout(timer)
      timer = setTimeout(() => void nextTick(paint), 120)
    },
    { immediate: true, flush: 'post' },
  )

  onBeforeUnmount(() => {
    if (timer) clearTimeout(timer)
    generation++
    for (const el of [...painted.keys()]) dispose(el)
  })

  return { repaint: paint }
}
