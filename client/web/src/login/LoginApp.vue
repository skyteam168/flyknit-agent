<script setup lang="ts">
import { computed, nextTick, onMounted, ref } from 'vue'
import MarkdownIt from 'markdown-it'
import DOMPurify from 'dompurify'
import { AlertCircle, ArrowLeft, Eye, EyeOff, Lock, Minus, Server, ShieldCheck, User, X, KeyRound } from '@lucide/vue'
import mark from '../assets/flyknitbuddy-mark.png'
import { HostError, host, type InitInfo, type LegalDoc, type LegalInfo } from './host'
import { setLanguage, t } from './i18n'

type View = 'authorize' | 'password' | 'manual'

const info = ref<InitInfo | null>(null)
const view = ref<View>('authorize')
const agreed = ref(false)
const nudge = ref(false)
const busy = ref(false)
const error = ref('')
const success = ref(false)

const username = ref('')
const password = ref('')
const showPassword = ref(false)
const passwordBox = ref<HTMLInputElement>()

const server = ref('http://')
const key = ref('')

const legal = ref<LegalInfo | null>(null)
const reading = ref<LegalDoc | null>(null)

const account = computed(() => info.value?.account)
const initial = computed(() => (account.value?.user || '?').slice(0, 1).toUpperCase())
const serverHost = computed(() => {
  try {
    return new URL(info.value?.server ?? '').host
  } catch {
    return info.value?.server ?? ''
  }
})

onMounted(async () => {
  const i = await host.init()
  setLanguage(i.lang)
  info.value = i
  username.value = i.account.display
  view.value = i.provisioned ? 'authorize' : 'manual'
  if (i.provisioned) void loadLegal()
})

async function loadLegal(): Promise<LegalInfo | null> {
  try {
    legal.value = await host.legal()
  } catch (e) {
    error.value = t('legalFailed', (e as Error).message)
  }
  return legal.value
}

const md = new MarkdownIt({ html: false, linkify: true, breaks: false })
const readingHtml = computed(() => (reading.value ? DOMPurify.sanitize(md.render(reading.value.content)) : ''))

async function openDoc(kind: 'terms' | 'privacy') {
  const docs = legal.value ?? (await loadLegal())
  reading.value = docs?.docs.find((d) => d.kind === kind) ?? null
}

function acceptDoc() {
  agreed.value = true
  reading.value = null
}

function go(next: View) {
  view.value = next
  error.value = ''
  if (next === 'password') void nextTick(() => passwordBox.value?.focus())
}

/** 没勾同意：抖一下复选框，提示先同意 */
function requireAgreement(): boolean {
  if (agreed.value) return true
  nudge.value = false
  void nextTick(() => (nudge.value = true))
  setTimeout(() => (nudge.value = false), 2400)
  return false
}

async function signIn(mode: 'windows' | 'password') {
  if (busy.value || !requireAgreement()) return
  const docs = legal.value ?? (await loadLegal())
  if (!docs) return
  busy.value = true
  error.value = ''
  try {
    await host.login({
      mode,
      username: mode === 'password' ? username.value.trim() : undefined,
      password: mode === 'password' ? password.value : undefined,
      agreed: docs.versions,
    })
    finish()
  } catch (e) {
    const err = e as HostError
    if (err.code === 'need_password') {
      // 本机账号点了「授权登录」：Windows 没法替他担保，要输一次密码
      go('password')
    } else if (err.code === 'wrong_password') {
      error.value = t('wrongPassword')
      password.value = ''
      passwordBox.value?.focus()
    } else if (err.code === 'legal_outdated') {
      agreed.value = false
      legal.value = null
      error.value = t('legalOutdated')
      void loadLegal()
    } else {
      error.value = err.message
    }
  } finally {
    busy.value = false
  }
}

