<script setup lang="ts">
import { onMounted, reactive, ref } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { ChatDotSquare, Refresh } from '@element-plus/icons-vue'
import { api } from '@/api'
import type { ChannelBinding, ChannelMessageRow, DingTalkConfig, WeComConfig } from '@/api/types'
import { dateTime, relative } from '@/utils/format'

const cfg = ref<DingTalkConfig | null>(null)
const bindings = ref<ChannelBinding[]>([])
const messages = ref<ChannelMessageRow[]>([])
const loading = ref(false)
const form = reactive({ enabled: false, client_id: '', client_secret: '', robot_code: '' })
const wc = ref<WeComConfig | null>(null)
const wcForm = reactive({ enabled: false, bot_id: '', secret: '' })
const tab = ref<'dingtalk' | 'wecom'>('dingtalk')
const CHANNEL_LABELS: Record<string, string> = { dingtalk: '钉钉', wecom: '企业微信' }

async function load() {
  loading.value = true
  try {
    const [c, w, b, m] = await Promise.all([api.dingtalk(), api.wecom(), api.channelBindings(), api.channelMessages()])
    cfg.value = c
    wc.value = w
    bindings.value = b
    messages.value = m
    Object.assign(form, { enabled: c.enabled, client_id: c.client_id, client_secret: '', robot_code: c.robot_code })
    Object.assign(wcForm, { enabled: w.enabled, bot_id: w.bot_id, secret: '' })
  } finally {
    loading.value = false
  }
}
onMounted(load)

const saving = ref(false)
async function save() {
  if (form.enabled && !cfg.value?.enabled) {
    try {
      await ElMessageBox.confirm(
        '开启后，绑定了电脑的员工可以在钉钉里给机器人发消息，让自己电脑上的 FlyknitBuddy 执行操作（读写文件、运行命令等，仍受安全中心策略约束；需要确认的操作不会远程执行）。确定开启吗？',
        '开启钉钉通道',
        { type: 'warning', confirmButtonText: '开启', cancelButtonText: '取消' },
      )
    } catch {
      return
    }
  }
  saving.value = true
  try {
    cfg.value = await api.saveDingtalk({ ...form })
    form.client_secret = ''
    ElMessage.success(form.enabled ? '已保存，正在连接钉钉…' : '已保存，钉钉通道已关闭')
    setTimeout(() => void refreshStatus(), 2500)
  } finally {
    saving.value = false
  }
}

async function refreshStatus() {
  cfg.value = await api.dingtalk()
  wc.value = await api.wecom()
}

const savingWc = ref(false)
async function saveWecom() {
  if (wcForm.enabled && !wc.value?.enabled) {
    try {
      await ElMessageBox.confirm(
        '开启后，绑定了电脑的员工可以在企业微信里私聊智能机器人，让自己电脑上的 FlyknitBuddy 执行操作（读写文件、运行命令等，仍受安全中心策略约束；需要确认的操作不会远程执行）。确定开启吗？',
        '开启企业微信通道',
        { type: 'warning', confirmButtonText: '开启', cancelButtonText: '取消' },
      )
    } catch {
      return
    }
  }
  savingWc.value = true
  try {
    wc.value = await api.saveWecom({ ...wcForm })
    wcForm.secret = ''
    ElMessage.success(wcForm.enabled ? '已保存，正在连接企业微信…' : '已保存，企业微信通道已关闭')
    setTimeout(() => void refreshStatus(), 2500)
  } finally {
    savingWc.value = false
  }
}

async function unbind(b: ChannelBinding) {
  try {
    await ElMessageBox.confirm(`解除${CHANNEL_LABELS[b.channel] ?? ''}「${b.external_name || '用户'}」和电脑 ${b.machine_name} 的绑定？`, '解除绑定', { type: 'warning' })
  } catch {
    return
  }
  await api.removeChannelBinding(b.id)
  ElMessage.success('已解除')
  bindings.value = await api.channelBindings()
}

const STATUS: Record<ChannelMessageRow['status'], { label: string; type: 'info' | 'warning' | 'success' | 'danger' | 'primary' }> = {
  pending: { label: '等电脑上线', type: 'warning' },
  running: { label: '执行中', type: 'primary' },
  succeeded: { label: '完成', type: 'success' },
  failed: { label: '失败', type: 'danger' },
  expired: { label: '已过期', type: 'info' },
}
</script>

