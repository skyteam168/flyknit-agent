<script setup lang="ts">
/**
 * IDE 中间：打开的文件标签 + Monaco。
 * - AI 改过的文件显示对比视图，上面一条「AI 改了这个文件」，看完点「收起对比」；
 * - 员工有没保存的修改时 AI 也改了：不动员工的内容，让员工选「用 AI 的」或「用我的」；
 * - Ctrl+S 保存；只读文件（二进制、太大、链接到工作区外）说明原因。
 * - 标签栏参照 VS Code / Cursor：标签多了横向滚动（滚轮也能滚），当前标签自动滚到看得见的地方；
 *   右边只留「保存」（有没保存的修改时）和「…」：「…」里列出没显示出来的标签、跟随 AI、在 VS Code 中打开、关闭其他 / 已保存 / 全部；
 *   标签上右键：关闭、关闭其他、关闭右侧、关闭已保存、复制路径。
 */
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { Bot, Check, Code2, ExternalLink, FolderGit2, GitCompare, MoreHorizontal, Save, ShieldCheck, X } from '@lucide/vue'
import { bridge } from '../../bridge'
import Popover from '../../components/Popover.vue'
import { EDITOR_FONTS, EDITOR_THEMES, FONT_WEIGHTS, clampFont, editorPrefs, resetEditorPrefs } from './prefs'
import { current, send, state, toast } from '../../store'
import MonacoView from './MonacoView.vue'
import { acceptDiff, activeTab, closeCodeTab, currentCode, executePlanInIde, isDirty, keepMine, keepTurn, narrowToProject, pendingTurnFor, planEdited, projects, saveCodeTab, selectionMarkdown, takeTheirs, undoTurn } from './store'
import type { CodeSelection, CodeTab } from './store'
import { lsp, serverFor, trustProject } from './lsp'

const emit = defineEmits<{ insertText: [text: string] }>()
const { t } = useI18n()

/** Ctrl+L：选中的代码带上文件和行号放进输入框，员工接着说要做什么 */
function addToChat(sel: CodeSelection) {
  emit('insertText', selectionMarkdown(sel, t('ui.code.snippetHeader', { path: sel.path, from: sel.from, to: sel.to })))
}

/** Ctrl+K：在编辑器里直接说怎么改，交给 AI（只改选中的这几行） */
async function quickEdit(sel: CodeSelection, instruction: string) {
  const text = selectionMarkdown(sel, t('ui.code.quickEditPrompt', { path: sel.path, from: sel.from, to: sel.to, instruction }))
  if (await send(text, { noSplit: true })) {
    toast(t('ui.code.quickEditSent'))
  } else {
    // AI 正在做别的：先放进输入框，等它做完再发（或者员工自己插话）
    emit('insertText', text)
    toast(t('ui.code.quickEditBusy'))
  }
}
const tabs = computed(() => currentCode.value?.tabs ?? [])

async function save() {
  const tab = activeTab.value
  const id = current.value?.id
  if (!tab || !id) return
  const r = await saveCodeTab(id, tab)
  if (r === 'saved') toast(t('ui.code.saved', { name: tab.name }))
}

function close(path: string) {
  const id = current.value?.id
  const tab = tabs.value.find((x) => x.path === path)
  if (!id || !tab) return
  if (isDirty(tab) && !window.confirm(t('ui.code.closeDirty', { name: tab.name }))) return
  closeCodeTab(id, path)
}

/** 一次关几个标签：有没保存的先问一次 */
function closeMany(list: CodeTab[]) {
  const id = current.value?.id
  if (!id || list.length === 0) return
  const dirty = list.filter(isDirty).length
  if (dirty > 0 && !window.confirm(t('ui.code.tabs.closeDirtyMany', { n: dirty }))) return
  for (const tab of list) closeCodeTab(id, tab.path)
}
const closeOthers = (keep: CodeTab) => closeMany(tabs.value.filter((x) => x !== keep))
const closeRight = (from: CodeTab) => closeMany(tabs.value.slice(tabs.value.indexOf(from) + 1))
const closeSaved = () => closeMany(tabs.value.filter((x) => !isDirty(x)))
const closeAll = () => closeMany([...tabs.value])

// ---------- 标签多了：滚动、看不见的标签收进「…」 ----------

