/**
 * 代码块渲染注册表。
 *
 * 模型在回答里写一个带语言标记的代码块，这里按语言找到对应的渲染器把它画出来；
 * 没有注册的语言就还按普通代码块显示。加一种图表或图形 = 注册一个渲染器，
 * markdown、分屏预览两边都会自动支持，不用各改一遍。
 *
 * 渲染库都是打进包里的（mermaid / viz / echarts），工厂电脑断网也能用。
 * 每个库都按需动态加载，没用到的用户不会为它付出启动时间。
 */

/**
 * 代码放进 data- 属性时要先 base64。
 * DOMPurify 为了防 mXSS，会把含有 `-->` 的属性值整个丢掉——
 * 而这正是 mermaid 箭头的写法（A --> B），直接放原文会让图神秘消失。
 */
export function encodeBlockCode(code: string): string {
  return btoa(String.fromCharCode(...new TextEncoder().encode(code)))
}

export function decodeBlockCode(encoded: string): string {
  const bytes = Uint8Array.from(atob(encoded), (c) => c.charCodeAt(0))
  return new TextDecoder().decode(bytes)
}

export type BlockTheme = 'light' | 'dark'

export interface BlockRenderer {
  /** 认领哪些语言标记（小写） */
  languages: string[]
  /** 把 code 画进 el。抛异常由调用方兜底显示成普通代码块。 */
  render(code: string, el: HTMLElement, theme: BlockTheme): Promise<void>
}

const renderers = new Map<string, BlockRenderer>()

export function registerBlockRenderer(renderer: BlockRenderer) {
  for (const lang of renderer.languages) renderers.set(lang.toLowerCase(), renderer)
}

export function hasBlockRenderer(lang: string): boolean {
  return renderers.has(lang.toLowerCase())
}

/** 已注册的语言，用来告诉模型它能画什么 */
export function registeredLanguages(): string[] {
  return [...renderers.keys()]
}

export async function renderBlock(lang: string, code: string, el: HTMLElement, theme: BlockTheme): Promise<boolean> {
  const renderer = renderers.get(lang.toLowerCase())
  if (!renderer) return false
  await renderer.render(code, el, theme)
  return true
}

// ---------------- Mermaid 流程图 ----------------

let mermaidReady: Promise<typeof import('mermaid').default> | null = null
let mermaidTheme: BlockTheme | null = null

async function loadMermaid(theme: BlockTheme) {
  if (!mermaidReady || mermaidTheme !== theme) {
    mermaidTheme = theme
    mermaidReady = import('mermaid').then((m) => {
      m.default.initialize({
        startOnLoad: false,
        theme: theme === 'dark' ? 'dark' : 'default',
        securityLevel: 'strict', // 不执行图里的脚本，也不允许点击回调
        fontFamily: 'inherit',
      })
      return m.default
    })
  }
  return mermaidReady
}

let mermaidSeq = 0

registerBlockRenderer({
  languages: ['mermaid', 'mmd'],
  async render(code, el, theme) {
    const mermaid = await loadMermaid(theme)
    const { svg } = await mermaid.render(`mmd-${++mermaidSeq}`, code)
    el.innerHTML = svg
  },
})

// ---------------- Graphviz (DOT) ----------------

let vizReady: Promise<{ renderSVGElement(src: string): SVGSVGElement }> | null = null

registerBlockRenderer({
  languages: ['dot', 'graphviz', 'gv'],
  async render(code, el) {
    vizReady ??= import('@viz-js/viz').then((m) => m.instance())
    const viz = await vizReady
    el.replaceChildren(viz.renderSVGElement(code))
  },
})

// ---------------- 图表 ----------------

export type ChartType = 'bar' | 'hbar' | 'line' | 'area' | 'pie' | 'donut' | 'scatter'

export interface ChartSpec {
  type: ChartType
  title?: string
  /** 横轴分类（饼图 / 环形图用作扇区名） */
  categories?: string[]
  series: { name?: string; data: number[]; stack?: string }[]
  xLabel?: string
  yLabel?: string
  /** 是否堆叠；也可以在单个 series 上用 stack 分组 */
  stacked?: boolean
}

export class ChartSpecError extends Error {}

