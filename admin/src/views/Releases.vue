<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { ElMessage, ElMessageBox, type UploadFile, type UploadRawFile } from 'element-plus'
import { Download, Refresh, Upload } from '@element-plus/icons-vue'
import { api } from '@/api'
import type { EnrollmentTicket, Release } from '@/api/types'
import { auth } from '@/store/auth'
import { bytes, dateTime, relative } from '@/utils/format'

// 发布一个版本，全厂电脑会自己发现、下载、校验、装上。这是这里影响面最大的一页，
// 所以上传和发布分两步：传完先停住，确认无误再发。
const releases = ref<Release[]>([])
const loading = ref(false)

// 换掉的是员工电脑上正在跑的程序本身，比下发一条运维指令重得多
const canPublish = computed(() => !!auth.user?.is_owner && !auth.user?.must_change_password)
const current = computed(() => releases.value.find((r) => r.published))

async function load() {
  loading.value = true
  try {
    releases.value = await api.releases()
    if (canPublish.value) tickets.value = await api.tickets()
  } finally {
    loading.value = false
  }
}

// ---------- 员工端安装包（带服务器地址和安装凭证） ----------
// 员工解压运行后直接点「登录」：域账号一键，本机账号输入 Windows 密码。不用再告诉他们服务器地址和注册密钥
const tickets = ref<EnrollmentTicket[]>([])
const packageDialog = ref(false)
const packageForm = reactive({ server_url: '', label: '' })
const downloading = ref(false)

function openPackage() {
  // 管理后台和接口同源，这个地址员工电脑一般也能访问；开发时（vite 端口）或走了反向代理的要手动改
  packageForm.server_url = packageForm.server_url || window.location.origin
  packageForm.label = ''
  packageDialog.value = true
}
async function downloadPackage() {
  if (!/^https?:\/\/[^\s/]+/.test(packageForm.server_url.trim())) {
    ElMessage.warning('服务器地址要写成 http://10.0.0.5:8000 这样的完整地址')
    return
  }
  downloading.value = true
  try {
    await api.downloadClientPackage(packageForm.server_url.trim(), packageForm.label.trim())
    packageDialog.value = false
    ElMessage.success('已开始下载。解压后把文件夹发给员工，运行 FlyknitBuddy.exe 点「登录」即可')
    tickets.value = await api.tickets()
  } finally {
    downloading.value = false
  }
}
async function revoke(t: EnrollmentTicket) {
  await ElMessageBox.confirm(
    `停用「${t.label}」？用这个安装包新装的电脑将无法登录；已经登录的 ${t.uses} 台不受影响。`,
    '停用安装包',
    { type: 'warning', confirmButtonText: '停用' },
  )
  Object.assign(t, await api.revokeTicket(t.id))
}
onMounted(load)

// ---------- 上传 ----------
const dialog = ref(false)
const file = ref<UploadRawFile | null>(null)
const version = ref('')
const notes = ref('')
const progress = ref(-1)

// 不自动上传时 el-upload 不会调 before-upload，选中的文件要从 on-change 里拿
function pick(picked: UploadFile) {
  const f = picked.raw
  if (!f) return
  if (!f.name.toLowerCase().endsWith('.zip')) {
    ElMessage.warning('请选择 .zip 文件')
    return
  }
  file.value = f
  // 文件名里一般就带着版本号，省得再手敲一遍
  const guess = /(\d+\.\d+(\.\d+)?([-+][\w.]+)?)/.exec(f.name)
  if (guess && !version.value) version.value = guess[1]
}

function openUpload() {
  file.value = null
  version.value = ''
  notes.value = ''
  progress.value = -1
  dialog.value = true
}

async function send() {
  if (!file.value) return ElMessage.warning('请选择 zip 包')
  if (!version.value.trim()) return ElMessage.warning('请填版本号')
  progress.value = 0
  try {
    await api.uploadRelease(file.value as unknown as File, version.value.trim(), notes.value,
                            (p) => (progress.value = p))
    dialog.value = false
    ElMessage.success('已上传。确认无误后点「发布」才会下发到员工电脑')
    await load()
  } finally {
    progress.value = -1
  }
}