const strip = ref<HTMLElement>()
/** 没完整显示出来的标签（被挤到可视区域外面的） */
const hidden = ref<string[]>([])
function measure() {
  const el = strip.value
  if (!el) return
  const box = el.getBoundingClientRect()
  hidden.value = [...el.querySelectorAll<HTMLElement>('.tab')]
    .filter((x) => {
      const r = x.getBoundingClientRect()
      return r.left < box.left - 1 || r.right > box.right + 1
    })
    .map((x) => x.dataset.path ?? '')
}
const hiddenTabs = computed(() => hidden.value.map((p) => tabs.value.find((x) => x.path === p)).filter((x): x is CodeTab => !!x))

function reveal(path: string) {
  void nextTick(() => {
    const el = strip.value?.querySelector<HTMLElement>(`.tab[data-path="${CSS.escape(path)}"]`)
    el?.scrollIntoView({ block: 'nearest', inline: 'nearest' })
    measure()
  })
}
function activate(path: string) {
  if (!currentCode.value) return
  currentCode.value.active = path
  reveal(path)
}
/** 竖着滚滚轮也能横着翻标签（VS Code 一样） */
function onWheel(e: WheelEvent) {
  const el = strip.value
  if (!el || Math.abs(e.deltaY) <= Math.abs(e.deltaX)) return
  el.scrollLeft += e.deltaY
  e.preventDefault()
}

watch(() => currentCode.value?.active, (path) => path && reveal(path))
watch(() => tabs.value.map((x) => x.path).join('|'), () => void nextTick(measure))
let resize: ResizeObserver | null = null
onMounted(() => {
  resize = new ResizeObserver(measure)
  if (strip.value) resize.observe(strip.value)
  measure()
})
onBeforeUnmount(() => {
  resize?.disconnect()
  document.removeEventListener('mousedown', closeMenu)
})

// ---------- 标签右键菜单 ----------

const menu = ref<{ tab: CodeTab; x: number; y: number } | null>(null)
function openMenu(e: MouseEvent, tab: CodeTab) {
  menu.value = { tab, x: Math.min(e.clientX, window.innerWidth - 200), y: e.clientY }
  document.addEventListener('mousedown', closeMenu)
}
function closeMenu(e?: MouseEvent) {
  if (e && (e.target as HTMLElement).closest?.('.tab-menu')) return
  menu.value = null
  document.removeEventListener('mousedown', closeMenu)
}
function menuDo(fn: () => void) {
  fn()
  closeMenu()
}
async function copyPath(tab: CodeTab) {
  try {
    await navigator.clipboard.writeText(tab.fullPath)
    toast(t('ui.code.tabs.copied'))
  } catch {
    toast(tab.fullPath)
  }
}
const dirOf = (tab: CodeTab) => tab.path.slice(0, Math.max(0, tab.path.length - tab.name.length)).replace(/[\\/]+$/, '')

/** 这个文件最近一次被 AI 改、还没处理的那一轮（对比条上的「保留 / 撤销此文件」用） */
const fileTurn = computed(() => (current.value && activeTab.value ? pendingTurnFor(current.value.id, activeTab.value.fullPath) : null))

/** 保留这个文件的改动：收起对比（整轮都保留在输入框上方的「保留」） */
function keepFile() {
  const tab = activeTab.value
  if (!tab) return
  acceptDiff(tab)
}

/** 撤销这个文件的改动：恢复成 AI 这一轮改之前的样子 */
function undoFile() {
  const tab = activeTab.value
  if (!tab || !current.value || !fileTurn.value) return
  void undoTurn(
    current.value.id,
    fileTurn.value.turnId,
    (n, dirty) => t('ui.code.decide.undoFileConfirm', { name: tab.name }) + (dirty ? '\n' + t('ui.code.decide.undoDirty', { n: dirty }) : ''),
    (n) => t('ui.code.decide.undone', { n }),
    [tab.fullPath],
  )
}

/** 用员工自己装的 VS Code 打开工作区（开着文件就跳到这个文件） */
async function openVsCode() {
  if (!current.value) return
  try {
    const r = await bridge.openInVsCode(current.value.id, activeTab.value && !activeTab.value.virtual ? activeTab.value.path : '')
    toast(r.ok ? t('ui.code.vscodeOpened') : r.message)
  } catch (e) {
    toast(String((e as Error)?.message ?? e))
  }
}

