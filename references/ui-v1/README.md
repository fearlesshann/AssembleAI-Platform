# 集智 / AssembleAI

Manufacturing AI platform UI mock **v1**. Static login and product shell only — no backend.

制造 AI 平台「集智」（AssembleAI）的界面样例 **v1**。只有登录页和工作台外壳，没有后端。

The shell follows a Linear-inspired black-and-white layout: dark elevation ladder (`#08090a` → `#191a1b`), hairline borders, a left sidebar, and a light theme toggle. Fonts load Inter Variable from Linear’s static host when the network allows it.

外壳是 Linear 风格的黑白界面：深色层级、细分割线、左侧边栏，并支持亮色主题切换。有网络时会加载 Inter Variable。

## Open / 打开

Clone the repo, then either open the file directly or serve the folder. Any non-empty account and password on the login form enters the shell. This is a demo gate in the browser, not real authentication.

克隆后可以直接打开文件，也可以起一个本地静态服务。登录页填写任意非空账号和密码即可进入外壳。这只是浏览器里的演示门槛，不是真实登录。

```bash
# open the file
open index.html          # macOS
xdg-open index.html      # Linux
start index.html         # Windows

# or a simple local server
python3 -m http.server 8080
# http://127.0.0.1:8080/
```

`index.html`, `app.css`, `app.js`, and `inertialWheelScroll.js` are plain static files. `npm install` is not required to view the mock.

`index.html`、`app.css`、`app.js`、`inertialWheelScroll.js` 都是静态文件。看界面不需要 `npm install`。
