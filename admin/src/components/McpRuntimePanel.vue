<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { ElMessage } from 'element-plus'
import type { UploadRequestOptions } from 'element-plus'
import { Download, Upload } from '@element-plus/icons-vue'
import { api } from '@/api'
import type { McpRuntimeConf, McpRuntimeItem } from '@/api/types'
import { bytes } from '@/utils/format'

/**
 * MCP 运行环境：npx / uvx 类的连接器需要 Node.js、uv、Python。员工电脑第一次用到时从服务器下载便携版，
 * 连接器要装的 npm / PyPI 包也经服务器转一道并缓存——员工电脑不用装任何东西、也不用上外网。
 */
const conf = ref<McpRuntimeConf | null>(null)
const saving = ref(false)
const busy = ref('')

async function load() {
  conf.value = await api.mcpRuntime()
}
onMounted(load)

async function save() {
  if (!conf.value) return
  saving.value = true
  try {
    const { runtimes: _r, mirror_size: _m, ...body } = conf.value
    conf.value = await api.saveMcpRuntime(body)
    ElMessage.success('已保存')
  } finally {
    saving.value = false
  }
}

async function prefetch(r: McpRuntimeItem) {
  busy.value = r.name
  try {
    conf.value = await api.prefetchMcpRuntime(r.name)
    ElMessage.success(`${r.title} 已下载到服务器`)
  } finally {
    busy.value = ''
  }
}

function uploader(r: McpRuntimeItem) {
  return async (opts: UploadRequestOptions) => {
    busy.value = r.name
    try {
      conf.value = await api.uploadMcpRuntime(r.name, opts.file)
      ElMessage.success(`${r.title} 离线包已上传`)
    } finally {
      busy.value = ''
    }
  }
}

function setVersion(name: string, version: string) {
  if (!conf.value) return
  const o = { ...(conf.value.overrides[name] ?? {}) }
  if (version.trim()) o.version = version.trim()
  else delete o.version
  conf.value.overrides = { ...conf.value.overrides, [name]: o }
}
</script>

<template>
  <div v-if="conf" class="panel runtime">
    <div class="panel-title">
      <h3>
        运行环境
        <small>npx / uvx 类的连接器需要 Node.js、uv、Python：员工电脑第一次用到时自动从服务器下载，连接器要装的包也经服务器缓存，员工电脑不用装、不用上外网</small>
      </h3>
      <el-switch v-model="conf.enabled" active-text="开启" @change="save" />
    </div>

    <template v-if="conf.enabled">
      <el-form label-width="120px" class="form">
        <el-form-item label="npm 源">
          <el-radio-group v-model="conf.npm" @change="save">
            <el-radio-button value="official">官方</el-radio-button>
            <el-radio-button value="npmmirror">npmmirror 镜像</el-radio-button>
          </el-radio-group>
        </el-form-item>
        <el-form-item label="PyPI 源">
          <el-radio-group v-model="conf.pypi" @change="save">
            <el-radio-button value="official">官方</el-radio-button>
            <el-radio-button value="tuna">清华镜像</el-radio-button>
          </el-radio-group>
        </el-form-item>
        <el-form-item label="Node.js 下载">
          <el-radio-group v-model="conf.node_dist" @change="save">
            <el-radio-button value="official">nodejs.org</el-radio-button>
            <el-radio-button value="npmmirror">npmmirror 镜像</el-radio-button>
          </el-radio-group>
          <span class="hint">服务器访问官方源慢或连不上时换成镜像</span>
        </el-form-item>
      </el-form>

      <el-table :data="conf.runtimes" size="small">
        <el-table-column label="运行环境" width="150">
          <template #default="{ row }"><strong>{{ row.title }}</strong></template>
        </el-table-column>
        <el-table-column label="版本" width="170">
          <template #default="{ row }">
            <el-input :model-value="row.version" size="small" @change="(v: string) => { setVersion(row.name, v); save() }" />
          </template>
        </el-table-column>
        <el-table-column label="服务器上" width="140">
          <template #default="{ row }">
            <el-tag v-if="row.cached" type="success" size="small">已缓存 {{ bytes(row.size) }}</el-tag>
            <el-tag v-else type="info" size="small">用到时下载</el-tag>
          </template>
        </el-table-column>
        <el-table-column label="下载地址" min-width="260" show-overflow-tooltip>
          <template #default="{ row }"><span class="muted">{{ row.url }}</span></template>
        </el-table-column>
        <el-table-column width="220" align="right">
          <template #default="{ row }">
            <el-button link type="primary" :icon="Download" :loading="busy === row.name" @click="prefetch(row as McpRuntimeItem)">现在下载</el-button>
            <el-upload :show-file-list="false" :http-request="uploader(row as McpRuntimeItem)" :accept="row.name === 'python' ? '.gz' : '.zip'" class="inline-upload">
              <el-button link :icon="Upload" :disabled="busy === row.name">上传离线包</el-button>
            </el-upload>
          </template>
        </el-table-column>
      </el-table>
      <p class="hint foot">
        服务器也上不了外网时：在能上网的电脑上按「下载地址」下载，再点「上传离线包」。已缓存的 npm / PyPI 包共 {{ bytes(conf.mirror_size) }}。
      </p>
    </template>
  </div>
</template>

<style scoped>
.runtime .panel-title {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 16px;
}
.runtime .panel-title h3 small {
  margin-left: 10px;
}
.form {
  margin: 4px 0 8px;
}
.form :deep(.el-form-item) {
  margin-bottom: 10px;
}
.hint {
  margin-left: 12px;
  color: var(--ink-faint);
  font-size: 12.5px;
}
.hint.foot {
  margin: 10px 0 0;
}
.muted {
  color: var(--ink-faint);
  font-size: 12.5px;
}
.inline-upload {
  display: inline-block;
  margin-left: 12px;
}
</style>
