---
name: Sol 画布操作手册
description: 把常见任务翻译成工具调用序列的速查手册:图生视频管线、画质增强、扩图改比例、参考图链式创作、节点布局惯例。涉及具体画布操作时先读它。
slug: canvas-recipes
---

# Sol 画布操作手册

Sol 画布上有五类节点:`text`(提示词/文字)、`image`(图片资产)、`video`(视频资产)、
`imageGen`(图像生成配置)、`videoGen`(视频生成配置)。边从输入指向目标。所有操作通过
Agent 工具完成,本手册把常见需求翻译成最短的工具序列。

## 通用布局惯例

- 从左往右流动:上游输入在左,生成节点居中,输出在右侧自动生成。
- 同一层级的并列节点(如多个候选方案)用相同 `y`、间隔 `x +~520`;上下叠放间隔 `y +~380`。
- 新建节点不传 `position` 时会自动排到最右侧,顺序编排时可以依赖这个行为。
- 建完一段图,可用 `manage_canvas` 的 `auto_layout` 整理,再 `fit_view` 让用户看到全貌。

## 图生视频管线

用户:让这张图动起来 / 生成一段视频。

1. 确认源 `image` 节点(画布上下文里选中的,或 `read_canvas` 找到)。
2. `create_node` kind=`videoGen`,data 填 `providerId`/`modelId`(从现有节点或用户确认取,
   不要编造)、`duration`、`resolution`。
3. 一次 `connect_nodes` 批量连线:`{connections:[{sourceId: 源图, targetId: videoGen}]}`
   (若还要提示词,先建 `text` 节点一起连入)。
4. `run_node` 跑 videoGen。视频是远端任务,慢是正常的,等结果即可,不要重复调用。

## 画质增强

- 放大 2x/4x:`edit_image` operation=`upscale`,factor 1–8。本地像素操作,立即完成。
- 只要局部:先 `crop` 再处理,处理完 `expand` 不回来也行,看用户意图。
- 旋转/翻转:operation=`rotate`(quarterTurns=1/2/3)或 `flip`(axis)。

## 改比例 / 扩图

用户:把 1:1 改成 16:9,别裁掉内容。

1. `edit_image` operation=`expand`,insets 按目标比例算
   (例:1:1 → 16:9,left+right = 宽度的一半,即 top=0, bottom=0, left=0.25, right=0.25)。
2. 再 operation=`outpaint`(带 providerId/modelId/prompt)让模型填充透明区域。

## 参考图链式创作

用户:照这张图的风格再画一张新的。

1. 建 `text` 节点写新图提示词(描述新内容 + 保留风格描述)。
2. 建 `imageGen` 节点;一次 `connect_nodes` 把参考图和提示词都连进 imageGen。
3. `run_node` 生成。多个参考图就都连进去,一条 connections 批量搞定。

## 常见错误对照

| 错误 | 原因与处理 |
|---|---|
| `node_not_found` | 用了上下文快照里的旧 id,先 `read_canvas` 核对 |
| `edge_already_exists` | 连线已存在,不用重连,继续下一步 |
| `connection_would_create_cycle` | 会成环,检查方向:边必须是 输入→配置节点 |
| `generation_node_not_configured` | imageGen/videoGen 缺 providerId/modelId,问用户或读现有节点 |
| `image_model_unavailable` | providerId/modelId 不存在或未启用,让用户在设置里确认 |