/** 当前文件属于工作区里的某个项目、而工作区比它大（D:\\AIWORK 里的 vue3-login）：提示可以收窄 */
const project = computed(() => (current.value ? projects[current.value.id] : undefined))
const base = (p: string) => p.split(/[\\/]/).filter(Boolean).pop() ?? p

// ---------- 语言服务（./lsp.ts）：没信任的项目问一句；没装的语言提示找 IT ----------
const lspServer = computed(() => (current.value && activeTab.value && !activeTab.value.virtual ? serverFor(current.value.id, activeTab.value.path) : null))
const lspStatus = computed(() => (current.value ? lsp.status[current.value.id] : null))
const askTrust = computed(() => {
  const st = lspStatus.value
  const ws = current.value?.workspace ?? ''
  return !!st?.enabled && !st.trusted && !!lspServer.value?.available && !lsp.dismissed['trust:' + ws]
})
const lspMissing = computed(() => {
  const s = lspServer.value
  return !!lspStatus.value?.enabled && !!s && !s.available && !s.disabled && !lsp.dismissed['missing:' + s.id]
})
function trust(yes: boolean) {
  if (!current.value) return
  if (yes) void trustProject(current.value.id, true)
  else lsp.dismissed['trust:' + (current.value.workspace ?? '')] = true
}

/** 方案标签：执行（改过的方案一起发过去） */
const planEditedNow = computed(() => !!current.value && !!activeTab.value?.virtual && planEdited(current.value.id))
function executePlan() {
  if (current.value) void executePlanInIde(current.value.id, { go: t('ui.planMode.go'), goEdited: t('ui.code.plan.goEdited') })
}

const reasonText = computed(() => {
  const r = activeTab.value?.reason ?? ''
  return r === 'binary' || r === 'tooLarge' || r === 'outside' ? t(`ui.code.readOnly.${r}`) : r
})
</script>

