---
name: 图像提示词工程
description: 结构化图像提示词写法(主体/构图/光影/风格/质量),风格与镜头词汇表,比例与分辨率的取舍,用 count/seed 做受控迭代,配合 edit_image 修图。用户要求写图或改图提示词时读它。
slug: image-prompt-craft
---

# 图像提示词工程

好的图像提示词是"可拍的画面描述",不是形容词堆砌。按下面的结构写,缺什么补什么。

## 提示词结构(按优先级)

1. **主体**:谁/什么,在做什么。具体到可视觉化:"一只橘色的猫趴在窗台" 而非 "一只可爱的猫"。
2. **环境与构图**:地点、时间、视角。视角词:extreme close-up / medium shot / wide shot /
   low angle / bird's-eye view / over-the-shoulder。
3. **光影**:golden hour / softbox studio lighting / rim light / volumetric fog / neon glow /
   overcast diffused light。光影对氛围的影响大于风格词。
4. **风格与媒介**:photorealistic / watercolor / flat vector / 3D render, octane render /
   ukiyo-e / pixel art / claymation。可叠加参考:"in the style of 1990s anime cel"。
5. **画质与细节**:highly detailed / sharp focus / 8k(对支持质量参数的模型写 quality 字段
   更可靠,不要只在提示词里喊 8k)。
6. **负面提示**(模型支持时):明确排除项,用逗号列表,放最后。

长度控制:75–150 词最佳,超过 250 词后段的权重会被稀释。

## 参数怎么选

| 需求 | Sol 参数 |
|---|---|
| 社媒头图/方图 | aspect=1:1 |
| 横幅/壁纸 | aspect=16:9 |
| 手机壁纸/故事板 | aspect=9:16 |
| 要 print 的图 | quality=high, outputFormat=png |
| 便宜快速地探索 | count=4 一次出 4 张挑好的,别建 4 个 imageGen |
| 复现某张好结果 | 记下那次的 seed,改提示词不动 seed |

## 受控迭代

- 用户说"再来几张类似的":**同 provider/model/seed,微调一个变量**(改构图词或光影词),
  用 `run_node` 在同一节点再跑一次。
- 用户说"换个风格":只替换风格段,主体与构图保持不变,便于对比。
- 批量探索不同方向:建多个 imageGen(每个不同风格词),一条 `run_nodes` 并行跑,
  比串行快数倍。

## 与画布工具的配合

- 写好提示词放进 `text` 节点(便于用户后续手改),而不是只塞进 imageGen 的隐藏字段。
- 生成后用 `edit_image` 做裁剪/放大/扩图,而不是重新生成(保形成本更低)。
- 用户对某张输出满意时,把它的 assetUrl 所在 image 节点作为下一个生成的参考图连入。
