<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { ElMessage, ElMessageBox, type FormInstance, type FormRules } from 'element-plus'
import { CopyDocument, Key, Plus, Refresh } from '@element-plus/icons-vue'
import { api } from '@/api'
import type { AdminUser } from '@/api/types'
import { auth } from '@/store/auth'
import { dateTime, randomPassword, relative } from '@/utils/format'

const users = ref<AdminUser[]>([])
const loading = ref(false)

async function load() {
  loading.value = true
  try {
    users.value = await api.users()
  } finally {
    loading.value = false
  }
}
onMounted(load)

// 比 id 不比用户名：用户名存在本地，是客户端说了算的东西
const isSelf = (u: AdminUser) => (auth.user ? u.id === auth.user.id : u.username === auth.username)

// 建号、改权限都归超级管理员。服务端会拒，这里置灰只是别让人白点一下
// 超级管理员本来就能给任何人开查看聊天、下发运维，给自己开也一样——只挡停用自己和撤销自己的超级管理员（会把后台锁在门外）
const canManage = computed(() => !!auth.user?.is_owner && !auth.user?.must_change_password)
// 最后一个超级管理员不能摘——摘了就没人管得了账号和安全策略
const owners = computed(() => users.value.filter((u) => u.is_owner && !u.disabled).length)
const lastOwner = (u: AdminUser) => u.is_owner && !u.disabled && owners.value <= 1

// ---------- 新建 ----------
const createDialog = ref(false)
const formRef = ref<FormInstance>()
const form = reactive({ username: '', display_name: '', can_read_chats: false, can_dispatch: false, is_owner: false })
const rules: FormRules = {
  username: [
    { required: true, message: '请输入登录名', trigger: 'blur' },
    { min: 3, max: 64, message: '3 到 64 个字符', trigger: 'blur' },
  ],
}
function openCreate() {
  Object.assign(form, { username: '', display_name: '', can_read_chats: false, can_dispatch: false, is_owner: false })
  createDialog.value = true
}
async function create() {
  if (!(await formRef.value?.validate().catch(() => false))) return
  const password = randomPassword()
  await api.createUser({ ...form, username: form.username.trim(), password })
  createDialog.value = false
  showPassword(form.username.trim(), password)
  await load()
}

// ---------- 初始密码只显示一次 ----------
const passwordDialog = ref(false)
const issued = reactive({ username: '', password: '' })
function showPassword(username: string, password: string) {
  Object.assign(issued, { username, password })
  passwordDialog.value = true
}
async function copyPassword() {
  await navigator.clipboard.writeText(`登录名：${issued.username}\n初始密码：${issued.password}`)
  ElMessage.success('已复制')
}

async function patch(u: AdminUser, body: Parameters<typeof api.updateUser>[1], revert: () => void) {
  try {
    Object.assign(u, await api.updateUser(u.id, body))
  } catch {
    revert()
  }
}
function toggleChats(u: AdminUser) {
  void patch(u, { can_read_chats: u.can_read_chats }, () => (u.can_read_chats = !u.can_read_chats))
}
function toggleDispatch(u: AdminUser) {
  void patch(u, { can_dispatch: u.can_dispatch }, () => (u.can_dispatch = !u.can_dispatch))
}
async function toggleOwner(u: AdminUser) {
  const undo = () => (u.is_owner = !u.is_owner)
  // 往上提是单向门：这个人从此能改所有人的权限和全厂的安全策略，先说清楚
  if (u.is_owner) {
    const ok = await ElMessageBox.confirm(
      `把「${u.display_name || u.username}」设为超级管理员？他将能建号、改所有人的权限、改全厂和单机的安全策略。`,
      '提升为超级管理员',
      { type: 'warning', confirmButtonText: '确定' },
    ).then(() => true, () => false)
    if (!ok) {
      undo()
      return
    }
  }
  await patch(u, { is_owner: u.is_owner }, undo)
}
async function toggleDisabled(u: AdminUser) {
  const next = !u.disabled
  if (next) {
    await ElMessageBox.confirm(`停用「${u.username}」？对方会被立即踢下线。`, '停用账号', { type: 'warning', confirmButtonText: '停用' })
  }
  await patch(u, { disabled: next }, () => {})
}
async function resetPassword(u: AdminUser) {
  await ElMessageBox.confirm(`为「${u.username}」生成新的初始密码？对方会被踢下线，下次登录必须修改密码。`, '重置密码', {
    type: 'warning',
    confirmButtonText: '重置',
  })
  const password = randomPassword()
  await api.updateUser(u.id, { password })
  showPassword(u.username, password)
  await load()
}
</script>

