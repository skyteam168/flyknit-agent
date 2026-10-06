import { ref, watch } from 'vue'

export type ThemeMode = 'light' | 'dark' | 'system'

const KEY = 'flyknit-admin-theme'
const media = window.matchMedia('(prefers-color-scheme: dark)')

export const themeMode = ref<ThemeMode>((localStorage.getItem(KEY) as ThemeMode) || 'system')
/** 实际生效的是不是深色。图表配色跟着它重绘 */
export const isDark = ref(false)

function apply() {
  isDark.value = themeMode.value === 'dark' || (themeMode.value === 'system' && media.matches)
  document.documentElement.classList.toggle('dark', isDark.value)
}

watch(themeMode, (v) => {
  localStorage.setItem(KEY, v)
  apply()
})
media.addEventListener('change', apply)
apply()