async function connect() {
  if (busy.value) return
  busy.value = true
  error.value = ''
  try {
    await host.manual(server.value.trim(), key.value.trim())
    finish()
  } catch (e) {
    error.value = (e as Error).message
  } finally {
    busy.value = false
  }
}

function finish() {
  success.value = true
  setTimeout(() => void host.done(), 1100)
}
</script>

<template>
  <div class="login" :class="{ ready: info }">
    <div class="aurora" aria-hidden="true"><i class="a1" /><i class="a2" /><i class="a3" /></div>
    <div class="grid" aria-hidden="true" />

    <header class="titlebar">
      <button type="button" class="tb" :title="t('minimize')" @click="host.window('minimize')"><Minus :size="16" /></button>
      <button type="button" class="tb close" :title="t('close')" @click="host.window('close')"><X :size="16" /></button>
    </header>

    <main v-if="info" class="content">
      <div class="brand">
        <div class="badge">
          <span class="ring" />
          <img :src="mark" alt="" />
        </div>
        <h1>FlyknitBuddy</h1>
        <p class="slogan">{{ t('slogan') }}</p>
      </div>

      <Transition name="swap" mode="out-in">
        <!-- 授权登录：用当前 Windows 账号 -->
        <section v-if="view === 'authorize'" key="authorize" class="panel">
          <div class="account">
            <span class="avatar">{{ initial }}</span>
            <span class="who">
              <strong :title="account!.display">{{ account!.display }}</strong>
              <small>{{ account!.kind === 'domain' ? t('authorizeHint') : t('localHint') }}</small>
            </span>
            <span class="tag" :class="account!.kind">{{ account!.kind === 'domain' ? t('domainAccount') : t('localAccount') }}</span>
          </div>
          <button type="button" class="primary" :class="{ busy }" :disabled="busy" @click="signIn('windows')">
            <span v-if="busy" class="spinner" />
            <span>{{ busy ? t('signingIn') : t('authorize') }}</span>
          </button>
        </section>

        <!-- 账号密码 -->
        <section v-else-if="view === 'password'" key="password" class="panel">
          <label class="field">
            <User :size="18" class="icon" />
            <input v-model="username" :placeholder="t('username')" autocomplete="username" spellcheck="false" @keydown.enter="passwordBox?.focus()" />
          </label>
          <label class="field">
            <Lock :size="18" class="icon" />
            <input
              ref="passwordBox"
              v-model="password"
              :type="showPassword ? 'text' : 'password'"
              :placeholder="t('password')"
              autocomplete="current-password"
              @keydown.enter="signIn('password')"
            />
            <button type="button" class="eye" tabindex="-1" @click="showPassword = !showPassword">
              <EyeOff v-if="showPassword" :size="17" /><Eye v-else :size="17" />
            </button>
          </label>
          <p class="hint">{{ t('passwordHint') }}</p>
          <button type="button" class="primary" :class="{ busy }" :disabled="busy" @click="signIn('password')">
            <span v-if="busy" class="spinner" />
            <span>{{ busy ? t('signingIn') : t('login') }}</span>
          </button>
          <p class="secure"><ShieldCheck :size="13" /> {{ t('secure') }}</p>
        </section>

        <!-- 手动连接服务器（没有安装包里的开通文件，或者 IT 让换服务器） -->
        <section v-else key="manual" class="panel">
          <p class="intro">{{ t('manualIntro') }}</p>
          <label class="field">
            <Server :size="18" class="icon" />
            <input v-model="server" :placeholder="t('server')" spellcheck="false" />
          </label>
          <label class="field">
            <KeyRound :size="18" class="icon" />
            <input v-model="key" :placeholder="t('key')" spellcheck="false" @keydown.enter="connect" />
          </label>
          <button type="button" class="primary" :class="{ busy }" :disabled="busy" @click="connect">
            <span v-if="busy" class="spinner" />
            <span>{{ busy ? t('connecting') : t('connect') }}</span>
          </button>
        </section>
      </Transition>

      <Transition name="fade">
        <p v-if="error" class="error" role="alert"><AlertCircle :size="15" /> {{ error }}</p>
      </Transition>

      <div v-if="view !== 'manual'" class="agree" :class="{ nudge }">
        <button type="button" class="check" role="checkbox" :aria-checked="agreed" :class="{ on: agreed }" @click="agreed = !agreed">
          <svg viewBox="0 0 16 16" aria-hidden="true"><path d="M3.5 8.5l3 3 6-7" /></svg>
        </button>
        <span>
          {{ t('agreePrefix') }}
          <a href="#" @click.prevent="openDoc('terms')">{{ t('terms') }}</a>{{ t('and') }}<a href="#" @click.prevent="openDoc('privacy')">{{ t('privacy') }}</a>
        </span>
        <Transition name="fade"><span v-if="nudge" class="bubble">{{ t('agreeFirst') }}</span></Transition>
      </div>

      <div class="links">
        <a v-if="view === 'authorize'" href="#" @click.prevent="go('password')">{{ t('usePassword') }}</a>
        <a v-else-if="view === 'password'" href="#" @click.prevent="go('authorize')"><ArrowLeft :size="15" /> {{ t('backToAuthorize') }}</a>
        <a v-else-if="info.provisioned" href="#" @click.prevent="go('authorize')"><ArrowLeft :size="15" /> {{ t('backToLogin') }}</a>
      </div>
    </main>

    <footer v-if="info" class="footer">
      <span v-if="info.provisioned" class="server"><ShieldCheck :size="13" /> {{ t('serverLabel', serverHost) }}</span>
      <a v-if="info.provisioned && view !== 'manual'" href="#" @click.prevent="go('manual')">{{ t('manual') }}</a>
      <span class="version">v{{ info.version }}</span>
    </footer>

    <!-- 协议全文：从底部滑上来 -->
    <Transition name="sheet">
      <div v-if="reading" class="sheet-mask" @click.self="reading = null">
        <div class="sheet" role="dialog" :aria-label="reading.title">
          <header>
            <h2>{{ reading.title }}</h2>
            <button type="button" class="tb" :title="t('close')" @click="reading = null"><X :size="18" /></button>
          </header>
          <!-- eslint-disable-next-line vue/no-v-html -- markdown-it 关了 html，再过一遍 DOMPurify -->
          <article class="doc" v-html="readingHtml" />
          <footer><button type="button" class="primary" @click="acceptDoc">{{ t('agreeAndContinue') }}</button></footer>
        </div>
      </div>
    </Transition>

    <!-- 登录成功 -->
    <Transition name="fade">
      <div v-if="success" class="success">
        <svg class="tick" viewBox="0 0 64 64" aria-hidden="true">
          <circle cx="32" cy="32" r="29" />
          <path d="M19 33l9 9 17-19" />
        </svg>
        <strong>{{ t('success') }}</strong>
        <small>{{ t('successSub') }}</small>
      </div>
    </Transition>
  </div>
