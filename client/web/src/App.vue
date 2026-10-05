<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { FilePlus2 } from '@lucide/vue'
import Sidebar from './components/Sidebar.vue'
import TopBar from './components/TopBar.vue'
import Home from './components/Home.vue'
import SkillsDialog from './components/SkillsDialog.vue'
import MemoryDialog from './components/MemoryDialog.vue'
import ResizeEdges from './components/ResizeEdges.vue'
import UsageDialog from './components/UsageDialog.vue'
import SchedulesDialog from './components/SchedulesDialog.vue'
import MessageList from './components/MessageList.vue'
import Composer from './components/Composer.vue'
import PlanPanel from './components/PlanPanel.vue'
import SettingsDialog from './components/SettingsDialog.vue'
import { addFiles, current, currentState, init, newConversation, state } from './store'

const { t } = useI18n()
const composer = ref<InstanceType<typeof Composer>>()
const home = ref<InstanceType<typeof Home>>()
const sidebarHidden = ref(false)
const width = ref(window.innerWidth)
const drawer = ref(false)
const dragging = ref(0)
const ready = ref(false)
const initError = ref('')
const reload = () => window.location.reload()

/** 窄窗口（迷你模式）：侧栏改为抽屉 */
const narrow = computed(() => width.value < 760)
const showPlan = computed(() => width.value >= 1100 && (currentState.value?.plan.length ?? 0) > 0)
/** 有当前对话时显示对话视图，否则显示新任务首页 */
const inConversation = computed(() => current.value !== null)

function toggleSidebar() {
  if (narrow.value) drawer.value = !drawer.value
  else sidebarHidden.value = !sidebarHidden.value
}

function useSkill(name: string) {
  state.skillsOpen = false
  if (inConversation.value) composer.value?.useSkill(name)
  else home.value?.useSkill(name)
}

const onResize = () => (width.value = window.innerWidth)

function onKey(e: KeyboardEvent) {
  if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'n') {
    e.preventDefault()
    newConversation()
    window.dispatchEvent(new CustomEvent('flyknit:focus-input'))
  }
  if (e.key === 'Escape' && drawer.value) drawer.value = false
}

// 拖拽文件到窗口任意位置
function hasFiles(e: DragEvent) {
  return [...(e.dataTransfer?.types ?? [])].includes('Files')
}
function onDragEnter(e: DragEvent) {
  if (hasFiles(e)) dragging.value++
}
function onDragLeave(e: DragEvent) {
  if (hasFiles(e)) dragging.value = Math.max(0, dragging.value - 1)
}
function onDrop(e: DragEvent) {
  e.preventDefault()
  dragging.value = 0
  const files = [...(e.dataTransfer?.files ?? [])]
  void addFiles(files)
}

onMounted(async () => {
  window.addEventListener('resize', onResize)
  window.addEventListener('keydown', onKey)
  try {
    await init()
    ready.value = true
  } catch (e) {
    initError.value = e instanceof Error ? e.message : String(e)
  }
})
onBeforeUnmount(() => {
  window.removeEventListener('resize', onResize)
  window.removeEventListener('keydown', onKey)
})
</script>

