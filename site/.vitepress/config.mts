import { defineConfig } from 'vitepress'

// FlyknitBuddy 帮助中心。构建产物输出到 server/app/static/help，服务端挂在 /help/（/docs 是服务端接口文档，不能占用）。
// 文档面向员工和 IT，只写产品怎么用：不写公司的服务器地址、账号、内部规则，以后对外公开也不用改。
export default defineConfig({
  lang: 'zh-CN',
  title: 'FlyknitBuddy',
  description: '面向工厂办公电脑的 AI 助手：办事、对话、翻译',
  base: '/help/',
  outDir: '../server/app/static/help',
  cleanUrls: false,
  lastUpdated: false,
  head: [['link', { rel: 'icon', type: 'image/png', href: '/help/favicon.png' }]],
  themeConfig: {
    logo: '/logo.png',
    siteTitle: 'FlyknitBuddy',
    nav: [
      { text: '使用指南', link: '/guide/introduction', activeMatch: '/guide/' },
      { text: '管理员', link: '/admin/deploy', activeMatch: '/admin/' },
      { text: '常见问题', link: '/faq' },
    ],
    sidebar: {
      '/guide/': [
        {
          text: '开始',
          items: [
            { text: '介绍', link: '/guide/introduction' },
            { text: '安装与登录', link: '/guide/install' },
          ],
        },
        {
          text: '日常使用',
          items: [
            { text: '三种模式与工作区', link: '/guide/usage' },
            { text: '确认与安全', link: '/guide/safety' },
            { text: '文件、预览与还原', link: '/guide/files' },
            { text: '长任务与定时任务', link: '/guide/tasks' },
            { text: '翻译、截图与快捷键', link: '/guide/shortcuts' },
          ],
        },
        {
          text: '能力扩展',
          items: [
            { text: '专家', link: '/guide/experts' },
            { text: '技能', link: '/guide/skills' },
            { text: '连接器', link: '/guide/connectors' },
          ],
        },
        {
          text: '写程序的同事',
          items: [
            { text: '开发者工具与 PR', link: '/guide/developer' },
            { text: '沙箱任务', link: '/guide/sandbox' },
            { text: '命令行与 IDE', link: '/guide/cli-and-ide' },
          ],
        },
      ],
      '/admin/': [
        {
          text: '管理员',
          items: [
            { text: '部署服务端', link: '/admin/deploy' },
            { text: '配置模型', link: '/admin/models' },
            { text: '装到员工电脑', link: '/admin/rollout' },
            { text: '安全中心', link: '/admin/security-center' },
            { text: '管理后台一览', link: '/admin/console' },
          ],
        },
      ],
    },
    search: {
      provider: 'local',
      options: {
        // 中文没有空格，默认分词会把一整句当成一个词，搜「回收站」只能命中刚好被标点隔开的地方。
        // 建索引时把每段中文拆成它的所有后缀（「进回收站」→ 进回收站 / 回收站 / 收站 / 站），
        // 浏览器端照常按前缀搜（搜索框的分词在浏览器里，函数传不过去，只能在建索引这边处理）
        miniSearch: {
          options: {
            tokenize: (text: string) => {
              const terms: string[] = []
              for (const part of text.split(/[\s\p{P}\p{S}]+/u)) {
                for (const m of part.matchAll(/\p{Script=Han}+|[^\p{Script=Han}]+/gu)) {
                  const run = m[0]
                  if (/\p{Script=Han}/u.test(run)) {
                    for (let i = 0; i < run.length; i++) terms.push(run.slice(i, i + 16))
                  } else {
                    terms.push(run)
                  }
                }
              }
              return terms
            },
          },
        },
        translations: {
          button: { buttonText: '搜索', buttonAriaLabel: '搜索' },
          modal: {
            displayDetails: '显示详细列表',
            resetButtonTitle: '清除',
            backButtonTitle: '关闭搜索',
            noResultsText: '没有找到',
            footer: { selectText: '选择', navigateText: '切换', closeText: '关闭' },
          },
        },
      },
    },
    outline: { level: [2, 3], label: '本页目录' },
    docFooter: { prev: '上一页', next: '下一页' },
    darkModeSwitchLabel: '外观',
    lightModeSwitchTitle: '切换到浅色',
    darkModeSwitchTitle: '切换到深色',
    sidebarMenuLabel: '目录',
    returnToTopLabel: '回到顶部',
    notFound: { title: '页面不存在', quote: '这个地址没有内容，可能是链接写错了或页面已经改名。', linkText: '回到帮助中心首页' },
    // 导航里的链接都会加上 /help/ 前缀，回产品主页（服务端首页 /）只能写原始 HTML
    footer: { message: '<a href="/">FlyknitBuddy 产品主页</a> · 帮助中心', copyright: '仅供公司内部使用' },
  },
})