/** 校验模型写出来的图表描述。宁可报错也不画出一张误导人的图。 */
export function parseChartSpec(code: string): ChartSpec {
  let raw: unknown
  try {
    raw = JSON.parse(code)
  } catch (e) {
    throw new ChartSpecError(`图表描述不是合法的 JSON：${(e as Error).message}`)
  }
  if (typeof raw !== 'object' || raw === null) throw new ChartSpecError('图表描述应该是一个对象')
  const spec = raw as Record<string, unknown>

  const types: ChartType[] = ['bar', 'hbar', 'line', 'area', 'pie', 'donut', 'scatter']
  const type = String(spec.type ?? 'bar').toLowerCase() as ChartType
  if (!types.includes(type)) throw new ChartSpecError(`不支持的图表类型 ${spec.type}，可用：${types.join(' / ')}`)

  const rawSeries = Array.isArray(spec.series) ? spec.series : []
  const series = rawSeries.map((s, i) => {
    const o = (s ?? {}) as Record<string, unknown>
    const data = Array.isArray(o.data) ? o.data.map(Number) : []
    if (data.length === 0) throw new ChartSpecError(`第 ${i + 1} 组数据是空的`)
    if (data.some((n) => !Number.isFinite(n))) throw new ChartSpecError(`第 ${i + 1} 组数据里有不是数字的值`)
    return { name: o.name ? String(o.name) : undefined, data, stack: o.stack ? String(o.stack) : undefined }
  })
  if (series.length === 0) throw new ChartSpecError('没有数据（series 是空的）')

  const categories = Array.isArray(spec.categories) ? spec.categories.map(String) : []
  // 分类数和数据点数对不上，图就是错的，直接说出来
  for (const [i, s] of series.entries()) {
    if (categories.length > 0 && s.data.length !== categories.length) {
      throw new ChartSpecError(`第 ${i + 1} 组有 ${s.data.length} 个值，但 categories 有 ${categories.length} 个`)
    }
  }
  if ((type === 'pie' || type === 'donut') && categories.length === 0) {
    throw new ChartSpecError('饼图和环形图需要 categories 来给每个扇区命名')
  }

  return {
    type,
    title: spec.title ? String(spec.title) : undefined,
    categories,
    series,
    xLabel: spec.xLabel ? String(spec.xLabel) : undefined,
    yLabel: spec.yLabel ? String(spec.yLabel) : undefined,
    stacked: spec.stacked === true,
  }
}

/** 色板：跟界面主色同一个家族，深浅模式下都够对比。 */
const Palette = ['#2f6df6', '#13b5b1', '#f2a33c', '#8b5cf6', '#e5556f', '#3fa34d', '#6b7280', '#d97706']

export function toEChartsOption(spec: ChartSpec, theme: BlockTheme): Record<string, unknown> {
  const ink = theme === 'dark' ? '#d7dbe3' : '#2b3040'
  const soft = theme === 'dark' ? '#8b93a4' : '#6b7280'
  const line = theme === 'dark' ? '#333a49' : '#e3e7ef'

  const base: Record<string, unknown> = {
    color: Palette,
    backgroundColor: 'transparent',
    textStyle: { color: ink, fontFamily: 'inherit' },
    title: spec.title ? { text: spec.title, left: 'center', textStyle: { color: ink, fontSize: 14, fontWeight: 600 } } : undefined,
    tooltip: { trigger: spec.type === 'pie' || spec.type === 'donut' ? 'item' : 'axis' },
    legend:
      spec.series.length > 1 || spec.type === 'pie' || spec.type === 'donut'
        ? { bottom: 0, textStyle: { color: soft } }
        : undefined,
    animationDuration: 420,
  }

  if (spec.type === 'pie' || spec.type === 'donut') {
    const data = spec.categories!.map((name, i) => ({ name, value: spec.series[0].data[i] }))
    return {
      ...base,
      series: [
        {
          type: 'pie',
          radius: spec.type === 'donut' ? ['46%', '70%'] : '66%',
          center: ['50%', spec.title ? '54%' : '48%'],
          data,
          label: { color: ink },
          labelLine: { lineStyle: { color: line } },
        },
      ],
    }
  }

  const categoryAxis = {
    type: 'category',
    data: spec.categories,
    name: spec.xLabel,
    axisLabel: { color: soft },
    axisLine: { lineStyle: { color: line } },
  }
  const valueAxis = {
    type: 'value',
    name: spec.yLabel,
    axisLabel: { color: soft },
    splitLine: { lineStyle: { color: line } },
  }
  const horizontal = spec.type === 'hbar'

  return {
    ...base,
    grid: { left: 12, right: 18, top: spec.title ? 46 : 18, bottom: base.legend ? 36 : 12, containLabel: true },
    xAxis: horizontal ? valueAxis : categoryAxis,
    yAxis: horizontal ? categoryAxis : valueAxis,
    series: spec.series.map((s) => ({
      name: s.name,
      type: spec.type === 'line' || spec.type === 'area' ? 'line' : spec.type === 'scatter' ? 'scatter' : 'bar',
      data: s.data,
      stack: s.stack ?? (spec.stacked ? 'total' : undefined),
      smooth: spec.type === 'line' || spec.type === 'area',
      areaStyle: spec.type === 'area' ? {} : undefined,
      barMaxWidth: 46,
    })),
  }
}

registerBlockRenderer({
  languages: ['chart', 'flyknit-chart'],
  async render(code, el, theme) {
    const spec = parseChartSpec(code)
    const echarts = await import('echarts')
    el.style.height = `${spec.type === 'pie' || spec.type === 'donut' ? 320 : 300}px`
    const chart = echarts.init(el, undefined, { renderer: 'svg' })
    chart.setOption(toEChartsOption(spec, theme))
    // 分屏拖宽、窗口变化时跟着重算
    const observer = new ResizeObserver(() => chart.resize())
    observer.observe(el)
    ;(el as HTMLElement & { __dispose?: () => void }).__dispose = () => {
      observer.disconnect()
      chart.dispose()
    }
  },
})