<template>
  <div class="page" v-loading="loading && !cfg">
    <div class="page-head">
      <div>
        <h1>消息通道</h1>
        <p>员工在钉钉或企业微信里私聊机器人，消息转给他自己电脑上的 FlyknitBuddy 去办，结果再发回去。两边都是服务器主动连出去的长连接，不需要公网地址。</p>
      </div>
      <el-button :icon="Refresh" @click="load">刷新</el-button>
    </div>

    <el-tabs v-model="tab" class="tabs">
      <el-tab-pane name="dingtalk">
        <template #label>钉钉 <el-tag v-if="cfg?.enabled" size="small" :type="cfg.connected ? 'success' : 'danger'" effect="plain">{{ cfg.connected ? '已连接' : '未连上' }}</el-tag></template>
    <div class="layout">
      <section class="panel">
        <div class="panel-title">
          <h3><el-icon><ChatDotSquare /></el-icon> 钉钉应用</h3>
          <template v-if="cfg">
            <el-tag v-if="!cfg.enabled" type="info" effect="plain">未开启</el-tag>
            <el-tag v-else-if="cfg.connected" type="success" effect="plain">已连接</el-tag>
            <el-tag v-else type="danger" effect="plain">未连上</el-tag>
          </template>
        </div>
        <el-form :model="form" label-width="120px">
          <el-form-item label="开启">
            <el-switch v-model="form.enabled" />
          </el-form-item>
          <el-form-item label="Client ID">
            <el-input v-model="form.client_id" placeholder="钉钉应用的 AppKey，例如 dingxxxxxxxx" />
            <p class="hint">请用公司钉钉组织下建的应用。换成另一个应用后，原来的绑定会全部清空，员工需要重新绑定。</p>
          </el-form-item>
          <el-form-item label="Client Secret">
            <el-input
              v-model="form.client_secret"
              type="password"
              show-password
              :placeholder="cfg?.client_secret ? `已保存（${cfg.client_secret}），留空表示不改` : '钉钉应用的 AppSecret'"
            />
          </el-form-item>
          <el-form-item label="机器人编码">
            <el-input v-model="form.robot_code" placeholder="可选，留空则使用 Client ID" />
            <p class="hint">企业内部应用的机器人，robotCode 一般就是 AppKey。</p>
          </el-form-item>
          <el-form-item>
            <el-button type="primary" :loading="saving" @click="save">保存</el-button>
          </el-form-item>
        </el-form>
        <el-alert v-if="cfg?.enabled && cfg.last_error" type="error" :closable="false" show-icon :title="`连接失败：${cfg.last_error}`" />
        <p v-if="cfg?.connected" class="hint">
          {{ relative(cfg.connected_since) }}连上；累计收到 {{ cfg.received }} 条消息<template v-if="cfg.last_message_at">，最近一条 {{ relative(cfg.last_message_at) }}</template>。
        </p>
      </section>

      <section class="panel guide">
        <div class="panel-title"><h3>接入步骤</h3></div>
        <ol>
          <li>登录 <code>open-dev.dingtalk.com</code>，创建「企业内部应用」，记下 Client ID 和 Client Secret。</li>
          <li>在应用里添加「机器人」能力，消息接收模式选 <b>Stream 模式</b>，然后发布机器人。</li>
          <li>权限管理里开通「企业内机器人发送消息」（<code>qyapi_robot_sendmsg</code>），用于在任务较长时把结果单聊发回。</li>
          <li>把凭证填到左边并开启，状态显示「已连接」即可。</li>
          <li>员工在电脑上的 FlyknitBuddy「设置 → 钉钉」生成绑定码，在钉钉里私聊机器人发「绑定 123456」。</li>
        </ol>
        <p class="hint">只接单聊；群里 @ 机器人会提示改为私聊。需要员工在电脑上确认的操作（删除、安装软件等）不会远程执行。</p>
      </section>
    </div>
      </el-tab-pane>

      <el-tab-pane name="wecom">
        <template #label>企业微信 <el-tag v-if="wc?.enabled" size="small" :type="wc.connected ? 'success' : 'danger'" effect="plain">{{ wc.connected ? '已连接' : '未连上' }}</el-tag></template>
        <div class="layout">
          <section class="panel">
            <div class="panel-title">
              <h3><el-icon><ChatDotSquare /></el-icon> 企业微信智能机器人</h3>
              <template v-if="wc">
                <el-tag v-if="!wc.enabled" type="info" effect="plain">未开启</el-tag>
                <el-tag v-else-if="wc.connected" type="success" effect="plain">已连接</el-tag>
                <el-tag v-else type="danger" effect="plain">未连上</el-tag>
              </template>
            </div>
            <el-form :model="wcForm" label-width="120px">
              <el-form-item label="开启">
                <el-switch v-model="wcForm.enabled" />
              </el-form-item>
              <el-form-item label="Bot ID">
                <el-input v-model="wcForm.bot_id" placeholder="智能机器人的 Bot ID" />
                <p class="hint">请用公司企业微信里建的机器人。换成另一个机器人后，原来的绑定会全部清空，员工需要重新绑定。</p>
              </el-form-item>
              <el-form-item label="Secret">
                <el-input
                  v-model="wcForm.secret"
                  type="password"
                  show-password
                  :placeholder="wc?.secret ? `已保存（${wc.secret}），留空表示不改` : '长连接模式的 Secret'"
                />
              </el-form-item>
              <el-form-item>
                <el-button type="primary" :loading="savingWc" @click="saveWecom">保存</el-button>
              </el-form-item>
            </el-form>
            <el-alert v-if="wc?.enabled && wc.last_error" type="error" :closable="false" show-icon :title="`连接失败：${wc.last_error}`" />
            <p v-if="wc?.connected" class="hint">
              {{ relative(wc.connected_since) }}连上；累计收到 {{ wc.received }} 条消息<template v-if="wc.last_message_at">，最近一条 {{ relative(wc.last_message_at) }}</template>。
            </p>
          </section>

          <section class="panel guide">
            <div class="panel-title"><h3>接入步骤</h3></div>
            <ol>
              <li>企业微信管理后台 →「应用管理」→「智能机器人」，创建一个机器人。</li>
              <li>在机器人的「API 模式」里，连接方式选 <b>使用长连接</b>，记下 Bot ID 和 Secret。</li>
              <li>设置机器人的可见范围，把要用的员工加进去。</li>
              <li>把 Bot ID 和 Secret 填到左边并开启，状态显示「已连接」即可。服务器不需要公网地址，也不用配置可信 IP。</li>
              <li>员工在电脑上的 FlyknitBuddy「设置 → 钉钉 / 企业微信」生成绑定码，在企业微信里私聊机器人发「绑定 123456」。</li>
            </ol>
            <p class="hint">只接单聊；在群里 @ 机器人会提示改为私聊。企业微信长连接里只有员工的账号（userid），所以绑定列表里显示的是账号而不是姓名。</p>
          </section>
        </div>
      </el-tab-pane>
    </el-tabs>

    <section class="panel">
      <div class="panel-title"><h3>已绑定 <small>{{ bindings.length }} 人</small></h3></div>
      <el-table :data="bindings" empty-text="还没有员工绑定">
        <el-table-column label="通道" width="90">
          <template #default="{ row }">{{ CHANNEL_LABELS[row.channel] ?? row.channel }}</template>
        </el-table-column>
        <el-table-column label="账号" prop="external_name" min-width="120" />
        <el-table-column label="电脑" min-width="160">
          <template #default="{ row }">{{ row.machine_name }} <small class="muted">{{ row.user_name }}</small></template>
        </el-table-column>
        <el-table-column label="绑定时间" min-width="150">
          <template #default="{ row }">{{ dateTime(row.created_at) }}</template>
        </el-table-column>
        <el-table-column label="最近使用" min-width="120">
          <template #default="{ row }">{{ row.last_used ? relative(row.last_used) : '—' }}</template>
        </el-table-column>
        <el-table-column width="90" align="right">
          <template #default="{ row }"><el-button link type="danger" @click="unbind(row as ChannelBinding)">解绑</el-button></template>
        </el-table-column>
      </el-table>
    </section>

    <section class="panel">
      <div class="panel-title"><h3>消息记录 <small>最近 100 条</small></h3></div>
      <el-table :data="messages" empty-text="还没有消息">
        <el-table-column type="expand">
          <template #default="{ row }">
            <div class="detail">
              <p><b>消息：</b>{{ row.text }}</p>
              <p v-if="row.answer"><b>回复：</b>{{ row.answer }}</p>
              <p v-if="row.error" class="err"><b>原因：</b>{{ row.error }}</p>
            </div>
          </template>
        </el-table-column>
        <el-table-column label="时间" min-width="150">
          <template #default="{ row }">{{ dateTime(row.created_at) }}</template>
        </el-table-column>
        <el-table-column label="通道" width="90">
          <template #default="{ row }">{{ CHANNEL_LABELS[row.channel] ?? row.channel }}</template>
        </el-table-column>
        <el-table-column label="账号" prop="external_name" min-width="110" />
        <el-table-column label="电脑" prop="machine_name" min-width="120" />
        <el-table-column label="消息" min-width="260" show-overflow-tooltip prop="text" />
        <el-table-column label="状态" width="120">
          <template #default="{ row }">
            <el-tag :type="STATUS[row.status as ChannelMessageRow['status']].type" effect="plain" size="small">
              {{ STATUS[row.status as ChannelMessageRow['status']].label }}
            </el-tag>
          </template>
        </el-table-column>
        <el-table-column label="已回复" width="80">
          <template #default="{ row }">{{ row.replied ? '是' : '—' }}</template>
        </el-table-column>
      </el-table>
    </section>
  </div>
</template>

<style scoped>
.page {
  display: flex;
  flex-direction: column;
  gap: 16px;
}
.layout {
  display: grid;
  grid-template-columns: minmax(0, 1.2fr) minmax(0, 1fr);
  gap: 16px;
  align-items: start;
}
@media (max-width: 1100px) {
  .layout {
    grid-template-columns: 1fr;
  }
}
.panel-title h3 {
  display: flex;
  gap: 6px;
  align-items: center;
}
.hint {
  margin-top: 4px;
}
.guide ol {
  margin: 0;
  padding-left: 20px;
  line-height: 1.8;
}
.muted {
  color: var(--ink-soft);
}
.detail {
  padding: 4px 16px;
  white-space: pre-wrap;
  line-height: 1.7;
}
.detail .err {
  color: var(--red);
}
</style>
