# 编码模式的语言服务（LSP）

开发部门在编码模式里写代码时，**语言服务**提供：跳到定义、查找引用、补全、悬停看类型、函数签名、重命名、报错波浪线。AI 也会用它检查自己改的代码（`code_diagnostics` 等工具）。

语言服务由 **IT 统一安装**，员工电脑上的客户端不会从网上下载。没装的语言，编辑器照常能用，只是没有这些功能。

## 前提

- 安全中心打开「**开发者工具**」（编码模式本身也要它）。
- 员工第一次在某个项目里用时，编辑器上方会问「**信任并启用**」：语言服务会运行项目里的部分代码（比如 TypeScript 插件、构建脚本），和 VS Code 的「信任此文件夹」一样，只在员工信任的项目里启动。

## 支持的语言和安装方法

| 语言 | 语言服务 | 安装（装在员工电脑上，管理员身份） | 依赖 |
| --- | --- | --- | --- |
| TypeScript / JavaScript / React | typescript-language-server（TS 5 / 6）或 TypeScript 7 自带的 `tsc --lsp` | `npm i -g typescript typescript-language-server` | Node.js |
| Vue | @vue/language-server | `npm i -g typescript @vue/language-server` | Node.js |
| Python | pyright | `npm i -g pyright` | Node.js |
| Go | gopls | `go install golang.org/x/tools/gopls@latest` | Go |
| Java | Eclipse JDT Language Server（jdtls） | 下载 jdtls，把启动脚本 `jdtls` 放进 PATH | JDK 17+ |
| C# | csharp-ls | `dotnet tool install -g csharp-ls` | .NET SDK |

TypeScript 会优先用项目自己 `node_modules` 里的版本（和项目一致）；项目里没有时用和语言服务装在一起的那个。装了 TypeScript 7 时，如果 typescript-language-server 起不来，会自动改用 `tsc --lsp`。

## 客户端去哪找

按顺序找，找到第一个能用的：

1. `C:\ProgramData\FlyknitBuddy\lsp\lsp.json` 里写的命令（见下）；
2. `C:\ProgramData\FlyknitBuddy\lsp\` 目录下，以及它的 `bin\`、`node_modules\.bin\`；
3. 系统 PATH（比如 `npm i -g` 装到的 `%APPDATA%\npm`）。

推荐统一装到 `C:\ProgramData\FlyknitBuddy\lsp\`：这个目录只有管理员能改，员工和项目里的文件改不了启动命令。例如：

```bat
cd /d C:\ProgramData\FlyknitBuddy\lsp
npm i typescript typescript-language-server @vue/language-server pyright
```

## lsp.json（可选）

放在 `C:\ProgramData\FlyknitBuddy\lsp\lsp.json`，用来指定命令或关掉某种语言：

```json
{
  "servers": {
    "python": { "command": "D:\\tools\\pyright\\pyright-langserver.cmd", "args": ["--stdio"] },
    "java": { "command": "D:\\tools\\jdtls\\bin\\jdtls.bat", "args": ["-data", "{data}"] }
  },
  "disabled": ["csharp"]
}
```

- `servers` 里的 id：`typescript`、`vue`、`python`、`go`、`java`、`csharp`；
- `{data}` 会换成这个项目的缓存目录（jdtls 需要）；
- `disabled` 里的语言不启动，员工看到「IT 已关闭」。

## 资源占用

每个语言服务是一个后台进程，常见占用几百 MB（Java 更多）。客户端同时最多开 4 个，空闲 10 分钟自动关，退出客户端时一起关。

## 怎么确认装好了

员工在编码模式标签栏右边的「**…**」里能看到「语言服务」一栏：每种语言显示「可用 / 运行中 / 没装 / IT 已关闭」，鼠标移上去显示找到的程序路径，或者安装方法。