<template>
  <div class="page">
    <el-alert
      v-if="!canManage"
      type="info"
      :closable="false"
      show-icon
      title="只有超级管理员能改账号权限"
      description="你可以看到这里有哪些账号、各自有什么权限，但改不了。需要调整请找超级管理员。"
      class="notice"
    />
    <div class="page-head">
      <div>
        <h1>管理员账号</h1>
        <p>每个 IT 人员用自己的账号登录，操作可追溯。查看聊天记录、下发运维任务都是额外授予的权限。</p>
      </div>
      <div class="toolbar">
        <el-button :icon="Refresh" :loading="loading" @click="load" />
        <el-button type="primary" :icon="Plus" :disabled="!canManage" @click="openCreate">新建账号</el-button>
      </div>
    </div>

    <div class="panel">
      <el-table :data="users" v-loading="loading" empty-text="还没有管理员账号">
        <el-table-column label="账号" min-width="180">
          <template #default="{ row }">
            <div class="who">
              <el-avatar :size="32" class="avatar">{{ (row.display_name || row.username).slice(0, 1).toUpperCase() }}</el-avatar>
              <div>
                <strong>{{ row.display_name || row.username }}<el-tag v-if="isSelf(row as AdminUser)" size="small" effect="plain" class="me">我</el-tag></strong>
                <small>{{ row.username }}</small>
              </div>
            </div>
          </template>
        </el-table-column>
        <el-table-column label="状态" width="130">
          <template #default="{ row }">
            <el-tag v-if="row.disabled" type="danger" size="small">已停用</el-tag>
            <el-tag v-else-if="row.must_change_password" type="warning" size="small">待修改初始密码</el-tag>
            <el-tag v-else type="success" size="small">正常</el-tag>
          </template>
        </el-table-column>
        <el-table-column label="最近登录" width="140">
          <template #default="{ row }">
            <el-tooltip :content="dateTime(row.last_login)" placement="top" :disabled="!row.last_login">
              <span>{{ relative(row.last_login) }}</span>
            </el-tooltip>
          </template>
        </el-table-column>
        <el-table-column label="创建时间" width="150">
          <template #default="{ row }">{{ dateTime(row.created_at) }}</template>
        </el-table-column>
        <el-table-column label="可查看聊天记录" width="130" align="center">
          <template #default="{ row }">
            <el-switch v-model="row.can_read_chats" :disabled="!canManage" @change="toggleChats(row as AdminUser)" />
          </template>
        </el-table-column>
        <el-table-column label="可下发运维任务" width="130" align="center">
          <template #default="{ row }">
            <el-switch v-model="row.can_dispatch" :disabled="!canManage" @change="toggleDispatch(row as AdminUser)" />
          </template>
        </el-table-column>
        <el-table-column label="超级管理员" width="120" align="center">
          <template #default="{ row }">
            <el-tooltip content="最后一个超级管理员不能撤销" placement="top" :disabled="!lastOwner(row as AdminUser)">
              <span>
                <el-switch
                  v-model="row.is_owner"
                  :disabled="!canManage || isSelf(row as AdminUser) || lastOwner(row as AdminUser)"
                  @change="toggleOwner(row as AdminUser)"
                />
              </span>
            </el-tooltip>
          </template>
        </el-table-column>
        <el-table-column label="启用" width="80" align="center">
          <template #default="{ row }">
            <el-switch
              :model-value="!row.disabled"
              :disabled="!canManage || isSelf(row as AdminUser) || lastOwner(row as AdminUser)"
              @update:model-value="toggleDisabled(row as AdminUser)"
            />
          </template>
        </el-table-column>
        <el-table-column width="110" align="right">
          <template #default="{ row }">
            <el-button link type="primary" :icon="Key" :disabled="!canManage || isSelf(row as AdminUser)" @click="resetPassword(row as AdminUser)">重置密码</el-button>
          </template>
        </el-table-column>
      </el-table>
    </div>

    <el-dialog v-model="createDialog" title="新建管理员账号" width="440px">
      <el-form ref="formRef" :model="form" :rules="rules" label-position="top">
        <el-form-item label="登录名" prop="username"><el-input v-model="form.username" placeholder="如 zhangsan" /></el-form-item>
        <el-form-item label="姓名"><el-input v-model="form.display_name" placeholder="显示在顶栏和操作记录里" /></el-form-item>
        <el-form-item><el-checkbox v-model="form.can_read_chats">允许查看员工聊天记录</el-checkbox></el-form-item>
        <el-form-item><el-checkbox v-model="form.can_dispatch">允许下发运维任务（清理、安装、修复、重启等）</el-checkbox></el-form-item>
        <el-form-item>
          <el-checkbox v-model="form.is_owner">超级管理员</el-checkbox>
          <span class="sub">能建号、改所有人的权限、改全厂和单机的安全策略</span>
        </el-form-item>
      </el-form>
      <p class="hint">系统会生成一个随机初始密码，对方首次登录时必须修改。</p>
      <template #footer>
        <el-button @click="createDialog = false">取消</el-button>
        <el-button type="primary" @click="create">创建</el-button>
      </template>
    </el-dialog>

    <el-dialog v-model="passwordDialog" title="初始密码" width="420px" :close-on-click-modal="false">
      <el-alert type="warning" :closable="false" show-icon title="只显示这一次，请当面或通过安全渠道交给对方" />
      <div class="issued">
        <div><label>登录名</label><code>{{ issued.username }}</code></div>
        <div><label>初始密码</label><code>{{ issued.password }}</code></div>
      </div>
      <template #footer>
        <el-button :icon="CopyDocument" @click="copyPassword">复制</el-button>
        <el-button type="primary" @click="passwordDialog = false">我已记下</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<style scoped>
.notice {
  margin-bottom: 16px;
}
.sub {
  margin-left: 8px;
  color: var(--el-text-color-secondary);
  font-size: 12px;
}
.who {
  display: flex;
  gap: 10px;
  align-items: center;
}
.who > div {
  display: flex;
  flex-direction: column;
  line-height: 1.35;
}
.who strong {
  font-weight: 500;
}
.who small {
  color: var(--ink-faint);
  font-size: 12px;
}
.avatar {
  background: var(--indigo-wash);
  color: var(--indigo);
  font-weight: 600;
}
.me {
  margin-left: 6px;
}
.issued {
  display: flex;
  flex-direction: column;
  gap: 10px;
  margin-top: 16px;
}
.issued > div {
  display: flex;
  gap: 12px;
  align-items: center;
  padding: 10px 14px;
  border-radius: var(--r-md);
  background: var(--cloth-sunk);
}
.issued label {
  width: 64px;
  color: var(--ink-faint);
  font-size: 12.5px;
}
.issued code {
  font-size: 15px;
  user-select: all;
}
</style>
