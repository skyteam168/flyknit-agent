<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import type { UploadRequestOptions } from 'element-plus'
import { Delete, Link, Refresh, Search, UploadFilled } from '@element-plus/icons-vue'
import { api } from '@/api'
import type { Skill } from '@/api/types'
import { bytes, dateTime } from '@/utils/format'

const skills = ref<Skill[]>([])
const loading = ref(false)
const keyword = ref('')

async function load() {
  loading.value = true
  try {
    skills.value = await api.skills()
  } finally {
    loading.value = false
  }
}
onMounted(load)

const filtered = computed(() => {
  const q = keyword.value.trim().toLowerCase()
  return q ? skills.value.filter((s) => `${s.name} ${s.description} ${s.author}`.toLowerCase().includes(q)) : skills.value
})
const requiredCount = computed(() => skills.value.filter((s) => s.required && s.enabled).length)

// ---------- 导入 ----------
const importDialog = ref(false)
const importMode = ref<'url' | 'file'>('url')
const importUrl = ref('')
const importRequired = ref(false)
const importing = ref(false)

function openImport() {
  importUrl.value = ''
  importRequired.value = false
  importMode.value = 'url'
  importDialog.value = true
}
function reportImported(names: string[]) {
  ElMessage.success(names.length ? `已导入 ${names.length} 个技能：${names.join('、')}` : '没有找到可导入的技能')
}
async function importFromUrl() {
  if (!importUrl.value.trim()) {
    ElMessage.error('请填写链接')
    return
  }
  importing.value = true
  try {
    const r = await api.importSkill(importUrl.value.trim(), importRequired.value)
    reportImported(r.imported)
    importDialog.value = false
    await load()
  } finally {
    importing.value = false
  }
}
async function upload(opts: UploadRequestOptions) {
  importing.value = true
  try {
    const r = await api.uploadSkill(opts.file, importRequired.value)
    reportImported(r.imported)
    importDialog.value = false
    await load()
  } finally {
    importing.value = false
  }
}

async function patch(s: Skill, key: 'required' | 'enabled') {
  try {
    await api.updateSkill(s.name, { [key]: s[key] })
  } catch {
    s[key] = !s[key]
  }
}
async function remove(s: Skill) {
  await ElMessageBox.confirm(`删除技能「${s.name}」？已经装到员工电脑上的副本会在下次同步时移除。`, '删除技能', {
    type: 'warning',
    confirmButtonText: '删除',
  })
  await api.deleteSkill(s.name)
  ElMessage.success('已删除')
  await load()
}
</script>

<template>
  <div class="page">
    <div class="page-head">
      <div>
        <h1>技能库</h1>
        <p>集中管理员工端可用的技能包。「必装」的技能会自动下发到每台电脑，其余的员工可以在技能市场里自行安装。</p>
      </div>
      <div class="toolbar">
        <el-input v-model="keyword" :prefix-icon="Search" placeholder="搜索技能" clearable style="width: 200px" />
        <el-button :icon="Refresh" :loading="loading" @click="load" />
        <el-button type="primary" :icon="UploadFilled" @click="openImport">导入技能</el-button>
      </div>
    </div>

    <div class="panel">
      <div class="panel-title">
        <h3>全部技能 <small>共 {{ skills.length }} 个，其中 {{ requiredCount }} 个必装</small></h3>
      </div>
      <el-table :data="filtered" v-loading="loading" empty-text="技能库是空的，点右上角「导入技能」">
        <el-table-column label="技能" min-width="260">
          <template #default="{ row }">
            <div class="skill">
              <strong>{{ row.name }}<span v-if="row.version" class="ver">v{{ row.version }}</span></strong>
              <small>{{ row.description || '没有描述' }}</small>
            </div>
          </template>
        </el-table-column>
        <el-table-column label="作者" width="120">
          <template #default="{ row }">{{ row.author || '—' }}</template>
        </el-table-column>
        <el-table-column label="来源" min-width="180" show-overflow-tooltip>
          <template #default="{ row }"><span class="muted">{{ row.origin || '—' }}</span></template>
        </el-table-column>
        <el-table-column label="大小" width="110" align="right">
          <template #default="{ row }">{{ bytes(row.size) }} · {{ row.file_count }} 个文件</template>
        </el-table-column>
        <el-table-column label="更新时间" width="150">
          <template #default="{ row }">{{ dateTime(row.updated_at) }}</template>
        </el-table-column>
        <el-table-column label="必装" width="80" align="center">
          <template #default="{ row }"><el-switch v-model="row.required" :disabled="!row.enabled" @change="patch(row as Skill, 'required')" /></template>
        </el-table-column>
        <el-table-column label="上架" width="80" align="center">
          <template #default="{ row }"><el-switch v-model="row.enabled" @change="patch(row as Skill, 'enabled')" /></template>
        </el-table-column>
        <el-table-column width="80" align="right">
          <template #default="{ row }">
            <el-button link type="danger" :icon="Delete" @click="remove(row as Skill)">删除</el-button>
          </template>
        </el-table-column>
      </el-table>
    </div>

    <el-dialog v-model="importDialog" title="导入技能" width="560px">
      <el-segmented
        v-model="importMode"
        :options="[
          { label: '从链接导入', value: 'url' },
          { label: '上传 zip', value: 'file' },
        ]"
        block
      />
      <div class="import-body">
        <template v-if="importMode === 'url'">
          <el-input v-model="importUrl" :prefix-icon="Link" placeholder="https://github.com/owner/repo/tree/main/skills/xxx" />
          <p class="hint">支持 GitHub 仓库或子目录页面链接，也支持任意技能包 zip 的下载链接。一个仓库里有多个技能时会全部导入。</p>
        </template>
        <el-upload v-else drag :http-request="upload" :show-file-list="false" accept=".zip" :disabled="importing">
          <el-icon class="el-icon--upload"><UploadFilled /></el-icon>
          <div class="el-upload__text">把技能包 zip 拖到这里，或<em>点击选择</em></div>
          <template #tip><p class="hint">内网离线环境用。zip 里每个含 SKILL.md 的目录算一个技能。</p></template>
        </el-upload>
        <el-checkbox v-model="importRequired">设为必装（自动下发到所有电脑）</el-checkbox>
      </div>
      <template #footer>
        <el-button @click="importDialog = false">取消</el-button>
        <el-button v-if="importMode === 'url'" type="primary" :loading="importing" @click="importFromUrl">导入</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<style scoped>
.skill {
  display: flex;
  flex-direction: column;
  line-height: 1.4;
}
.skill strong {
  font-weight: 500;
}
.skill small {
  display: -webkit-box;
  overflow: hidden;
  color: var(--ink-faint);
  font-size: 12px;
  -webkit-box-orient: vertical;
  -webkit-line-clamp: 2;
}
.ver {
  margin-left: 6px;
  color: var(--ink-faint);
  font-size: 11.5px;
  font-weight: 400;
}
.import-body {
  display: flex;
  flex-direction: column;
  gap: 12px;
  margin-top: 16px;
}
</style>
