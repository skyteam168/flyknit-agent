/** 图表配色从 CSS 变量里取，深浅色主题自动跟上 */
export function palette() {
  const css = getComputedStyle(document.documentElement)
  const v = (name: string) => css.getPropertyValue(name).trim()
  return {
    ink: v('--ink'),
    soft: v('--ink-soft'),
    faint: v('--ink-faint'),
    line: v('--line'),
    cloth: v('--cloth'),
    series: [v('--indigo'), v('--thread'), v('--amber'), v('--violet'), v('--red'), '#0284c7', '#65a30d', '#db2777'],
  }
}

export function baseAxis() {
  const p = palette()
  return {
    axisLine: { lineStyle: { color: p.line } },
    axisTick: { show: false },
    axisLabel: { color: p.faint, fontSize: 11 },
    splitLine: { lineStyle: { color: p.line, type: 'dashed' as const } },
  }
}

export function tooltip() {
  const p = palette()
  return {
    backgroundColor: p.cloth,
    borderColor: p.line,
    textStyle: { color: p.ink, fontSize: 12 },
    extraCssText: 'border-radius:10px;box-shadow:0 8px 24px -8px rgba(0,0,0,.25);',
  }
}

/** 渐变填充：顶部 alpha → 底部透明 */
export function areaFill(color: string, alpha = 0.28) {
  return {
    type: 'linear' as const,
    x: 0,
    y: 0,
    x2: 0,
    y2: 1,
    colorStops: [
      { offset: 0, color: withAlpha(color, alpha) },
      { offset: 1, color: withAlpha(color, 0.02) },
    ],
  }
}

function withAlpha(color: string, alpha: number) {
  const m = /^#([0-9a-f]{6})$/i.exec(color)
  if (!m) return color
  const n = parseInt(m[1], 16)
  return `rgba(${(n >> 16) & 255},${(n >> 8) & 255},${n & 255},${alpha})`
}
