<script setup lang="ts">
/** 一个终端画面（xterm.js）：显示员工自己的 PowerShell，键盘输入原样交给宿主；跟着面板大小调行列数 */
import { onBeforeUnmount, onMounted, ref } from 'vue'
import { Terminal } from '@xterm/xterm'
import { FitAddon } from '@xterm/addon-fit'
import '@xterm/xterm/css/xterm.css'
import { bridge } from '../../bridge'
import { attachPty, detachPty } from './pty'

const props = defineProps<{ id: string }>()
const host = ref<HTMLElement>()
let term: Terminal | null = null
let fit: FitAddon | null = null
let resize: ResizeObserver | null = null
let observer: MutationObserver | null = null
let timer = 0

const css = (name: string) => getComputedStyle(document.documentElement).getPropertyValue(name).trim()
const theme = () => ({
  background: css('--cloth') || '#ffffff',
  foreground: css('--ink') || '#18181b',
  cursor: css('--indigo') || '#3446c9',
  selectionBackground: 'rgba(52, 70, 201, 0.30)',
})

function refit() {
  if (!term || !fit || !host.value?.clientWidth) return
  fit.fit()
  // 拖动面板时别每一像素都告诉宿主
  clearTimeout(timer)
  timer = window.setTimeout(() => term && void bridge.resizePty(props.id, term.cols, term.rows).catch(() => {}), 120)
}

onMounted(() => {
  term = new Terminal({
    fontFamily: "'Cascadia Mono', 'Cascadia Code', Consolas, 'Courier New', monospace",
    fontSize: 13,
    cursorBlink: true,
    scrollback: 5000,
    allowProposedApi: false,
    theme: theme(),
  })
  fit = new FitAddon()
  term.loadAddon(fit)
  term.open(host.value!)
  refit()
  attachPty(props.id, (data) => term?.write(data))
  term.onData((data) => void bridge.writePty(props.id, data).catch(() => {}))
  resize = new ResizeObserver(refit)
  resize.observe(host.value!)
  observer = new MutationObserver(() => term && (term.options.theme = theme()))
  observer.observe(document.documentElement, { attributes: true, attributeFilter: ['data-theme'] })
  term.focus()
})

onBeforeUnmount(() => {
  clearTimeout(timer)
  detachPty(props.id)
  resize?.disconnect()
  observer?.disconnect()
  term?.dispose()
})
</script>

<template>
  <div ref="host" class="xterm-host" />
</template>

<style scoped>
.xterm-host {
  flex: 1;
  min-width: 0;
  min-height: 0;
  padding: 4px 0 0 8px;
  background: var(--cloth);
}
</style>