</template>

<style>
html,
body,
#app {
  height: 100%;
  margin: 0;
}
body {
  overflow: hidden;
  background: #f5f8ff;
  font-family: 'Segoe UI Variable Text', 'Segoe UI', 'Microsoft YaHei UI', 'Microsoft YaHei', 'PingFang SC', system-ui, sans-serif;
  -webkit-font-smoothing: antialiased;
  user-select: none;
}
</style>

<style scoped>
.login {
  --blue: #2563eb;
  --blue-2: #3b82f6;
  --cyan: #06b6d4;
  --ink: #0f172a;
  --ink-2: #475569;
  --ink-3: #94a3b8;
  --line: #e2e8f0;
  position: relative;
  display: flex;
  flex-direction: column;
  height: 100%;
  overflow: hidden;
  color: var(--ink);
  opacity: 0;
  transition: opacity 0.3s ease;
}
.login.ready {
  opacity: 1;
}

/* ---------- 背景：缓慢流动的极光 + 渐隐的点阵 ---------- */
.aurora {
  position: absolute;
  inset: -20%;
  filter: blur(60px);
  pointer-events: none;
}
.aurora i {
  position: absolute;
  border-radius: 50%;
  opacity: 0.55;
  animation: drift 18s ease-in-out infinite alternate;
}
.a1 {
  top: 2%;
  left: 4%;
  width: 52%;
  height: 46%;
  background: radial-gradient(circle, #93c5fd, transparent 70%);
}
.a2 {
  top: 6%;
  right: 0;
  width: 48%;
  height: 40%;
  background: radial-gradient(circle, #a5f3fc, transparent 70%);
  animation-delay: -6s;
}
.a3 {
  bottom: 4%;
  left: 24%;
  width: 60%;
  height: 40%;
  background: radial-gradient(circle, #c7d2fe, transparent 70%);
  animation-delay: -12s;
}
@keyframes drift {
  to {
    transform: translate(6%, 5%) scale(1.12);
  }
}
.grid {
  position: absolute;
  inset: 0;
  background-image: radial-gradient(rgba(37, 99, 235, 0.13) 1px, transparent 1px);
  background-size: 22px 22px;
  mask-image: radial-gradient(ellipse at 50% 18%, #000 0%, transparent 62%);
  pointer-events: none;
}

/* ---------- 标题栏（拖动窗口、最小化、关闭） ---------- */
.titlebar {
  position: relative;
  z-index: 2;
  display: flex;
  justify-content: flex-end;
  gap: 2px;
  height: 40px;
  padding: 6px 8px 0;
  box-sizing: border-box;
  /* 宿主开了 IsNonClientRegionSupportEnabled：按住这一条能拖动窗口（和主界面的顶栏一样） */
  app-region: drag;
  -webkit-app-region: drag;
}
.tb {
  display: grid;
  place-items: center;
  width: 32px;
  height: 28px;
  border: 0;
  border-radius: 8px;
  background: transparent;
  color: var(--ink-2);
  cursor: pointer;
  transition: background 0.15s, color 0.15s;
  app-region: no-drag;
  -webkit-app-region: no-drag;
}
.tb:hover {
  background: rgba(15, 23, 42, 0.06);
}
.tb.close:hover {
  background: #ef4444;
  color: #fff;
}

/* ---------- 品牌区 ---------- */
.content {
  position: relative;
  z-index: 1;
  display: flex;
  flex: 1;
  flex-direction: column;
  align-items: center;
  width: 340px;
  margin: 0 auto;
}
.brand {
  display: flex;
  flex-direction: column;
  align-items: center;
  margin-top: 6px;
  animation: rise 0.6s cubic-bezier(0.2, 0.8, 0.2, 1) both;
}
.badge {
  position: relative;
  display: grid;
  place-items: center;
  width: 108px;
  height: 108px;
  border-radius: 50%;
  background: #fff;
  box-shadow:
    0 22px 44px -18px rgba(37, 99, 235, 0.55),
    0 0 0 8px rgba(255, 255, 255, 0.55);
  animation: float 6s ease-in-out infinite;
}
.badge .ring {
  position: absolute;
  inset: -3px;
  border-radius: 50%;
  background: conic-gradient(from 210deg, #60a5fa, #22d3ee, #818cf8, #60a5fa);
  mask: radial-gradient(circle, transparent 54px, #000 54.5px);
  opacity: 0.9;
  animation: spin 10s linear infinite;
}
.badge img {
  position: relative;
  width: 80px;
  height: 80px;
  object-fit: contain;
}
@keyframes float {
  50% {
    transform: translateY(-5px);
  }
}
@keyframes spin {
  to {
    transform: rotate(360deg);
  }
}
@keyframes rise {
  from {
    opacity: 0;
    transform: translateY(12px);
  }
}
h1 {
  margin: 22px 0 6px;
  font-size: 32px;
  font-weight: 800;
  letter-spacing: -0.6px;
  background: linear-gradient(120deg, #0f172a 20%, #1d4ed8 70%, #0891b2);
  -webkit-background-clip: text;
  background-clip: text;
  color: transparent;
}
.slogan {
  margin: 0;
  color: var(--ink-2);
  font-size: 14.5px;
  letter-spacing: 0.4px;
}

/* ---------- 表单区 ---------- */
.panel {
  display: flex;
  flex-direction: column;
  gap: 12px;
  width: 100%;
  margin-top: 30px;
}
.account {
  display: flex;
  gap: 12px;
  align-items: center;
  padding: 12px 14px;
  border: 1px solid rgba(226, 232, 240, 0.9);
  border-radius: 16px;
  background: rgba(255, 255, 255, 0.72);
  backdrop-filter: blur(12px);
  box-shadow: 0 6px 20px -12px rgba(15, 23, 42, 0.25);
}
.avatar {
  display: grid;
  flex: none;
  place-items: center;
  width: 38px;
  height: 38px;
  border-radius: 12px;
  background: linear-gradient(135deg, var(--blue), var(--cyan));
  color: #fff;
  font-size: 16px;
  font-weight: 700;
}
.who {
  display: flex;
  flex: 1;
  flex-direction: column;
  gap: 2px;
  min-width: 0;
}
.who strong {
  overflow: hidden;
  font-size: 14px;
  font-weight: 600;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.who small {
  color: var(--ink-3);
  font-size: 12px;
}
.tag {
  flex: none;
  padding: 3px 8px;
  border-radius: 999px;
  font-size: 11.5px;
  font-weight: 600;
}
.tag.domain {
  background: #dbeafe;
  color: #1d4ed8;
}
.tag.local {
  background: #f1f5f9;
  color: #475569;
}

.primary {
  position: relative;
  display: flex;
  gap: 10px;
  align-items: center;
  justify-content: center;
  width: 100%;
  height: 52px;
  overflow: hidden;
  border: 0;
  border-radius: 14px;
  background: linear-gradient(135deg, #1d4ed8, var(--blue-2) 55%, var(--cyan));
  color: #fff;
  font: inherit;
  font-size: 16.5px;
  font-weight: 650;
  letter-spacing: 2px;
  cursor: pointer;
  box-shadow: 0 14px 28px -12px rgba(37, 99, 235, 0.75);
  transition: transform 0.15s, box-shadow 0.2s, filter 0.2s;
}
.primary::after {
  content: '';
  position: absolute;
  inset: 0;
  background: linear-gradient(110deg, transparent 30%, rgba(255, 255, 255, 0.35) 50%, transparent 70%);
  transform: translateX(-120%);
}
.primary:hover:not(:disabled) {
  transform: translateY(-1px);
  filter: brightness(1.05);
  box-shadow: 0 18px 32px -12px rgba(37, 99, 235, 0.85);
}
.primary:hover:not(:disabled)::after {
  transform: translateX(120%);
  transition: transform 0.8s ease;
}
.primary:active:not(:disabled) {
  transform: translateY(0) scale(0.99);
}
.primary:disabled {
  cursor: default;
  filter: saturate(0.85);
}
.spinner {
  width: 18px;
  height: 18px;
  border: 2.5px solid rgba(255, 255, 255, 0.35);
  border-top-color: #fff;
  border-radius: 50%;
  animation: spin 0.8s linear infinite;
}

.field {
  position: relative;
  display: flex;
  align-items: center;
  height: 52px;
  border: 1px solid var(--line);
  border-radius: 14px;
  background: rgba(255, 255, 255, 0.85);
  transition: border-color 0.15s, box-shadow 0.15s;
}
.field:focus-within {
  border-color: var(--blue-2);
  box-shadow: 0 0 0 4px rgba(59, 130, 246, 0.16);
}
.field .icon {
  flex: none;
  margin: 0 10px 0 16px;
  color: var(--ink-3);
}
.field:focus-within .icon {
  color: var(--blue);
}
.field input {
  flex: 1;
  min-width: 0;
  height: 100%;
  border: 0;
  outline: none;
  background: transparent;
  color: var(--ink);
  font: inherit;
  font-size: 15px;
  user-select: text;
}
.field input::placeholder {
  color: var(--ink-3);
}
.eye {
  display: grid;
  place-items: center;
  width: 40px;
  height: 100%;
  border: 0;
  background: transparent;
  color: var(--ink-3);
  cursor: pointer;
}
.eye:hover {
  color: var(--ink-2);
}
.hint,
.intro {
  margin: -2px 4px 2px;
  color: var(--ink-3);
  font-size: 12px;
  line-height: 1.5;
}
.intro {
  margin: 0 4px 2px;
  font-size: 13px;
  text-align: center;
}
.secure {
  display: flex;
  gap: 5px;
  align-items: center;
  justify-content: center;
  margin: 0;
  color: #16a34a;
  font-size: 11.5px;
}

.error {
  display: flex;
  gap: 6px;
  align-items: center;
  width: 100%;
  margin: 12px 0 0;
  padding: 9px 12px;
  box-sizing: border-box;
  border-radius: 10px;
  background: #fef2f2;
  color: #dc2626;
  font-size: 12.5px;
  line-height: 1.45;
}

/* ---------- 协议勾选 ---------- */
.agree {
  position: relative;
  display: flex;
  gap: 8px;
  align-items: center;
  margin-top: 18px;
  color: var(--ink-2);
  font-size: 12.5px;
}
.agree a {
  color: var(--blue);
  text-decoration: none;
}
.agree a:hover {
  text-decoration: underline;
}
.check {
  display: grid;
  flex: none;
  place-items: center;
  width: 18px;
  height: 18px;
  padding: 0;
  border: 1.5px solid #cbd5e1;
  border-radius: 6px;
  background: #fff;
  cursor: pointer;
  transition: background 0.15s, border-color 0.15s;
}
.check svg {
  width: 12px;
  height: 12px;
  fill: none;
  stroke: #fff;
  stroke-width: 2.4;
  stroke-linecap: round;
  stroke-linejoin: round;
  stroke-dasharray: 16;
  stroke-dashoffset: 16;
  transition: stroke-dashoffset 0.25s ease;
}
.check.on {
  border-color: var(--blue);
  background: var(--blue);
}
.check.on svg {
  stroke-dashoffset: 0;
}
.agree.nudge .check {
  border-color: #f97316;
  animation: shake 0.42s;
}
@keyframes shake {
  20%,
  60% {
    transform: translateX(-4px);
  }
  40%,
  80% {
    transform: translateX(4px);
  }
}
.bubble {
  position: absolute;
  bottom: calc(100% + 10px);
  left: -6px;
  padding: 8px 12px;
  border-radius: 10px;
  background: #0f172a;
  color: #fff;
  font-size: 12px;
  white-space: nowrap;
  box-shadow: 0 10px 24px -10px rgba(15, 23, 42, 0.6);
}
.bubble::after {
  content: '';
  position: absolute;
  top: 100%;
  left: 12px;
  border: 6px solid transparent;
  border-top-color: #0f172a;
}

.links {
  margin-top: 18px;
}
.links a {
  display: inline-flex;
  gap: 4px;
  align-items: center;
  color: var(--blue);
  font-size: 14px;
  font-weight: 500;
  text-decoration: none;
}
.links a:hover {
  color: #1d4ed8;
}

.footer {
  position: relative;
  z-index: 1;
  display: flex;
  gap: 14px;
  align-items: center;
  justify-content: center;
  padding: 0 0 16px;
  color: var(--ink-3);
  font-size: 11.5px;
}
.footer a {
  color: var(--ink-2);
  text-decoration: none;
}
.footer a:hover {
  color: var(--blue);
}
.server {
  display: inline-flex;
  gap: 4px;
  align-items: center;
}

/* ---------- 协议全文 ---------- */
.sheet-mask {
  position: absolute;
  inset: 0;
  z-index: 10;
  display: flex;
  align-items: flex-end;
  background: rgba(15, 23, 42, 0.32);
  backdrop-filter: blur(2px);
}
.sheet {
  display: flex;
  flex-direction: column;
  width: 100%;
  height: 86%;
  border-radius: 22px 22px 0 0;
  background: #fff;
  box-shadow: 0 -18px 40px -20px rgba(15, 23, 42, 0.45);
}
.sheet header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 16px 16px 10px 24px;
  border-bottom: 1px solid #f1f5f9;
}
.sheet h2 {
  margin: 0;
  font-size: 17px;
}
.doc {
  flex: 1;
  overflow: auto;
  padding: 6px 24px 12px;
  color: var(--ink-2);
  font-size: 13.5px;
  line-height: 1.85;
  user-select: text;
}
.doc :deep(h1) {
  margin: 14px 0 6px;
  color: var(--ink);
  font-size: 18px;
  text-align: center;
}
.doc :deep(h2) {
  margin: 18px 0 6px;
  color: var(--ink);
  font-size: 14.5px;
}
.doc :deep(strong) {
  color: var(--ink);
}
.doc :deep(ol),
.doc :deep(ul) {
  padding-left: 20px;
}
.sheet footer {
  padding: 12px 24px 18px;
  border-top: 1px solid #f1f5f9;
}

/* ---------- 登录成功 ---------- */
.success {
  position: absolute;
  inset: 0;
  z-index: 20;
  display: flex;
  flex-direction: column;
  gap: 8px;
  align-items: center;
  justify-content: center;
  background: rgba(245, 248, 255, 0.92);
  backdrop-filter: blur(6px);
}
.tick {
  width: 76px;
  height: 76px;
  margin-bottom: 8px;
}
.tick circle {
  fill: none;
  stroke: #22c55e;
  stroke-width: 3.5;
  stroke-dasharray: 190;
  stroke-dashoffset: 190;
  animation: draw 0.6s ease forwards;
}
.tick path {
  fill: none;
  stroke: #22c55e;
  stroke-width: 4.5;
  stroke-linecap: round;
  stroke-linejoin: round;
  stroke-dasharray: 44;
  stroke-dashoffset: 44;
  animation: draw 0.4s 0.45s ease forwards;
}
@keyframes draw {
  to {
    stroke-dashoffset: 0;
  }
}
.success strong {
  font-size: 20px;
}
.success small {
  color: var(--ink-3);
  font-size: 13px;
}

/* ---------- 过渡 ---------- */
.swap-enter-active,
.swap-leave-active {
  transition: opacity 0.2s ease, transform 0.2s ease;
}
.swap-enter-from {
  opacity: 0;
  transform: translateX(14px);
}
.swap-leave-to {
  opacity: 0;
  transform: translateX(-14px);
}
.fade-enter-active,
.fade-leave-active {
  transition: opacity 0.2s ease;
}
.fade-enter-from,
.fade-leave-to {
  opacity: 0;
}
.sheet-enter-active,
.sheet-leave-active {
  transition: opacity 0.25s ease;
}
.sheet-enter-active .sheet,
.sheet-leave-active .sheet {
  transition: transform 0.3s cubic-bezier(0.2, 0.8, 0.2, 1);
}
.sheet-enter-from,
.sheet-leave-to {
  opacity: 0;
}
.sheet-enter-from .sheet,
.sheet-leave-to .sheet {
  transform: translateY(100%);
}
</style>