<template>
  <section class="editor-area">
    <div class="tabbar">
      <div ref="strip" class="tabs" role="tablist" @wheel="onWheel" @scroll.passive="measure">
        <div
          v-for="tab in tabs"
          :key="tab.path"
          class="tab"
          :class="{ on: currentCode?.active === tab.path }"
          role="tab"
          :data-path="tab.path"
          :aria-selected="currentCode?.active === tab.path"
          :title="tab.path"
          @click="activate(tab.path)"
          @auxclick.middle="close(tab.path)"
          @contextmenu.prevent="openMenu($event, tab)"
        >
          <span class="name">{{ tab.name }}</span>
          <span v-if="tab.aiChanged" class="ai" :title="t('ui.code.aiChanged')"><Bot :size="11" /></span>
          <span v-if="isDirty(tab)" class="dirty" :title="t('ui.code.unsaved')">●</span>
          <button type="button" class="x" :aria-label="t('ui.code.close')" @click.stop="close(tab.path)"><X :size="12" /></button>
        </div>
      </div>
      <div class="actions">
        <button v-if="activeTab && !activeTab.readOnly && isDirty(activeTab)" type="button" class="icon" :title="t('ui.code.saveHint')" :aria-label="t('ui.code.save')" @click="save">
          <Save :size="15" />
        </button>
        <Popover placement="down" align="end" :width="300">
          <template #trigger="{ toggle, open }">
            <button
              type="button"
              class="icon more"
              :class="{ on: open }"
              :title="hiddenTabs.length ? t('ui.code.tabs.moreHidden', { n: hiddenTabs.length }) : t('ui.code.tabs.more')"
              :aria-label="t('ui.code.tabs.more')"
              @click="measure(), toggle()"
            >
              <MoreHorizontal :size="16" />
              <span v-if="hiddenTabs.length" class="badge">{{ hiddenTabs.length }}</span>
            </button>
          </template>
          <template #default="{ close: closePop }">
            <div class="more-menu">
              <template v-if="hiddenTabs.length">
                <p class="group">{{ t('ui.code.tabs.hidden', { n: hiddenTabs.length }) }}</p>
                <button v-for="tab in hiddenTabs" :key="tab.path" type="button" class="item file" :title="tab.path" @click="activate(tab.path), closePop()">
                  <span class="fname">{{ tab.name }}</span>
                  <span v-if="isDirty(tab)" class="dirty">●</span>
                  <span class="dir">{{ dirOf(tab) }}</span>
                </button>
                <hr />
              </template>
              <button v-if="currentCode" type="button" class="item" :title="t('ui.code.followHint')" @click="currentCode.follow = !currentCode.follow">
                <span class="check"><Check v-if="currentCode.follow" :size="13" /></span>{{ t('ui.code.follow') }}
              </button>
              <button type="button" class="item" :title="t('ui.code.vscodeHint')" @click="openVsCode(), closePop()">
                <span class="check"><ExternalLink :size="13" /></span>{{ t('ui.code.tabs.vscode') }}
              </button>
              <!-- 编辑器外观：主题、字号、粗细、字体、行高、换行、缩略图、连字（每个员工自己的偏好，记在本机） -->
              <hr />
              <p class="group">{{ t('ui.code.appearance.title') }}</p>
              <div class="pref">
                <label>{{ t('ui.code.appearance.theme') }}</label>
                <select v-model="editorPrefs.theme">
                  <option v-for="th in EDITOR_THEMES" :key="th" :value="th">{{ t(`ui.code.appearance.themes.${th}`) }}</option>
                </select>
              </div>
              <div class="pref">
                <label>{{ t('ui.code.appearance.fontSize') }}</label>
                <span class="stepper">
                  <button type="button" :aria-label="t('ui.code.appearance.smaller')" @click="editorPrefs.fontSize = clampFont(editorPrefs.fontSize - 1)">−</button>
                  <b>{{ editorPrefs.fontSize }}</b>
                  <button type="button" :aria-label="t('ui.code.appearance.larger')" @click="editorPrefs.fontSize = clampFont(editorPrefs.fontSize + 1)">+</button>
                </span>
              </div>
              <div class="pref">
                <label>{{ t('ui.code.appearance.fontWeight') }}</label>
                <select v-model="editorPrefs.fontWeight">
                  <option v-for="w in FONT_WEIGHTS" :key="w" :value="w">{{ t(`ui.code.appearance.weights.w${w}`) }}</option>
                </select>
              </div>
              <div class="pref">
                <label>{{ t('ui.code.appearance.font') }}</label>
                <select v-model="editorPrefs.font">
                  <option v-for="f in EDITOR_FONTS" :key="f.id" :value="f.id">{{ t(`ui.code.appearance.fonts.${f.id}`) }}</option>
                </select>
              </div>
              <div class="pref">
                <label>{{ t('ui.code.appearance.lineHeight') }}</label>
                <select v-model.number="editorPrefs.lineHeight">
                  <option :value="0">{{ t('ui.code.appearance.lineAuto') }}</option>
                  <option v-for="h in [18, 20, 22, 24, 26, 28]" :key="h" :value="h">{{ h }}</option>
                </select>
              </div>
              <label class="pref check-row"><input v-model="editorPrefs.wordWrap" type="checkbox" />{{ t('ui.code.appearance.wordWrap') }}</label>
              <label class="pref check-row"><input v-model="editorPrefs.minimap" type="checkbox" />{{ t('ui.code.appearance.minimap') }}</label>
              <label class="pref check-row" :title="t('ui.code.appearance.ligaturesHint')"><input v-model="editorPrefs.ligatures" type="checkbox" />{{ t('ui.code.appearance.ligatures') }}</label>
              <button type="button" class="item" @click="resetEditorPrefs()"><span class="check" />{{ t('ui.code.appearance.reset') }}</button>
              <!-- 语言服务：哪些语言装了、这个项目信任了没有 -->
              <template v-if="lspStatus?.enabled">
                <hr />
                <p class="group">{{ t('ui.code.lsp.title') }}</p>
                <div v-for="sv in lspStatus.servers" :key="sv.id" class="item lsp-row" :title="sv.available ? sv.exe : t('ui.code.lsp.installHint', { hint: sv.installHint })">
                  <span class="dot" :class="{ on: sv.available && lspStatus.trusted, off: !sv.available || sv.disabled }" />
                  <span>{{ sv.name }}</span>
                  <span class="lsp-state">{{ sv.disabled ? t('ui.code.lsp.disabled') : !sv.available ? t('ui.code.lsp.notInstalled') : sv.running ? t('ui.code.lsp.running') : t('ui.code.lsp.ready') }}</span>
                </div>
                <button v-if="lspStatus.trusted" type="button" class="item" @click="trustProject(current!.id, false), closePop()">
                  <span class="check" />{{ t('ui.code.lsp.untrust') }}
                </button>
                <button v-else type="button" class="item" @click="trustProject(current!.id, true), closePop()">
                  <span class="check"><ShieldCheck :size="13" /></span>{{ t('ui.code.lsp.trust') }}
                </button>
              </template>
              <template v-if="tabs.length">
                <hr />
                <button v-if="activeTab && tabs.length > 1" type="button" class="item" @click="closeOthers(activeTab), closePop()"><span class="check" />{{ t('ui.code.tabs.closeOthers') }}</button>
                <button type="button" class="item" @click="closeSaved(), closePop()"><span class="check" />{{ t('ui.code.tabs.closeSaved') }}</button>
                <button type="button" class="item" @click="closeAll(), closePop()"><span class="check" />{{ t('ui.code.tabs.closeAll') }}</button>
              </template>
            </div>
          </template>
        </Popover>
      </div>
    </div>
    <!-- 标签右键菜单 -->
    <div v-if="menu" class="tab-menu" :style="{ left: menu.x + 'px', top: menu.y + 'px' }" role="menu">
      <button type="button" @click="menuDo(() => close(menu!.tab.path))">{{ t('ui.code.close') }}</button>
      <button type="button" :disabled="tabs.length < 2" @click="menuDo(() => closeOthers(menu!.tab))">{{ t('ui.code.tabs.closeOthers') }}</button>
      <button type="button" :disabled="tabs.indexOf(menu.tab) === tabs.length - 1" @click="menuDo(() => closeRight(menu!.tab))">{{ t('ui.code.tabs.closeRight') }}</button>
      <button type="button" @click="menuDo(closeSaved)">{{ t('ui.code.tabs.closeSaved') }}</button>
      <button type="button" @click="menuDo(closeAll)">{{ t('ui.code.tabs.closeAll') }}</button>
      <hr />
      <button v-if="!menu.tab.virtual" type="button" @click="menuDo(() => void copyPath(menu!.tab))">{{ t('ui.code.tabs.copyPath') }}</button>
    </div>

    <!-- 当前项目（从打开的文件往上找到 package.json / .git 之类推断）；工作区比它大时可以一键收窄 -->
    <div v-if="project?.projectRoot && project.narrower" class="banner project">
      <FolderGit2 :size="14" />
      <span>{{ t('ui.code.project.current', { name: project.projectName, workspace: base(project.workspace) }) }}</span>
      <button type="button" class="primary" :title="t('ui.code.project.narrowHint', { path: project.projectRoot })" @click="narrowToProject(current!.id)">
        {{ t('ui.code.project.narrow') }}
      </button>
    </div>
    <template v-if="activeTab">
      <!-- 语言服务：项目还没信任（语言服务会运行项目里的部分代码）、或者这台电脑没装这种语言的 -->
      <div v-if="askTrust" class="banner lsp">
        <ShieldCheck :size="14" />
        <span>{{ t('ui.code.lsp.trustAsk', { name: lspServer!.name }) }}</span>
        <button type="button" @click="trust(false)">{{ t('ui.code.lsp.notNow') }}</button>
        <button type="button" class="primary" @click="trust(true)">{{ t('ui.code.lsp.trust') }}</button>
      </div>
      <div v-else-if="lspMissing" class="banner">
        <span>{{ t('ui.code.lsp.missing', { name: lspServer!.name }) }}</span>
        <button type="button" @click="lsp.dismissed['missing:' + lspServer!.id] = true">{{ t('ui.code.lsp.gotIt') }}</button>
      </div>
      <!-- 计划模式的方案文档：可以直接改，改完点执行，以这一版为准 -->
      <div v-if="activeTab.virtual" class="banner plan">
        <span>{{ planEditedNow ? t('ui.code.plan.docEdited') : t('ui.code.plan.docHint') }}</span>
        <button v-if="current && state.planMode[current.id]" type="button" class="primary" @click="executePlan">{{ t('ui.code.plan.execute') }}</button>
      </div>
      <div v-if="activeTab.conflict" class="banner warn">
        <span>{{ t('ui.code.conflict') }}</span>
        <button type="button" @click="takeTheirs(activeTab)">{{ t('ui.code.takeTheirs') }}</button>
        <button type="button" class="primary" @click="keepMine(current!.id, activeTab)">{{ t('ui.code.keepMine') }}</button>
      </div>
      <!-- 源代码管理打开的对比：和上次提交比，可以直接在右边改 -->
      <div v-else-if="activeTab.before !== null && activeTab.diffSource === 'git'" class="banner git">
        <GitCompare :size="14" />
        <span>{{ t('ui.code.git.diffBanner') }}</span>
        <button type="button" class="primary" @click="acceptDiff(activeTab)">{{ t('ui.code.git.closeDiff') }}</button>
      </div>
      <div v-else-if="activeTab.before !== null || (activeTab.aiChanged && fileTurn)" class="banner ai">
        <Bot :size="14" />
        <span>{{ activeTab.before !== null ? t('ui.code.diffBanner') : t('ui.code.decide.changedByAi') }}</span>
        <button v-if="fileTurn" type="button" :title="t('ui.code.decide.undoFileHint')" @click="undoFile">{{ t('ui.code.decide.undoFile') }}</button>
        <button type="button" class="primary" :title="t('ui.code.decide.keepFileHint')" @click="keepFile">{{ t('ui.code.decide.keepFile') }}</button>
      </div>
      <div v-else-if="activeTab.readOnly" class="banner">{{ reasonText }}</div>
      <MonacoView :tab="activeTab" @save="save" @add-to-chat="addToChat" @quick-edit="quickEdit" />
    </template>
    <div v-else class="empty">
      <Code2 :size="34" />
      <strong>{{ t('ui.code.emptyTitle') }}</strong>
      <p>{{ t('ui.code.emptyHint') }}</p>
    </div>
  </section>
