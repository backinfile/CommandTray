# 中转协议 v1

所有 JSON 使用 UTF-8，字段为 camelCase。`POST /v1/devices/pair` 用 `{deviceName,pairingCode}` 换取 `{deviceId,token}`。其他 `/v1` 调用使用 `Authorization: Bearer <token>`。已配对设备共享一个数据集合。

| 请求 | 内容与结果 |
|---|---|
| `GET /healthz` | 返回就绪状态和协议版本。 |
| `GET /v1/devices` | 返回 `{devices:[{id,name,pairedAt}]}`。 |
| `DELETE /v1/devices/{id}` | 当前设备退出配对，其他设备由部署管理员撤销。 |
| `GET /v1/workspace` | 返回 `{revision,document}`。 |
| `PUT /v1/workspace` | 发送 `{baseRevision,document}`，成功时返回新的版本。遇到冲突时以 409 返回服务器的版本和内容。保留本地未提交内容并由用户决定采用或合并哪一版。 |
| `GET /v1/clipboard` | 返回 `{revision,text,deviceId,updatedAt}`。 |
| `PUT /v1/clipboard` | 发送 `{text}` 并返回更新后的记录。 |
| `GET /v1/files` | 返回 `{files:[{id,name,folder,size,sha256,modifiedAt,deviceId}]}`。 |
| `POST /v1/files?name=<name>&folder=<folder>` | 文件内容放在请求体里。名称和目录经 URL 编码，路径使用斜线分隔的相对目录。文件上限为 128 MiB。相同目录、名称和内容的重试会返回已有文件。 |
| `GET /v1/files/{id}` | 下载文件二进制内容，支持 HTTP Range。 |
| `DELETE /v1/files/{id}` | 删除指定共享文件。 |

工作区文档示例

```json
{
  "schemaVersion": 1,
  "activeSpaceId": "dev",
  "spaces": [
    {
      "id": "dev",
      "name": "开发",
      "items": [
        {"id":"note1","kind":"note","x":0,"y":0,"width":300,"height":180,"data":{"text":"发布前核对版本号"}},
        {"id":"tasks1","kind":"todo","x":312,"y":0,"width":300,"height":180,"data":{"tasks":[{"id":"task1","text":"构建发布包","completed":false}]}}
      ]
    }
  ],
  "sshHosts": [],
  "sshKeyEnvelopes": []
}
```

客户端保留自己无法显示的字段和组件。SSH 主机配置使用 `{id,name,address,username,port,keyId}`。画板使用 `data.strokes[]`，每个笔画包含 `color` 和 `points:[{x,y}]`。跨设备私钥使用 `sshKeyEnvelopes[]` 中的 `{id,name,envelope}`，`envelope` 是版本化密文，解锁所需的口令只在设备上输入。
