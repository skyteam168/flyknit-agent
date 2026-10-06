import { nextTick, onBeforeUnmount, watch, type Ref } from 'vue'
import { decodeBlockCode, renderBlock, type BlockTheme } from './blocks'
import { state } from '../store'

/**
 * 把 markdown 里的 .rich-block 占位符真正画出来。
 *
 * v-html 插进来的内容 Vue 管不到，所以挂载后扫一遍容器，
 * 找到占位符交给对应的渲染器。渲染失败就退回成普通代码块，
 * 模型写错一张图不该让整条回答显示不出来。
 */
export function useRichBlocks(container: Ref<HTMLElement | null | undefined>, deps: () => unknown) {
  const disposers: (() => void)[] = []
  // v-html 每次重渲染都会换掉整棵 DOM。渲染库是异步加载的（mermaid 的包不小），
  // 等待期间内容可能已经重渲染过，这时老的那一轮必须作废，否则会把图画进已经脱离文档的节点里。
  let generation = 0

  function currentTheme(): BlockTheme {
    const theme = state.app?.theme ?? 'system'
    if (theme === 'light') return 'light'
    if (theme === 'dark') return 'dark'
    return window.matchMedia?.('(prefers-color-scheme: dark)').matches ? 'dark' : 'light'
  }

  function cleanup() {
    for (const d of disposers.splice(0)) {
      try {
        d()
      } catch {
        // 清理失败不影响后续渲染
      }
    }
  }

  async function paint() {
    const mine = ++generation
    const root = container.value
    if (!root) return
    const theme = currentTheme()
    const blocks = root.querySelectorAll<HTMLElement>('pre.rich-block[data-code]')
    // 上一轮被打断的块，data-done 还留着但图没画出来。这里清掉，让它们这一轮重新画。
    for (const el of blocks) {
      if (!el.classList.contains('rendered') && !el.classList.contains('failed')) {
        delete el.dataset.done
      }
    }
    for (const el of blocks) {
      if (mine !== generation) return // 已经有新的一轮了，这一轮的结果没人要
      const lang = el.dataset.lang ?? ''
      const code = decodeBlockCode(el.dataset.code ?? '')
      if (el.dataset.done === '1') continue
      el.dataset.done = '1'
      el.classList.add('rendering')
      try {
        const ok = await renderBlock(lang, code, el, theme)
        if (!ok) throw new Error(`没有 ${lang} 的渲染器`)
        if (mine !== generation || !el.isConnected) return
        el.classList.remove('rendering')
        el.classList.add('rendered')
        const dispose = (el as HTMLElement & { __dispose?: () => void }).__dispose
        if (dispose) disposers.push(dispose)
      } catch (e) {
        if (mine !== generation || !el.isConnected) return
        // 画不出来就老老实实显示源码，并说明原因
        el.classList.remove('rendering')
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
  }

  // 流式输出时内容每秒变几十次，没必要每次都重画；停下来 120ms 再画
  let timer: ReturnType<typeof setTimeout> | null = null
  watch(
    deps,
    () => {
      if (timer) clearTimeout(timer)
      timer = setTimeout(() => {
        cleanup()
        void nextTick(paint)
      }, 120)
    },
    { immediate: true, flush: 'post' },
  )

  onBeforeUnmount(() => {
    if (timer) clearTimeout(timer)
    generation++
    cleanup()
  })

  return { repaint: paint }
}