</template>

<style scoped>
.editor-area {
  display: flex;
  flex-direction: column;
  flex: 1;
  min-height: 0;
  min-width: 0;
  background: var(--cloth);
}
.tabbar {
  position: relative;
  z-index: 30;
  display: flex;
  flex: none;
  align-items: stretch;
  min-height: 34px;
  border-bottom: 1px solid var(--line);
  background: var(--loom);
}
.tabs {
  display: flex;
  flex: 1;
  align-items: stretch;
  min-width: 0;
  overflow-x: auto;
  overflow-y: hidden;
  scrollbar-width: none;
}
.tabs:hover {
  scrollbar-width: thin;
}
.tab {
  display: flex;
  flex: none;
  align-items: center;
  gap: 5px;
  max-width: 220px;
  padding: 0 6px 0 12px;
  border-right: 1px solid var(--line);
  color: var(--ink-soft);
  font-size: calc(12.5px * var(--font-scale));
  cursor: pointer;
  white-space: nowrap;
}
.tab.on {
  background: var(--cloth);
  color: var(--ink);
  box-shadow: inset 0 2px 0 var(--indigo);
}
.tab .name {
  overflow: hidden;
  text-overflow: ellipsis;
}
.ai {
  display: grid;
  place-items: center;
  color: var(--indigo);
}
.dirty {
  color: var(--amber);
  font-size: 10px;
}
.x {
  display: grid;
  place-items: center;
  width: 20px;
  height: 20px;
  border: 0;
  border-radius: 4px;
  background: transparent;
  color: inherit;
  opacity: 0.5;
  cursor: pointer;
}
.x:hover {
  opacity: 1;
  background: color-mix(in srgb, var(--ink) 8%, transparent);
}
.actions {
  display: flex;
  flex: none;
  align-items: center;
  gap: 2px;
  padding: 0 6px;
  border-left: 1px solid var(--line);
}
.icon {
  position: relative;
  display: grid;
  place-items: center;
  width: 26px;
  height: 26px;
  border: 0;
  border-radius: 5px;
  background: transparent;
  color: var(--ink-soft);
  cursor: pointer;
}
.icon:hover,
.icon.on {
  color: var(--ink);
  background: color-mix(in srgb, var(--ink) 8%, transparent);
}
.icon.more:has(.badge) {
  display: flex;
  gap: 3px;
  width: auto;
  padding: 0 6px 0 4px;
}
.badge {
  min-width: 14px;
  height: 14px;
  padding: 0 3px;
  border-radius: 7px;
  background: var(--indigo);
  color: #fff;
  font-size: 9px;
  line-height: 14px;
  text-align: center;
}
.more-menu {
  display: flex;
  flex-direction: column;
}
.pref {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  padding: 3px 10px;
  font-size: calc(12.5px * var(--font-scale));
}
.pref label {
  color: var(--ink-soft);
}
.pref select {
  max-width: 160px;
  padding: 2px 4px;
  border: 1px solid var(--line);
  border-radius: 5px;
  background: var(--cloth);
  color: var(--ink);
  font: inherit;
}
.stepper {
  display: inline-flex;
  align-items: center;
  gap: 6px;
}
.stepper button {
  width: 22px;
  height: 22px;
  border: 1px solid var(--line);
  border-radius: 5px;
  background: var(--cloth);
  color: var(--ink);
  cursor: pointer;
}
.stepper b {
  min-width: 20px;
  text-align: center;
  font-weight: 600;
}
.check-row {
  justify-content: flex-start;
  color: var(--ink);
  cursor: pointer;
}
.more-menu .group {
  margin: 2px 8px 4px;
  color: var(--ink-faint);
  font-size: calc(11px * var(--font-scale));
}
.more-menu hr,
.tab-menu hr {
  width: 100%;
  margin: 4px 0;
  border: 0;
  border-top: 1px solid var(--line);
}
.item {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 6px 8px;
  border: 0;
  border-radius: 5px;
  background: transparent;
  color: var(--ink);
  font-size: calc(12.5px * var(--font-scale));
  text-align: left;
  cursor: pointer;
}
.item:hover {
  background: color-mix(in srgb, var(--indigo) 14%, transparent);
}
.item .check {
  display: grid;
  flex: none;
  place-items: center;
  width: 16px;
  color: var(--ink-soft);
}
.item.file {
  gap: 6px;
  padding-left: 12px;
}
.fname {
  flex: none;
  max-width: 60%;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.dir {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  color: var(--ink-faint);
  font-size: calc(11px * var(--font-scale));
  text-overflow: ellipsis;
  white-space: nowrap;
  direction: rtl;
  text-align: left;
}
.tab-menu {
  position: fixed;
  z-index: 200;
  display: flex;
  flex-direction: column;
  min-width: 170px;
  padding: 5px;
  border: 1px solid var(--line);
  border-radius: var(--r-md);
  background: var(--cloth);
  box-shadow: var(--shadow-pop);
}
.tab-menu button {
  padding: 6px 10px;
  border: 0;
  border-radius: 5px;
  background: transparent;
  color: var(--ink);
  font-size: calc(12.5px * var(--font-scale));
  text-align: left;
  cursor: pointer;
}
.tab-menu button:hover:not(:disabled) {
  background: color-mix(in srgb, var(--indigo) 14%, transparent);
}
.tab-menu button:disabled {
  opacity: 0.4;
  cursor: default;
}
.banner {
  display: flex;
  flex: none;
  align-items: center;
  gap: 8px;
  padding: 6px 12px;
  border-bottom: 1px solid var(--line);
  background: var(--loom);
  color: var(--ink-soft);
  font-size: calc(12.5px * var(--font-scale));
}
.banner span {
  flex: 1;
}
.banner.lsp {
  background: var(--indigo-wash);
  color: var(--ink);
}
.lsp-row {
  cursor: default;
  font-size: calc(12px * var(--font-scale));
}
.lsp-row:hover {
  background: transparent;
}
.dot {
  flex: none;
  width: 7px;
  height: 7px;
  margin: 0 4px 0 5px;
  border-radius: 50%;
  background: var(--ink-faint);
}
.dot.on {
  background: var(--green, #3fb27f);
}
.dot.off {
  background: color-mix(in srgb, var(--ink) 25%, transparent);
}
.lsp-state {
  margin-left: auto;
  color: var(--ink-faint);
  font-size: calc(11px * var(--font-scale));
}
.banner.git {
  background: var(--thread-wash);
  color: var(--ink);
}
.banner.plan {
  background: var(--amber-wash);
}
.banner.project {
  background: var(--thread-wash);
  color: var(--ink);
}
.banner.ai {
  background: var(--indigo-wash);
  color: var(--indigo);
}
.banner.warn {
  background: var(--amber-wash);
  color: var(--amber);
}
.banner button {
  padding: 3px 10px;
  border: 1px solid var(--line);
  border-radius: 6px;
  background: var(--cloth);
  color: var(--ink);
  font-size: calc(12px * var(--font-scale));
  cursor: pointer;
}
.banner button.primary {
  border-color: var(--indigo);
  background: var(--indigo);
  color: #fff;
}
.empty {
  display: flex;
  flex: 1;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 8px;
  padding: 24px;
  color: var(--ink-faint);
  text-align: center;
}
.empty strong {
  color: var(--ink-soft);
}
.empty p {
  max-width: 360px;
  margin: 0;
  font-size: calc(12.5px * var(--font-scale));
}
</style>
