# 部署 CommandTray 中转服务

发布包中的 `*-image.tar` 与 `*-compose.tar.gz` 可以直接部署。用 `docker load -i <image.tar>` 加载镜像，解压 Compose 包后复制 `.env.example` 为 `.env` 并填写配对码，然后运行 `docker compose up -d`。

源码构建时，从仓库根目录构建镜像，然后进入部署目录，在 `.env` 写入随机配对码。Windows、Linux 和 Android 客户端以中转地址及配对码换取独立的设备凭据。

```sh
docker build -f deploy/relay/Dockerfile -t commandtray-relay:local .
cd deploy/relay
cp .env.example .env
docker compose up -d
curl --fail http://127.0.0.1:58630/healthz
```

Compose 默认将端口绑定到部署主机的回环地址。跨设备使用时，把 HTTPS 反向代理指向该端口，将 HTTPS 地址填入客户端。已配对设备共用一个同步空间。保存好随机配对码，修改配对码并重启服务可更新新设备的入网凭据。

## 备份与恢复

停止服务后，用同一个 Compose 数据卷运行管理员命令。备份存放在数据卷的 `backups` 目录。备份包含已配对设备的凭据摘要和共享文件，适合保存在受控位置。

```sh
docker compose stop relay
docker compose run --rm relay admin backup
docker compose run --rm relay admin backups
docker compose run --rm relay admin restore <backup-id>
docker compose up -d
```

恢复时校验共享文件的大小和 SHA-256，原数据保留在数据卷的 `previous-*` 目录。迁移服务器时，将备份压缩包复制到新实例的数据卷的 `backups` 目录，再按备份 ID 恢复。从旧备份恢复会一并恢复当时的设备名单，使用 `admin devices` 检查，并可用 `admin revoke <device-id>` 撤销。

## 本地 MCP

部署主机上的 MCP 客户端可以使用 `stdio` 启动中转服务的管理员接口。Docker 部署使用下列参数，保留标准输入和输出。

```sh
docker compose run --rm -T relay mcp --stdio
```

可用工具包括读取状态、列出设备与备份、撤销设备、备份和恢复。撤销、备份和恢复使用独占数据目录，运行这些操作前请先停止中转服务。中转服务的 HTTP 接口服务于配对设备的数据同步。
