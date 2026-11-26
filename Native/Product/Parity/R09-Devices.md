# R-09 · Full 添加设备与设备详情

冻结源码为 `3d9f9e1083b228f666e30d97e3c8edfce5e6654a`。WPF 的实际入口为应用页动态注入的设备侧栏。点击「添加设备」调用 `OpenPhoneManagement(true) → ShowConnection`，点击已保存设备行调用 `SelectProfile → OpenPhoneManagement(false) → ShowDevice`。页面条件为 `_showConnection || Profile.LastDevice == null`。应用详情和设备详情占用同一列。实现位置见冻结的 `MainWindow.PhoneDevices.cs`、`PhoneDevicePage.xaml.cs:177` 和旧 Native 的 `AppsPageView`、`PhoneDevicePageView`。Lite 编译时排除手机页面与关联业务。

| 冻结 WPF 场景与源 | Native 入口／业务 | R-09 处理与验证范围 |
| --- | --- | --- |
| 添加设备类型选择，`PhoneDevicePage.xaml:389`、`PhoneDevicePage.AirPlay.cs` | `phone.connchoice.android/airplay`，共用 `AirPlayReceiverViewControl` | 单行分组按钮。Android 显示配对、直连卡，AirPlay 独占接收器卡。切换或离开会停止二维码及已保存设备发现。已有设备仍由左侧栏进入。 |
| 扫码及六位配对码，XAML:399，`PhoneDevicePage.Pairing.cs` | `phone.qrrefresh / pairaddr / paircode / pairbtn`，`PhonePreview.PairAsync` | 按 0.9∶1 列宽布局，正文按列宽换行，二维码按钮 166 DIP、图像 164 DIP、最近邻采样。配对码为明文，限制六字符，操作前取值并清空。冻结 `SetBusy` 只在操作中禁用配对按钮，空输入仍可提交给配对业务反馈错误；按钮底色来自 `Theme.Overlay`。手工命令等待原二维码任务退休，页面退出与变更不会接受已过期的结果。|
| 直接连接、搜索已保存设备、手工地址、USB 恢复，XAML:428，`PhoneDevicePage.DirectConnection.cs` | `phone.connaddr / saved / searchsaved / connbtn / restoreusb:{identity}`，`DiscoverAndConnectAsync / RestoreUsbAsync` | 按冻结版顺序显示地址、搜索、连接。发现结果不会替代正在编辑的手工地址；下拉选择手工项后聚焦地址框。USB 恢复仅对已发现、可恢复且身份明确的设备可见。刷新沿用应用侧栏入口。连接弹窗与后台结果共用真实手机状态。|
| 设备头，XAML:146，`PhoneDevicePage.xaml.cs:198` | `phone.rename / rename.box / reconnect / disconnect / unpair / remove` | 四个命令按冻结顺序保留。离线设备可重连、删除，取消配对给出离线原因；断开只对在线设备有效。USB 在线时命令文案为「停止接收」。名称编辑与实际绘制使用同一编辑字段，限定 64 字符，实时进 Store。 |
| 音视频流参数，XAML:185，`PhoneDevicePage.Streaming.cs` | `phone.stream.{audioenc,audiodec,audiobr,audiolat,videoenc,videodec,videobr,videofps}`，`AndroidAudioSettingsStore` | 紧凑的两行、8 个下拉，布局与命中共享几何。以已保存设备身份载入/写入配置，离线时也能查看和调整。枚举编码器限在线目标。Raw PCM 禁用音频编码器及比特率。媒体和整机音量控制位于伴随窗。 |
| 固定应用、应用目录，XAML:247、484，`PhoneDevicePage.xaml.cs:418` | `phone.smart:{pkg} / pin:{pkg} / addapps / appsearch / catalog.row:{pkg} / catalog.done`，`Store.Retain / RetainMany / SetSmartVolume` | 固定应用显示原应用名、分类描述、图钉及智能音量。在线时才启用添加。目录覆盖详情、聚焦搜索，列出名称和包名，支持 Ctrl/Shift、Ctrl+A 和右键批量固定。返回恢复详情滚动。列表区首次查冻结格式的设备目录缓存，显式刷新重读当前传输并同步已固定应用的元数据，失败不会标为成功的空目录。已固定图钉使用 45° 实心路径，离线添加按钮以半透明 Overlay 标示禁用。 |
| 文件传输与目录选择，XAML:261、298，`PhoneDevicePage.AndroidFiles.cs:148` | `phone.sendfile / browsefiles / rfile / fileclose / folderup / rfileact / remotefolder / localfolder / cancelfile`，`PhonePreview.EnqueueTransfer` | 冻结版的手机目录浏览就在文件卡内。打开列表时同卡的常规传输内容由 XAML 可见性触发器折叠。Windows 文件与本机目录选择使用系统对话框。发送拖放区域最小高度 104 DIP，只接受真实命中；选文件、切目录、Esc 返回、Backspace 上级、Enter 接收均经现有业务。目录图标在左，文件勾选与文件夹箭头在右。读取超时显示错误、结束等待并允许退出或重新选择目录。关闭视图取消列目录请求，已确认的队列独立继续。 |
| 剪贴板，XAML:377，`PhoneDevicePage.xaml.cs:235` | `phone.clipboard`，`PhoneClipboard.IsEnabled / StatusFor / SetEnabled` | 启用状态、离线等待、提示从当前所选设备读取，离线可配置。实际复制同步需要已授权真机和连接。 |
| 隐藏与资源归属 | `OnDeactivated / CancelViewOperation / CancelEncoderRead / CloseCatalog / CloseAndroidPicker` | 关闭页面清除配对码和二维码、停止发现及该页专属工作。详情不另建媒体监听；目录图标解码结果按代次接收，成功和失败共用有界缓存。 |

**资料与验证**　冻结版使用 920 × 620 DIP、144 DPI、简体中文、浅色，离线详情使用隔离的 Pixel R09 资料与两个固定应用。[添加设备截图](../../../../r09-evidence/wpf-light-connection-2f73d0a929/android-add.png) · [离线详情](../../../../r09-evidence/wpf-light-apps-offline-2171c532d4/device-details.png) · [传输与剪贴板](../../../../r09-evidence/wpf-light-apps-offline-2171c532d4/device-bottom.png)

表中行为均已作源码核对。首轮 Native Full（EXE SHA-256 前缀 `a154178c`）与冻结截图使用相同尺寸、DPI、语言和主题，实际显示了双栏二维码、两行流设置、两条离线固定应用以及文件与剪贴板卡片。该轮图看出配对按钮、图钉和离线添加按钮的样式偏差，已据此完成上述源码修复。[Native 添加设备初次取景](../../../../r09-evidence/native-light-connection-d97c3c2445/android-add.png) · [Native 离线详情初次取景](../../../../r09-evidence/native-light-apps-offline-a2502d80c5/device-details.png)。最终候选的截图、系统输入和构建由 R-09 总报告按候选 SHA 列出。扫码配对成功、无线/USB 连接与断开、真机目录、传输、编解码与音频输出、剪贴板同步需在已获授权的设备上验证。