<template>
  <div v-if="initError" class="init-error">
    <strong>FlyknitBuddy</strong>
    <p>{{ initError }}</p>
    <button type="button" class="btn primary" @click="reload">Reload</button>
  </div>
  <div v-else-if="!ready" class="boot">
    <div class="boot-card" role="status" aria-live="polite">
      <span class="spinner" />
      <p class="boot-title">{{ t('ui.loading.title') }}</p>
      <p class="boot-sub">{{ t('ui.loading.subtitle') }}</p>
    </div>
  </div>
  <div
    v-else
    class="shell"
    :class="{ narrow }"
    @dragenter.prevent="onDragEnter"
    @dragover.prevent
    @dragleave="onDragLeave"
    @drop="onDrop"
  >
    <div v-if="narrow && drawer" class="drawer-scrim" @click="drawer = false" />
    <Sidebar v-show="narrow ? drawer : !sidebarHidden" class="side" :class="{ drawer: narrow }" @navigate="drawer = false" />

    <main class="main">
      <TopBar :narrow="narrow" :sidebar-hidden="narrow || sidebarHidden" @toggle-sidebar="toggleSidebar" />
      <div class="body">
        <div v-if="inConversation" class="conversation">
          <MessageList />
          <Composer ref="composer" />
        </div>
        <Home v-else ref="home" />
        <PlanPanel v-if="showPlan" :plan="currentState!.plan" />
      </div>
    </main>

    <div v-if="dragging > 0" class="dropzone" aria-hidden="true">
      <div><FilePlus2 :size="30" /><span>{{ t('input.drop') }}</span></div>
    </div>

    <SettingsDialog v-if="state.settingsOpen" />
    <SkillsDialog v-if="state.skillsOpen" @use="useSkill" />
    <MemoryDialog v-if="state.memoryOpen" />
    <UsageDialog v-if="state.usageOpen" />
    <SchedulesDialog v-if="state.schedulesOpen" />
    <div v-if="state.toast" class="toast" role="status">{{ state.toast }}</div>
    <ResizeEdges />
  </div>
</template>

<style scoped>
.init-error {
  display: grid;
  place-content: center;
  gap: 10px;
  height: 100%;
  text-align: center;
  color: var(--ink-soft);
}
/* 启动卡片：与 index.html 和宿主窗口中的加载卡片保持一致，切换时不闪烁 */
.boot {
  position: fixed;
  inset: 0;
  display: grid;
  place-items: center;
  background: #f4f6f9;
}
.boot-card {
  width: 380px;
  max-width: calc(100vw - 32px);
  padding: 32px 36px;
  border-radius: 16px;
  border: 1px solid #e6e8ee;
  background: #fff;
  box-shadow: 0 12px 32px -10px rgba(27, 36, 64, 0.18);
  text-align: center;
}
.spinner {
  display: block;
  width: 40px;
  height: 40px;
  margin: 0 auto 20px;
  border-radius: 50%;
  border: 4px solid #e9ebf2;
  border-top-color: #3446c9;
  animation: spin 900ms linear infinite;
}
.boot-title {
  margin: 0;
  font-size: 17px;
  font-weight: 600;
  color: #1f2330;
}
.boot-sub {
  margin: 8px 0 0;
  font-size: 13px;
  color: #7a8092;
}
@keyframes spin {
  to {
    transform: rotate(360deg);
  }
}
.shell {
  display: flex;
  height: 100%;
  position: relative;
}
.side {
  width: var(--sidebar-w);
  flex: none;
}
.side.drawer {
  position: absolute;
  z-index: 40;
  top: 0;
  bottom: 0;
  left: 0;
  box-shadow: var(--shadow-pop);
  animation: slide 180ms ease-out;
}
.drawer-scrim {
  position: absolute;
  inset: 0;
  z-index: 39;
  background: color-mix(in srgb, var(--ink) 20%, transparent);
}
.main {
  flex: 1;
  min-width: 0;
  display: flex;
  flex-direction: column;
}
.body {
  flex: 1;
  min-height: 0;
  display: flex;
}
.conversation {
  flex: 1;
  min-width: 0;
  display: flex;
  flex-direction: column;
}

.dropzone {
  position: absolute;
  inset: 10px;
  z-index: 70;
  display: grid;
  place-items: center;
  border: 2px dashed var(--indigo);
  border-radius: var(--r-lg);
  background: color-mix(in srgb, var(--indigo-wash) 88%, transparent);
  pointer-events: none;
}
.dropzone div {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 10px;
  color: var(--indigo);
  font-weight: 600;
  font-size: var(--t-lg);
}
.toast {
  position: absolute;
  left: 50%;
  bottom: 96px;
  z-index: 80;
  transform: translateX(-50%);
  max-width: min(520px, calc(100% - 32px));
  padding: 10px 16px;
  border-radius: var(--r-md);
  background: var(--ink);
  color: var(--loom);
  font-size: var(--t-sm);
  box-shadow: var(--shadow-pop);
}
@keyframes slide {
  from {
    transform: translateX(-24px);
    opacity: 0;
  }
}
</style>
