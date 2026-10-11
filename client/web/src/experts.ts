import { i18n } from './i18n'

/** 使用量：1.6万 / 16K，按界面语言缩写 */
export function compactNumber(n: number): string {
  try {
    return new Intl.NumberFormat(i18n.global.locale.value, { notation: 'compact', maximumFractionDigits: 1 }).format(n)
  } catch {
    return String(n)
  }
}

/** 从名字算一个稳定的色相：同一个分类、同一位专家每次颜色都一样 */
export function hueOf(text: string): number {
  let h = 0
  for (const ch of text) h = (h * 31 + ch.charCodeAt(0)) % 360
  return h
}
