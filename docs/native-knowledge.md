# 知识库原生依赖与验收

这套工具为 Windows x64 提供真实 MinIO、RabbitMQ 和带 pgvector 的独立 PostgreSQL，供知识库摄取与查询联合验收使用。无需 Docker，不注册 Windows 服务，不修改机器 PATH、既有 Visual Studio 或原生 PostgreSQL 安装。

## 固定依赖

完整下载地址、长度、SHA256 和源码提交位于 [`tools/native-knowledge.lock.json`](../tools/native-knowledge.lock.json)。安装器拒绝散列不匹配的下载；源码构建同时核对 Git 标签对应的固定提交。

| 依赖 | 固定版本 | 来源与用途 |
| --- | --- | --- |
| Go | 1.26.10 Windows x64 ZIP | [Go 官方下载](https://go.dev/dl/)；与仓库 MinIO Dockerfile 保持一致 |
| MinIO | RELEASE.2025-10-15T17-29-55Z；`9e49d5e7a648f00e26f2246f4dc28e6b07f8c84a` | [上游源码](https://github.com/minio/minio/releases/tag/RELEASE.2025-10-15T17-29-55Z)，`CGO_ENABLED=0` 原生构建 |
| Erlang/OTP | 28.5.0.8 Windows x64 ZIP | [Erlang 官方发布](https://github.com/erlang/otp/releases/tag/OTP-28.5.0.8)；ZIP 内 `Install.exe -s` 仅准备本地启动文件 |
| RabbitMQ | 4.4.0 Windows ZIP | [RabbitMQ 官方发布](https://github.com/rabbitmq/rabbitmq-server/releases/tag/v4.4.0)；以普通应用进程运行 |
| PostgreSQL | 17.11，EDB build 5 | 复用 [`native-postgres.lock.json`](../tools/native-postgres.lock.json) 已验证的安装；复制独立运行目录 |
| pgvector | 0.8.7；`f37c13f68b57d2c3472b2214fbcff699d6d34876` | [上游源码](https://github.com/pgvector/pgvector/tree/v0.8.7)，使用官方 Windows Makefile 编译 |
| MSVC / Windows SDK | 14.44.35226 / 10.0.26100.15 | 微软 Visual Studio 官方目录的固定 VSIX、MSI、CAB；仅解包所需 x64 编译器、头文件与库 |

微软包的 SHA256 来自已安装的官方 Visual Studio 2022 目录，目录自身 SHA256 也已记录。部分 VSIX 的目录 `size` 与实际 HTTP 长度不同；锁文件分别保留 `catalogSize` 与官方 HTTPS `Content-Length`，安装时同时核对实际长度和原发布者 SHA256。不采用第三方 pgvector DLL。

## 安装

使用 PowerShell 7 或更新版本，并先准备现有 PostgreSQL：

```powershell
pwsh -NoProfile -File tools/install-native-postgres.ps1
pwsh -NoProfile -File tools/install-native-knowledge.ps1 -IncludeVector
```

首次 MinIO 构建需要下载 Go 模块并编译，编译器并发固定为 2。Go 不读取 Windows 系统代理，安装器会将现有系统代理仅传入构建子进程；不会修改代理设置，也不会输出代理地址。其他模型、数据库等环境凭据不会传给依赖构建进程。

若工作区所在盘空间不足，可显式提供另一个盘上的**新目录**：

```powershell
pwsh -NoProfile -File tools/install-native-knowledge.ps1 `
  -BuildCacheRoot E:\assembleai-native-build-local -IncludeVector
```

该目录必须不存在，或已带本工具生成且属于当前工作区的标记。Go 缓存、编译临时文件和微软编译工具放入该目录；正式依赖和安装清单仍位于 `.local-data/native-knowledge`。微软 MSI 只以只读模式访问 File、Component、Directory、Media 表，CAB 由系统 `expand.exe` 解包，不调用 MSI 安装动作，也不更新既有 VS 配置。

SDK 选择包含 PostgreSQL Windows 头文件所需的两组 OnecoreUap 头文件；MSVC 同时包含 Desktop 与 Store 的 x64 CRT 库，覆盖上游 Makefile 的默认库依赖。重复安装逐项比较归档与已有编译器文件的 SHA256，相同文件直接复用。编译结束后仅终止完整路径属于本工具链的 VCTIP 副进程，避免遗留进程占用编译器 DLL；不修改机器的 VS 遥测设置。

`-SkipMinioBuild` 仅准备 Go、Erlang、RabbitMQ，不代表服务可验收。没有已构建 MinIO 时，服务启动器会拒绝运行。

生成的 `installed.json` 不含服务凭据，包含：

- `minioExecutable`、`minioSha256`、`minioCommit`；
- `erlangHome`、`rabbitmqHome`、固定 `versions`；
- `postgresHome`、`vectorVersion`、`pgvectorCommit`（使用 `-IncludeVector` 后）；
- `buildCacheRoot`，用于确认外置缓存目录的归属。

独立 PostgreSQL 位于 `.local-data/native-knowledge/postgresql/pgsql`。原 `.local-data/native-postgres/postgresql/pgsql` 保持无 vector，以保留控制面验收中“扩展缺失应返回 `0A000`”的负例。知识库验收必须读取新的 `postgresHome`，并执行完整迁移与扩展核验。

## 服务生命周期

[`tools/knowledge-native.py`](../tools/knowledge-native.py) 提供上下文管理器：

```python
with NativeKnowledgeServices(run_directory, config_path=None, artifact_dir=None) as services:
    configuration = services.configuration
    sensitive_values = services.sensitive
    # 将 configuration 传给联合验收子进程；不要打印或提交。
assert services.cleanup_ok
```

`config_path` 是依赖安装清单，默认 `.local-data/native-knowledge/installed.json`。配置结构如下，各访问凭据每次随机产生：

```text
minio: endpoint, accessKey, secretKey, bucket, region
rabbit: host, port, managementEndpoint, username, password, virtualHost
```

启动器为 S3、AMQP、管理 API、EPMD、Erlang 分布端口分配随机 loopback 端口。RabbitMQ 使用独立节点名、虚拟主机、账号、cookie、配置和数据目录。服务子进程环境采用允许列表，不继承模型或数据库凭据。凭据不出现在命令行，标准输出经脱敏后写入 `.artifacts/knowledge`。

每次运行在调用方 `run_directory` 下创建带唯一标记的子目录。退出时停止 RabbitMQ、自有进程树及 EPMD，确认端口关闭，再核对标记并清理该次数据。不会递归删除调用方的 PostgreSQL 运行目录，也不会操作系统已有服务。

若清单明确指定另一个盘上的 `buildCacheRoot`，并且其归属标记与当前工作区一致，MinIO 对象数据使用其中 `services/objects-<run-id>` 的独立标记目录。其余配置与日志位置不变；退出时同样核验标记并清理对象目录。

## 独立烟测

```powershell
apps/sample/backend/.venv/Scripts/python.exe tools/knowledge-native.py --smoke
```

烟测验证真实 S3 签名请求的建桶、上传、下载字节一致与删除；通过真实 RabbitMQ 管理 API 创建持久队列、发布、消费、确认并删除；另向真实 AMQP 0-9-1 端口执行协议握手。它不替代平台生产 RabbitMQ 客户端、worker 与数据库的完整联合验收。

输出只包含通过标记，不包含访问凭据或业务数据。成功结果保存在该次 `.artifacts/knowledge/run-<id>/result.json`，其中 `cleanup` 必须为 `true`。

本机 2026-11-05 的服务烟测结果位于 `.artifacts/knowledge/run-c9fc6761d24840dd8cb2bc8a0a13cd21/result.json`，对象往返、队列往返、AMQP 握手和清理四项均通过。带向量扩展的正式安装日志为 `.artifacts/t12-native-vector-linked.log`；原控制面 PostgreSQL 仍无 `vector.control`。平台的 384 维检索、ACL 与删除验证以知识库联合验收结果为准。

## 本机容量与保留目录

2026-11-05 的实测中，D 盘总容量约 138 GiB，空闲约 1.07 GiB 时，MinIO 建桶成功但对象上传返回 `507`。上游源码固定保留 1% 磁盘空间，因此该盘需要约 1.38 GiB 空闲。验收使用已标记 E 盘目录存放对象，不修改 MinIO 的保留空间阈值。

本机本轮外置构建目录为 `E:\assembleai-native-build-20261105-t12-review`。安装包、Go 模块、编译缓存和工具链有意保留以供复验；一个版本参数诊断用的 `minio-probe.exe` 也保留在该构建目录。中断的早期构建目录 `.local-data/native-knowledge/tmp` 的递归清理被自动审批拒绝，仍原样保留，没有换用其他方式删除。服务运行数据由每次验收独立清理，这些保留构建缓存不含服务访问凭据。
