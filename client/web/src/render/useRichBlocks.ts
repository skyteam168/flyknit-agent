import { nextTick, onBeforeUnmount, watch, type Ref } from 'vue'
import { decodeBlockCode, renderBlock, type BlockTheme } from './blocks'
import { state } from '../store'
import { i18n } from '../i18n'

/**
 * 界面刚因为卡死或崩溃被宿主自动重新加载过（地址带 recovered=1）：这次打开期间图表不自动画，
 * 点一下才画——多半就是某张图把界面拖垮的，自动再画一遍会再卡一次。
 */
export const safeMode = typeof location !== 'undefined' && new URLSearchParams(location.search).has('recovered')

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
      (el) =>
        !el.classList.contains('rendered') &&
        !el.classList.contains('failed') &&
        !el.classList.contains('rendering') &&
        !el.classList.contains('held'),
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

  /** 安全模式下先放一个按钮，点了才画这一张 */
  function hold(el: HTMLElement) {
    el.classList.add('held')
    el.textContent = ''
    const btn = document.createElement('button')
    btn.type = 'button'
    btn.className = 'rich-hold'
    btn.textContent = i18n.global.t('ui.render.held')
    btn.addEventListener('click', () => {
      el.dataset.allow = '1'
      el.classList.remove('held')
      el.textContent = ''
      void paint()
    })
    el.append(btn)
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
      if (safeMode && !el.dataset.allow) {
        hold(el)
        continue
      }
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
        // 画不出来：一句话说明，原文收在「查看原文」里，不把一大段源码和报错直接摊给员工
        el.classList.add('failed')
        el.textContent = ''
        const note = document.createElement('div')
        note.className = 'rich-error'
        // 原因只留第一行：mermaid 的报错后面跟着好几行语法提示，员工看不懂
        const reason = String((e as Error)?.message ?? e).split('\n')[0].slice(0, 80)
        note.textContent = i18n.global.t('ui.render.failed', { reason })
        const details = document.createElement('details')
        const summary = document.createElement('summary')
        summary.textContent = i18n.global.t('ui.render.source')
        const pre = document.createElement('code')
        pre.textContent = code
        details.append(summary, pre)
        el.append(note, details)
        console.warn('[rich-block] 渲染失败', lang, e)
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
