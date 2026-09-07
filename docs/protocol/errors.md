# 实时协议错误码

V1/V2 WebSocket 失败响应使用 `RealtimeEnvelope` 包装 `ErrorPayload`。能够解析原请求时，响应会回传原 `RequestId`、`OperationId` 和 `ProtocolVersion`。

## 操作号与重试

| 错误码 | 是否可原样重试 | 说明 |
| --- | --- | --- |
| `server_error` | 是 | 结果可能已经提交，必须使用相同操作号和相同载荷重试 |
| `movement_rate_limited` | 等待后可以 | 当前没有提交移动回执；等待预算恢复后可使用同一操作号重试 |
| `operation_conflict` | 否 | 同一角色已用该操作号提交不同命令或载荷 |
| `invalid_operation_id` | 否 | 操作号为空、空白或超过 128 个字符 |
| `operation_expired` | 否 | 旧状态只保留操作号而没有命令指纹，客户端应刷新状态 |

相同角色的成功命令使用相同操作号和相同载荷重放时返回原业务结果与当前快照。不同角色的操作号空间彼此隔离。

## 移动

| 错误码 | 说明 |
| --- | --- |
| `invalid_movement` | 非有限坐标、距离计算溢出或单次距离超过内容版本允许的最大容量 |
| `movement_rate_limited` | 当前服务器时间预算不足 |
| `not_in_zone` | 角色不在区域权威成员列表中 |

移动预算及生命周期语义见[服务器权威移动](../gameplay/movement.md)。

## 报文与版本

| 错误码 | 说明 |
| --- | --- |
| `message_too_large` | 完整 WebSocket 消息超过 64 KiB，服务端发送错误后结束连接 |
| `invalid_envelope` | 二进制外层无法反序列化为 `RealtimeEnvelope` |
| `unsupported_protocol` | 当前只接受协议 V1/V2 |
| `invalid_command` | 命令载荷与消息编号要求的 MessagePack 类型不匹配 |
| `unknown_message` | 协议版本已知，但消息编号在该版本中不可用 |

其他玩法拒绝仍通过相同 `ErrorPayload` 返回；随着 `GAME-002` 等任务完成，本表继续补齐稳定含义和重试建议。
