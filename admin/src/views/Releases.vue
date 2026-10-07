<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { ElMessage, ElMessageBox, type UploadRawFile } from 'element-plus'
import { Refresh, Upload } from '@element-plus/icons-vue'
import { api } from '@/api'
import type { Release } from '@/api/types'
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
  } finally {
    loading.value = false
  }
}
onMounted(load)

// ---------- 上传 ----------
const dialog = ref(false)
const file = ref<UploadRawFile | null>(null)
const version = ref('')
const notes = ref('')
const progress = ref(-1)

function pick(f: UploadRawFile) {
  file.value = f
  // 文件名里一般就带着版本号，省得再手敲一遍
  const guess = /(\d+\.\d+(\.\d+)?([-+][\w.]+)?)/.exec(f.name)
  if (guess && !version.value) version.value = guess[1]
  return false
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

    <el-dialog v-model="dialog" title="上传新版本" width="560px" :close-on-click-modal="false">
      <el-alert type="info" :closable="false" show-icon class="notice"
                title="把 dotnet publish 出来的整个文件夹打成一个 zip 上传"
                description="里面要有 FlyknitBuddy.exe 和 FlyknitUpdater.exe。上传完不会立刻下发，确认无误后再点发布。" />
      <el-upload drag :before-upload="pick" :limit="1" :auto-upload="false" accept=".zip">
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
</style>
