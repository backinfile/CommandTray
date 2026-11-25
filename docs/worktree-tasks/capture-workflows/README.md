# 截图与录屏

## 用户输入

> 截图和录制视频的小功能也不齐，你继续寻找参考项目，补齐功能
>
> 提交推送发布新版本，更新部署和我本机

## 实现与验证

- 补充捕获来源、历史操作、标注以及录屏设置。实现借鉴了 [ShareX 的区域捕获](https://getsharex.com/docs/region-capture)、[ShareX 的图片编辑器](https://getsharex.com/docs/photo-editor) 和 [OBS 录制指南](https://obsproject.com/kb/standard-recording-output-guide)。
- Windows 构建和编码器失败重试的回归测试通过。合成音视频用于检验 FFmpeg 输出、放弃录制及关闭时保存。用合成图片验证了遮挡、撤销、重做和裁剪，并复测了捕获页的录屏选项、历史筛选和 OCR 控件。
- 实际多屏、物理录音设备及 Tesseract 识别仍需在配有对应设备和依赖的机器上验证。
- 发布使用 `release/suite/1.0.3` 与 `suite-v1.0.3`。
