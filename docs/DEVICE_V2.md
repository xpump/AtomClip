# 0.2.0 设备身份与连接协议

状态：已实现并完成构建与桌面互通 / 模拟验证，待实机验收；这些结果不等同于开发板实测。0.1.1 界面升级无需刷机；以下 v2 身份认证需要升级固件并进行一次物理 USB 绑定。

## 信任边界

`K_device` 为每块板独立随机的 32 字节控制密钥，只经物理 USB 写入及用户主动加密导出/导入。`K_data` 为 32 字节正文配对密钥，独立于控制密钥；既有套件迁移可保留旧正文配对，新设备默认随机生成。USB 主机是可信物理边界，可以读取正文配对信息；BLE 不提供读取任一密钥或无验证认领功能。Windows 用用户级 DPAPI，Android 用 Keystore AES-GCM 保存配置，不在日志记录密钥。忘记只删除本机记录，改绑和恢复出厂必须接入 USB 并明确确认。

ESP32-S3 DeviceId = SHA256(ASCII `AtomClip/DeviceId/v2`、NUL、工厂默认 MAC 的 6 字节)[0..16)。不使用运行时 BLE 地址。未来无可靠工厂 ID 的平台须首次生成并持久存储随机 128 位 ID。

## GATT、广播与 Info

所有整数为小端。UUID 共同后缀为 `-12a5-4ea1-81a0-2c4dd9cd0001`：服务 `a29bc201`，Info READ `a29bc202`，Auth WRITE+NOTIFY `a29bc203`，RX WRITE `a29bc204`，TX NOTIFY `a29bc205`。v2 不发布旧服务，不让旧客户端误连。

Info 固定 40 字节：`ACI2`[4]、协议版本2[1]、Info格式1[1]、BoardProfileId[2]、能力位图[4]、Flash字节数[4]、固件major/minor/patch[3]、保留0[1]、DeviceId[16]、配置标志[1]、保留0[3]。标志位0=已配置控制密钥，位1=正文密钥已配置。能力位0=CDC，1=直接HID，2=PowerShell初始化，3=GBK，4=VUC，5=已知LED，6=已知按钮，7=v2安全会话。

旧式主广播：Flags 3字节 + 128位Service UUID 18字节 + `AC-XXXX` 短名9字节，共30字节。扫描响应只放128位ServiceData：UUID16字节 +13字节提示 +AD头2字节，共31字节。提示为版本[1]、短ID[2]、Profile[2]、能力[4]、FlashMB[1]、固件版本[3]。短ID为完整DeviceId首2字节，只展示和筛选。未认证的广播/Info仅为线索，完整Info在认证证明中绑定。

## 双向认证与会话

Auth 分片传输控制帧：`ACV2`[4]、类型[1]、长度[2]、payload、CRC32[4]；CRC覆盖类型、长度和payload。payload最多256字节，片段空闲超时3秒，整个认证15秒；所有消息均支持 MTU23 的20字节ATT有效负载。

1. ClientHello(1)：目标DeviceId[16] + 新clientNonce[32]。
2. ServerHello(2)：Info[40] + 新serverNonce[32] + HMAC-SHA256证明[32]。
3. ClientProof(3)：客户端证明[32]。
4. ServerFinish(4)：完成证明[32]。客户端验证后才设置连接就绪。

证明为 HMAC(K_device, ASCII角色域 + NUL + Info + clientNonce + serverNonce)。角色域分别为 `AtomClip/v2/server`、`AtomClip/v2/client`、`AtomClip/v2/finish`，不得互换。nonce由平台安全随机源提供，每次连接重新生成。身份不符、证明失败、复制完整ID冲突、超时均关闭连接，不能转连其他设备或降级到旧协议。

HKDF-SHA256：salt = SHA256(`AtomClip/v2/session` + NUL + Info + 双方nonce)，PRK = HMAC(salt,K_device)。四个expand标签为 `AtomClip/v2/c2d/key`、`AtomClip/v2/d2c/key`（输出32字节），`AtomClip/v2/c2d/nonce`、`AtomClip/v2/d2c/nonce`（输出首4字节），标签均包含末尾NUL，按RFC5869加计数1。

会话帧为 `ACS2`[4]、单调序号[8]、明文长度[4]、AES-256-GCM密文、tag[16]。头16字节为AAD；nonce为方向专属4字节前缀+序号8字节。每方向从0严格递增，限制小于2^63；重复、跳号、篡改均断开，旧帧不进入应用。明文最多16525字节，内层为既有ACB1完整帧；重组空闲超时5秒，总超时120秒。PING、状态、回执、HID任务等同样保护。断线清除nonce、序号、解析器、队列、认证状态及派生密钥。