// ---------- 发布 ----------
async function publish(r: Release) {
  await ElMessageBox.confirm(
    `发布 ${r.version} 之后，所有员工电脑会在几小时内自己装上（员工也可以点「立即重启升级」）。` +
    '确认这个包是好的吗？',
    '发布新版本',
    { type: 'warning', confirmButtonText: '发布' },
  )
  await api.updateRelease(r.id, { published: true })
  ElMessage.success(`${r.version} 已发布`)
  await load()
}

async function unpublish(r: Release) {
  await ElMessageBox.confirm(
    `停止下发 ${r.version}。已经装上的电脑不会退回去，只是还没装的不再收到。`,
    '停止下发', { type: 'warning', confirmButtonText: '停止下发' },
  )
  await api.updateRelease(r.id, { published: false })
  await load()
}

async function remove(r: Release) {
  await ElMessageBox.confirm(`删除 ${r.version}？安装包文件会一起删掉。`, '删除版本',
                             { type: 'warning', confirmButtonText: '删除' })
  await api.deleteRelease(r.id)
  await load()
}

</script>

<template>
  <div class="page">
    <el-alert
      v-if="!canPublish"
      type="info"
      :closable="false"
      show-icon
      title="只有超级管理员能发布新版本"
      description="发布之后全厂电脑会自己装上，所以这一页只能看。"
      class="notice"
    />

    <div class="page-head">
      <div>
        <h1>员工端版本</h1>
        <p>发布之后，员工电脑会自己发现并提示「新版本就绪」；不点的话，退出或下次开机时自动装上。</p>
      </div>
      <div class="toolbar">
        <el-button :icon="Refresh" :loading="loading" @click="load" />
        <el-button :icon="Download" :disabled="!canPublish || !current" @click="openPackage">下载员工端安装包</el-button>
        <el-button type="primary" :icon="Upload" :disabled="!canPublish" @click="openUpload">上传新版本</el-button>
      </div>
    </div>

    <el-alert v-if="current" type="success" :closable="false" show-icon class="notice"
              :title="`当前下发中：${current.version}`"
              :description="`${bytes(current.size)} · ${dateTime(current.created_at)} 由 ${current.uploaded_by} 上传`" />

    <div class="panel">
      <el-table :data="releases" v-loading="loading" empty-text="还没有上传过版本">
        <el-table-column label="版本" width="140">
          <template #default="{ row }">
            <strong>{{ row.version }}</strong>
          </template>
        </el-table-column>
        <el-table-column label="状态" width="110">
          <template #default="{ row }">
            <el-tag v-if="row.published" type="success" size="small">下发中</el-tag>
            <el-tag v-else type="info" size="small">未发布</el-tag>
          </template>
        </el-table-column>
        <el-table-column label="更新日志" min-width="280">
          <template #default="{ row }"><span class="notes">{{ row.notes || '—' }}</span></template>
        </el-table-column>
        <el-table-column label="大小" width="100">
          <template #default="{ row }">{{ bytes(row.size) }}</template>
        </el-table-column>
        <el-table-column label="上传" width="150">
          <template #default="{ row }">
            <el-tooltip :content="dateTime(row.created_at)" placement="top">
              <span>{{ relative(row.created_at) }}<small> · {{ row.uploaded_by }}</small></span>
            </el-tooltip>
          </template>
        </el-table-column>
        <el-table-column width="200" align="right">
          <template #default="{ row }">
            <el-button v-if="!row.published" link type="primary" :disabled="!canPublish" @click="publish(row as Release)">发布</el-button>
            <el-button v-else link type="warning" :disabled="!canPublish" @click="unpublish(row as Release)">停止下发</el-button>
            <el-button link type="danger" :disabled="!canPublish || row.published" @click="remove(row as Release)">删除</el-button>
          </template>
        </el-table-column>
      </el-table>
    </div>

    <div v-if="canPublish && tickets.length" class="panel tickets">
      <div class="panel-title">
        <h3>已下载的安装包<small>每下载一次生成一张安装凭证。包外泄了就停用那一张：已登录的电脑不受影响，用它新装的登录不上</small></h3>
      </div>
      <el-table :data="tickets">
        <el-table-column label="名称" min-width="160">
          <template #default="{ row }"><strong>{{ row.label }}</strong></template>
        </el-table-column>
        <el-table-column label="服务器地址" min-width="180" prop="server_url" />
        <el-table-column label="已登录" width="90" align="right">
          <template #default="{ row }">{{ row.uses }} 台</template>
        </el-table-column>
        <el-table-column label="最近一次" width="120">
          <template #default="{ row }">{{ row.last_used_at ? relative(row.last_used_at) : '—' }}</template>
        </el-table-column>
        <el-table-column label="下载" width="150">
          <template #default="{ row }">
            <el-tooltip :content="dateTime(row.created_at)" placement="top">
              <span>{{ relative(row.created_at) }}<small> · {{ row.created_by }}</small></span>
            </el-tooltip>
          </template>
        </el-table-column>
        <el-table-column width="110" align="right">
          <template #default="{ row }">
            <el-tag v-if="row.revoked" type="info" size="small">已停用</el-tag>
            <el-button v-else link type="danger" @click="revoke(row as EnrollmentTicket)">停用</el-button>
          </template>
        </el-table-column>
      </el-table>
    </div>

    <el-dialog v-model="packageDialog" title="下载员工端安装包" width="560px">
      <el-alert type="info" :closable="false" show-icon class="notice"
                :title="`打包当前下发中的版本 ${current?.version ?? ''}，带上服务器地址和一张安装凭证`"
                description="员工解压后运行 FlyknitBuddy.exe，直接点「登录」：加了域的电脑一键登录，没加域的输入这台电脑的 Windows 密码。不用再告诉员工服务器地址和注册密钥。" />
      <el-form label-position="top">
        <el-form-item label="员工电脑访问服务器的地址">
          <el-input v-model="packageForm.server_url" placeholder="http://10.0.0.5:8000" />
          <p class="hint">默认是你现在打开后台用的地址。员工电脑要能访问到它；走了反向代理或域名的请改成员工那边用的地址。</p>
        </el-form-item>
        <el-form-item label="名称（可选）">
          <el-input v-model="packageForm.label" maxlength="200" placeholder="比如：三车间、财务部、2026 年 10 月" />
          <p class="hint">用来区分不同批次的安装包，下面列表里能看到每个包登录了几台电脑。</p>
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="packageDialog = false">取消</el-button>
        <el-button type="primary" :loading="downloading" @click="downloadPackage">下载</el-button>
      </template>
    </el-dialog>

    <el-dialog v-model="dialog" title="上传新版本" width="560px" :close-on-click-modal="false">
      <el-alert type="info" :closable="false" show-icon class="notice"
                title="把 dotnet publish 出来的整个文件夹打成一个 zip 上传"
                description="里面要有 FlyknitBuddy.exe 和 FlyknitUpdater.exe。上传完不会立刻下发，确认无误后再点发布。" />
      <el-upload drag :auto-upload="false" :show-file-list="false" accept=".zip" :on-change="pick">
        <div class="drop">
          <el-icon :size="26"><Upload /></el-icon>
          <p>{{ file ? file.name : '把 zip 拖到这里，或点击选择' }}</p>
          <small v-if="file">{{ bytes(file.size) }}</small>
        </div>
      </el-upload>
      <el-form label-position="top" class="form">
        <el-form-item label="版本号">
          <el-input v-model="version" placeholder="0.3.0" />
        </el-form-item>
        <el-form-item label="更新日志">
          <el-input v-model="notes" type="textarea" :rows="5"
                    placeholder="员工点「更新日志」看到的就是这段，用他们看得懂的话写&#10;· 语音输入支持越南语&#10;· 修复了大表格偶尔卡住的问题" />
        </el-form-item>
      </el-form>
      <el-progress v-if="progress >= 0" :percentage="progress" :stroke-width="14" />
      <template #footer>
        <el-button :disabled="progress >= 0" @click="dialog = false">取消</el-button>
        <el-button type="primary" :loading="progress >= 0" @click="send">上传</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<style scoped>
.notice {
  margin-bottom: 16px;
}
.notes {
  display: -webkit-box;
  overflow: hidden;
  color: var(--el-text-color-regular);
  font-size: 13px;
  -webkit-box-orient: vertical;
  -webkit-line-clamp: 2;
  white-space: pre-wrap;
}
.drop {
  padding: 22px 10px;
  color: var(--el-text-color-secondary);
}
.drop p {
  margin: 8px 0 2px;
}
.form {
  margin-top: 16px;
}
.tickets {
  margin-top: 16px;
}
.tickets h3 small {
  margin-left: 10px;
  color: var(--el-text-color-secondary);
  font-size: 12px;
  font-weight: normal;
}
</style>
