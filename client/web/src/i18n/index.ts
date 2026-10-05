import { createI18n } from 'vue-i18n'
import { messages, type MessageSchema } from './messages'
import type { UiLanguage } from '../types'

export const i18n = createI18n<[MessageSchema], UiLanguage, false>({
  legacy: false,
  locale: 'zh-CN',
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