固件认证前禁止USB转发、HID任务和正文通知。认证连接内才允许“识别这块板”：只有Profile声明的LED可闪灯；通用配置无灯时返回不支持，不能猜引脚。错误客户端最多占用15秒，单连接保持。

## USB 管理及绑定恢复

物理USB管理使用ACB1类型7，与原有1–6型正文帧并存。请求：opcode[1]、随机requestId[16]、参数。响应：opcode|0x80、相同requestId[16]、status[1]、结果。CRC、长度、requestId和完整ID均核对。响应不得通过BLE转发。

- 1 Info：返回Info[40]。
- 2 Provision：flags[1] + K_device[32] + K_data[32]。flags位0允许替换已有控制绑定；默认禁止抢先覆盖。成功返回更新Info[40] + HMAC(K_device, `AtomClip/v2/usb-bind`+NUL+Info+requestId)[32]。
- 3 FactoryReset：清除控制绑定并生成新的独立正文密钥，返回Info。必须明确物理确认。
- 4 ReadData：返回Info[40]+K_data[32]，仅物理主机；控制密钥不可读取。
- 5 ConfirmPending：返回Info[40]+HMAC(K_device, `AtomClip/v2/usb-proof`+NUL+Info+requestId)[32]，用于恢复写入中断后的待确认绑定。

客户端在写入前先把候选控制密钥保存为OS加密的待确认记录，收到持久化成功证明后标记有效，避免板已绑定而客户端崩溃丢失唯一密钥。待确认配置不自动重连；可经USB核对恢复。固件以单个NVS配置blob原子提交，保存失败不得返回成功。

## 配置互通

单条绑定明文：`ACBK2`[5] + Info[40] + K_device[32] + K_data[32] + alias字节长度[2] + UTF-8别名（最多160字节）。本地记录另保存待确认状态、当前选中完整ID；OS加密文件不直接跨平台复制。

用户主动导出 `.acbind`：`ACBF2`[5] + salt[16] + PBKDF2次数[4] + nonce[12] + 密文长度[4] + AES-GCM密文 + tag[16]。PBKDF2-HMAC-SHA256，200000次，256位密钥；口令至少12字符。头41字节作为AAD。导入限定上述版本、次数和最大长度，错误口令/篡改不修改本地绑定；跨平台使用同一UTF-8口令语义并做测试。

## 板型与升级边界

首轮实现AtomS3U 8MB及Generic ESP32-S3 SPI NOR 4/8/16MB，要求原生USB，采用当前官方Arduino Core提供的DIO/80MHz预编译配置、不启用PSRAM（SPI NOR必须支持80MHz；不宣称支持仅40MHz器件）。通用板默认无按钮/LED。型号由用户确认，芯片/JEDEC/VID不能识别品牌。

普通升级写清单指定的bootloader、分区表与应用区段，禁止覆盖配置NVS区域。只有检查分区兼容后才能声称保留绑定；不兼容时必须USB备份迁移或明确重新配对。恢复出厂独立确认，不能伪装成普通升级。每段检查芯片、容量、偏移、长度、SHA256及写后摘要。

C3/C6/经典ESP32仅为后续BLE↔串口桥移植路线，不能宣称HID；nRF52840/Pico W系列需专用实现与烧录后端。无BLE的S2、普通Pico等需要额外无线硬件。硬件能力参考[Espressif USB表](https://docs.espressif.com/projects/esp-iot-solution/en/latest/usb/usb_overview/usb_overview.html)、[AtomS3U引脚表](https://docs.m5stack.com/en/core/AtomS3U)，不代表已实测多品牌板。

## 0.2.1 客户端发现与配置迁移

不改变 V2 协议或固件。附近扫描按 BLE 地址去重，读取完整 Info 后才与本地绑定合并；短号、名称和 RSSI 不作为合并凭据。多个地址宣称相同完整编号时保留冲突行。Info 读取未经认证，连接仍重新核对编号并完成双向 HMAC / AES-GCM 会话。有限扫描中未读到 Info 的广播显示“编号未读取”，不会改绑或切换目标。

已移除旧固件 BLE UUID、手动地址模式及明文传输分支。ACBS2 为迁移保留旧字段的读取位置，丢弃旧地址，旧模式目标清空；已有 V2 确认 / 待确认记录保留。新保存始终写零旧模式和空旧地址。旧正文配对是独立的内网兼容数据，不是旧 BLE 连接功能。
