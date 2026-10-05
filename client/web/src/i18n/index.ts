import { createI18n } from 'vue-i18n'
import { messages, type MessageSchema } from './messages'
import type { UiLanguage } from '../types'

/** 宿主在页面地址里带上界面语言（?lang=vi-VN），启动画面在宿主响应之前就能显示正确的语言 */
function initialLanguage(): UiLanguage {
  const lang = new URLSearchParams(window.location.search).get('lang')
  return lang === 'vi-VN' || lang === 'en-US' || lang === 'zh-CN' ? lang : 'zh-CN'
}

export const i18n = createI18n<[MessageSchema], UiLanguage, false>({
  legacy: false,
  locale: initialLanguage(),
  fallbackLocale: 'zh-CN',
  messages,
})

export const uiLanguages: { code: UiLanguage; label: string }[] = [
  { code: 'zh-CN', label: '简体中文' },
  { code: 'vi-VN', label: 'Tiếng Việt' },
  { code: 'en-US', label: 'English' },
]

export function applyLanguage(lang: UiLanguage) {
  i18n.global.locale.value = lang
  document.documentElement.lang = lang
}
