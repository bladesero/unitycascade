# Shuriken Cascade

Unity 2022.3 的独立 Shuriken Prefab 编辑窗口；时间轴和参数编辑均为 Editor 代码。

结构设计与更新记录见 [ARCHITECTURE.md](ARCHITECTURE.md)。当前设计（2026-09-14）：Emitter 文件夹 → 已启用模块 → 自动发现 Curve／Gradient 参数，点击打开 Unity 官方编辑器；发射区间与 Burst 直接在时间轴编排；预览支持 Transform／Shape Gizmo。

## 使用

1. 选择 **Tools → VFX → Shuriken Cascade**，拖入普通特效 Prefab；也可以在 Project 中右键选择 **Open in Shuriken Cascade**。
2. 左侧预览：拖动旋转、中键或 Shift+拖动平移、滚轮缩放，F 或「聚焦」对准效果。工具栏提供播放／暂停、重播和 0.25–4 倍速；「背景」色块调整背景，「后处理」开关与「环境设置…」配置预览 Volume。
3. 右侧每个 ParticleSystem 一列，列内直接使用 Unity 原生 Particle System Inspector 编辑参数，包括 Renderer、Transform 和曲线。每列独立纵向滚动，发射器区域横向滚动；不再显示旧模块块列表或底部「模块参数」页签。
4. 拖动预览与发射器之间的竖向分隔条调整左右比例；拖动时间轴上边线调整上方面板与时间轴，拖动性能面板上边线调整相邻区域。Esc 取消本次拖动，顶部「重置布局」恢复默认尺寸。
5. 点击「保存」或在窗口内按 Ctrl+S（macOS 为 Cmd+S）写回原 Prefab。「还原」重新读取磁盘版本。

修改参数后预览从起点重播；暂停时保持暂停。显示／独显、预览种子及相机设置不会保存进 Prefab。独显只切换 Renderer 可见性，不停掉父子发射器模拟。结构操作及 Undo／Redo 会清除显示筛选。

## 原生 Inspector

- 原生 Inspector 直接嵌入每个发射器列，复用 Unity 的字段、条件显示、Burst 列表、渐变和模块折叠；上方显示 Transform，下方的 Particle System Curves 提供原生曲线编辑区，默认折叠，按需展开。
- 列头保留选中、重命名、显示／独显、复制发射器和删除；区域工具栏提供新增。点击列、时间轴轨道或性能表行会联动选中发射器，从时间轴和性能表选择时自动横向定位到对应列。
- 每列顶部的模块下拉框用于跨列复制／粘贴，所有列共用所选模块类型，方便复制到其他发射器。该选择独立于原生模块的折叠状态。
- 原生 Inspector 直接编辑加载的工作副本。普通输入、曲线和渐变弹出编辑完成后会检测真实数据变化，再标脏和重建预览；展开模块或改变面板布局不会标脏。保存前也会检查延迟提交的修改。
- 工作副本置于停用的临时编辑容器中，保留资产原本的 activeSelf，不让原生 Inspector 自动播放作者对象。预览仍模拟另一份副本；临时容器不进入 Prefab 保存内容。
- 嵌套 Prefab 的参数保持只读；原生 Play On Awake 等操作对嵌套内容产生的联动修改会恢复。新增场景引用、自引用或循环子发射器引用会被校验并退回。Renderer 材质字段仅替换引用，不嵌入共享材质编辑器。
- 原生 Inspector 自带的 Open Editor 按钮属于 Unity 的独立粒子编辑窗口，其展开状态及曲线界面可能与 Unity Inspector 联动；日常播放和定位使用 Cascade 的预览工具栏。
- 仅为横向视口中的列绘制原生 Inspector，离屏列用等宽占位维持位置；可见列复用 Editor。离屏 Editor 在短暂缓冲后释放，当前选中列保留以接收曲线／渐变弹窗的延迟提交；再次滚回时按需重建。结构变化、Undo／Redo、切换资产、关闭或编译时也会释放。实现通过公开的 `Editor.CreateEditor`、`OnInspectorGUI` 和 `OnPreviewGUI` 接口嵌入，不反射调用内部模块 UI。

## 发射器选择与删除

- 列头复选框独立勾选／取消该发射器，不清除其他勾选，允许全部取消；点击层级名称进行单选。按住 Shift 点击另一列的复选框或层级名称，按发射器列顺序选中连续范围。反向选择和跨横向滚动选择均可用，选中列高亮并在工具栏显示数量。
- 点击工具栏「删除选中」、任一已选列的「删除」，或在发射器选择获得焦点时按 Delete，直接删除当前选择及其子树，不弹确认对话框。点击未选列的删除按钮只删除该列。
- 批量删除作为一次 Undo，Ctrl+Z 可同时恢复节点和 Sub Emitter 引用。父子发射器同时选中时只删除一次子树。修改仍需显式保存才会写回 Prefab。
- 所有目标在修改前统一检查；若包含根节点、嵌套 Prefab 子树或被嵌套发射器引用的子树，整批删除停止，在窗口内显示原因。
- 时间轴和性能表共用选择与高亮；Shift 范围始终按发射器列顺序计算，不受性能表排序影响。多选用于删除，原生参数、显示／独显、复制发射器和模块剪贴板仍按所在列操作。
- 编辑文本、原生字段或其他控件持有键盘焦点时，不将 Delete 解释成批量删除。

## 面板尺寸与分隔条

- 面板矩形由统一布局计算器分配，不再由各区域独立估算剩余空间。左右宽度按用户比例保存；时间轴和性能内容区保存期望高度，上方面板使用剩余高度。
- 竖向分隔条调整预览／发射器列；时间轴上方横线调整上方面板／时间轴，保持性能区不动；性能面板上方横线调整时间轴／性能区，保持上方面板不动。时间轴折叠时，后者调整上方面板／性能区。
- 拖动以按下时的实际尺寸和鼠标总位移计算，达到最小尺寸时停止，不会逐次叠加产生跳变。Esc 恢复拖动前设置。
- 缩小窗口时按统一最小尺寸压缩可用空间，保留期望高度，放大后可恢复。折叠区仅保留工具栏，其余空间让给展开区域。
- 左右比例、折叠状态和下方面板高度保存为编辑器偏好，不进入 Prefab 或资产 Undo。顶部「重置布局」恢复 35% 的预览比例、默认下方面板高度、展开时间轴和折叠性能区。

## 多发射器绘制优化

- 11 个发射器无需每帧完整绘制 11 份 Inspector；小窗口只绘制可见的约 2–3 列，大窗口按实际视口计算。可见范围在 Layout 时确定，同一组输入／Repaint 事件保持一致，防止滚动引发 GUILayout 控件错位。右侧宽度显式限定，各列恢复 IMGUI 的宽屏、标签、缩进及颜色状态，避免互相污染。
- 全窗口共享一份嵌套对象保护和引用校验基线。普通轮询只检查对象的 dirty version，只有发生变化才对文档序列化比对；原生 GUI 输入、保存和关闭仍完整确认变化。弹窗晚提交和屏幕外参数修改仍由共享跟踪器处理。
- 自动 UI 重绘在播放／定位时限制为 30 Hz，暂停且聚焦时为 10 Hz；用户输入仍即时响应。模拟位置与轨道继续按固定 60 FPS 推进，重绘频率不改变模拟步长。
- 暂停且相机、尺寸、可见性和模拟帧未变化时复用预览纹理。变化后重新提交渲染；统计只采集真实渲染调用。此优化减少窗口空闲绘制开销，不代表降低粒子本身的模拟复杂度。

## 时间轴与性能分析

底部「分析」分栏提供 **性能分析／资源引用／Shader 分析** 三个 Tab，共用折叠状态和高度分隔条。点击 Tab 自动展开，选中的 Tab 保存在本机编辑器偏好。

### 资源引用与 GPU 资源容量

- 「资源引用」扫描当前工作副本，包括未保存替换、所有 Renderer、Trail Material、禁用节点、嵌套节点和组件直接引用的 Material／Texture／Mesh／Sprite。展开材质声明的纹理槽与 Sprite 纹理，不递归任意 ScriptableObject 依赖。
- 可按材质、纹理／RT、网格筛选，搜索名称或路径，按名称、引用数、容量排序。点击资源查看节点／组件／属性来源；点击粒子引用联动发射器，Project 定位只选中资源，不修改资产。
- 材质行显示 Shader 和关联纹理数，详情显示关键字；纹理行显示尺寸、格式、层／面和 Mip 数。引用共享资源按 GUID＋Local File ID 去重，临时对象按会话身份区分。材质关联纹理小计可能重叠，全局总计重新去重。
- 「性能分析」新增纹理、网格、已知 RT 和可估算合计；预览 Volume 资源与工具的预览／Gizmo RT 另列。纹理按当前导入格式的完整 Mip 链计算，网格按顶点／索引缓冲计算，RT 区分颜色、深度、MSAA 与 resolve 表面。
- 这是资源容量估算，不是实时驻留显存。CPU 可读副本、粒子／Trails 动态缓冲、驱动分配、Shader 程序和 URP 内部临时 RT 未计；无法可靠读取的资源显示 N/A。

### Shader 静态指令分析

- 选择材质、SubShader 和 Pass，点击「分析选中」；「分析全部」处理引用材质当前 SubShader 的启用 Pass。VS／PS 分开列出，显示 ALU、总指令、采样／加载、分支及临时寄存器，可排序并查看诊断。
- 固定为 **Windows64／D3D11 静态比较基准**，不切换实际 D3D12 预览或修改项目设置。编译使用按钮点击时冻结的材质与相关全局关键字；详情显示关键字、Tier 和统计来源。
- 优先读取 D3DReflect 统计。当前 Unity 给外部工具的 DXBC 会剥离 STAT；此时使用系统 D3DDisassemble 对真实编译指令保守分类，并明确标注来源。向量指令计一次，不推算循环执行次数或标量运算数。DXIL、未知指令、反射／编译失败显示 N/A 和原因，不返回假零值。
- 编译按变体排队，每次编辑器更新处理一项；单次原生同步编译可能短暂阻塞，「取消」在项间生效。播放和绘制不会自动编译。结果缓存按 Shader 依赖、平台、Pass、阶段、关键字和 Tier 区分；资源变更后标记过期，手动重新分析。
- 性能页显示有效覆盖数及 VS／PS 最大 ALU 项，可跳转查看详情。这些数值不代表实际 Draw Call、GPU 周期、耗时或整个特效的 ALU 总量。

- 时间轴位于预览和发射器列下方，可折叠，并拖动上边线调整高度。折叠状态与高度保存在 Editor 偏好中。
- 点击或拖动刻度／Emitter 概览轨道空白处定位会暂停预览；白线为实际到达时间，黄色线为重算目标。定位完成后保持暂停，点击「播放」继续。数字输入与前／后一帧按固定 60 FPS 吸附，逐帧不受播放倍速影响。展开后的时间类参数与全局时序对齐，非时间参数显示速度、长度等自身横轴；点击参数行不触发全局定位。
- 默认预览范围为 0–10 秒，结束时间可设为 1–600 秒。到末尾暂停；打开「区间循环」后，在 A／B 之间播放。循环返回 A 时从零重算，保留 A 之前产生的粒子与拖尾。
- 每个发射器一条轨道，点击名称与发射器列选中联动。发射区间、循环周期和 Burst 按发射器 Simulation Speed 换算；随机延迟显示范围，概率 Burst 用空心标记。轨道是配置示意，不代表粒子存活时间；事件驱动子发射器不显示虚构的绝对起点。密集周期／Burst 标记会合并以限制绘制开销。
- **拖动发射区间修改 Start Delay，拖动 Burst 标记修改该 Burst 的 Time。** 标记优先响应，重复／循环标记共享同一原始 Burst；不改变 Duration、Count、Cycles、Interval 或 Probability。拖动位移按预览时间的 1/60 秒吸附，再换算为发射器时间，不受播放倍速影响。随机常数延迟的两端一起移动、宽度不变，最早时间不小于零。
- 拖动时显示高亮位置和参数值，松手提交一次并从零重播，保留播放／暂停状态；不会在每次鼠标移动时重建预览。一次拖动对应一次 Undo／Redo，只有点击「保存」或 Ctrl+S 才写回 Prefab。Esc、失去窗口焦点、切换资产、参数改变或编译会取消未提交拖动；只点击区间、不移动不会改参数。
- 嵌套、父事件驱动、Simulation Speed 为零及曲线延迟轨道不开放绝对时间拖动。循环 Prewarm 忽略 Start Delay，因此该区间不能拖动；Burst 仍可编辑。合并后没有独立标记的密集 Burst、重合标记及特殊延迟模式可通过原生 Inspector 精确编辑。
- 前进、倒拖和播放使用同样的固定模拟步长。倒拖从起点重算，自动随机种子只在预览副本内固定。长定位按约 8 ms 更新预算分段，预算在模拟步之间检查；可随时拖到新目标或取消，单次重型模拟调用仍可能超出预算。
- 下方可折叠的「性能分析」面板显示总量和逐发射器的粒子数、已观察峰值、粒子上限、材质引用、几何估算及 CPU 调用耗时；点击表头按粒子数、三角形估算或模拟耗时排序，点击名称选中发射器。拖动面板顶部边线调整高度，折叠状态和高度保存在 Editor 偏好中。
- 模拟 CPU 按 1/60 秒模拟步记录，渲染提交 CPU 按预览提交记录，显示最近 120 次有效样本的均值／峰值；定位重算耗时单列，不混入播放样本。父系统耗时包含它触发的子系统，子发射器显示「计入父系统」。未采样发射器不显示为已测得零开销。
- 隐藏粒子仍计入模拟统计。可绘制几何总量排除隐藏、禁用 Renderer 与 Render Mode=None；Billboard 按每粒子 4 顶点、2 三角形估算，Mesh 按已配置网格的最大复杂度估算上界，无法可靠读取时显示 N/A。Trails 不纳入几何估算；材质总量按配置引用去重。
- 这些数值不代表独立特效的 GPU 时间、实际 Draw Calls、Overdraw 或最终设备帧率，也不包含相机裁剪结果。统计表以 10 Hz 刷新，粒子峰值在每个模拟步记录。
- 普通重播、定位与区间循环保留已观察峰值；「重置统计」从当前状态重新开始。参数修改／Undo／Redo／结构变化会重建预览并清空统计，保留播放或暂停状态。切换资产恢复默认时间范围；编译重载后时间归零，历史样本不恢复。
- 定位、播放、统计与布局操作不修改 Prefab、不增加资产 Undo 记录；拖动区间／Burst 属于参数编辑，会标记未保存。关闭、切换、编译或丢弃编辑会取消待完成定位及未提交拖动，并释放预览资源。

## Emitter 文件夹与参数轨道

- 点击时间轴中 Emitter 左侧的三角展开文件夹，显示 Main 和已启用的内置模块。所有已启用模块都会自动发现当前为 Curve／Two Curves／Gradient／Two Gradients／Random Color 的参数，有可展示参数的模块可继续展开。文件夹只是视图，不改变 Transform 层级；其展开状态独立于原生 Inspector，切换 Prefab 时清空。
- **Emitter 行右键 → 添加模块**：启用选中的内置模块、保留旧参数并展开。已启用模块不能重复添加。**模块行右键**提供复制／粘贴、禁用（保留参数）、重置；复制／粘贴与列头共用剪贴板。重置采用 Unity 默认值，保持模块启用。菜单不再提供编辑模块参数的入口。
- **全局时间对齐**：Color／Size 图形、刻度、网格和播放游标与顶部全局时间尺对齐。默认起点是 Start Delay ÷ Simulation Speed；没有连续发射的纯 Burst 效果，再加上首个有效 Burst 的时间 ÷ Simulation Speed。图形长度是 Start Lifetime ÷ Simulation Speed。例如 Delay=2、Lifetime=3、Speed=1 时，图形显示在全局 2–5 秒，50% 关键点位于 3.5 秒。时间轴仅展示全局时序；点击图形后在 Unity 原生编辑器中编辑参数保存的归一化横轴值（通常为 0–1），全局秒数只用于轨道展示。
- **显示参考**：持续发射与循环效果显示首轮参考，不将同一组生命周期曲线复制为所有出生批次。随机寿命取最大值且不单独标注；曲线型 Start Lifetime 取采样最大值。随机 Delay 取最早起点，概率 Burst 是配置参考而非必然发生的事件；预热采用首轮配置参考。父事件触发、零模拟速度、未知延迟以及零／无限寿命回退到相对百分比轴。Main 行显示采用的参考说明；寿命超出当前范围时可增大预览结束时间。
- **Burst 行**：横轴是发射器自身秒数，只显示原始 Burst，不重复绘制循环／重复实例。拖动 key 修改 Time；选中后直接在轨道下方编辑 Time、Count 模式／值、Cycles、Interval、Probability，右键新增／删除。事件驱动、零速度或特殊 Start Delay 的发射器也能在此编辑相对时间；嵌套 Prefab 仍只读。最多显示 600 个 key，其余可在原生 Inspector 中编辑。
- **Color 行**：渐变轨道只展示颜色变化，点击图形直接打开 Unity 原生 Gradient Editor，颜色／Alpha 关键点、插值和预设都在官方窗口中编辑。Two Gradients 分开显示 A／B。Color／Two Colors 等常量模式不生成渐变轨道，在原生 Inspector 切换为 Gradient 后自动出现。Random Color 保留随机采样百分比轴。
- **Size 行**：曲线轨道只展示形状，点击图形直接打开 Unity 原生 Curve Editor，关键点、切线和预设都在官方窗口中编辑。Two Curves 分开显示 Min／Max；Separate Axes 在模块行切换，按 X／Y／Z 分行。曲线模式与乘数在原生 Inspector 编辑；时间轴保留权重、包裹模式以及未修改轴的数据。
- **自动发现范围**：包括 Main 的起始参数、Emission 的发射率和各 Burst 的 Count 曲线、Shape 的适用速度参数，以及 Velocity、Limit Velocity、Rotation、Noise、Trails、Lights、Custom Data 等启用模块。Two Curves 显示 Min／Max，Two Gradients 显示 A／B；禁用模块、关闭的分轴／Remap、Custom Data 未使用通道等不显示。模块或模式变化后自动刷新，发现过程不改写参数。
- **参数横轴**：生命周期参数沿用全局寿命时序；Main／Emission／Shape 和 Noise Scroll Speed 采用发射器首轮周期，不应用首个 Burst 的出生偏移。By Speed 使用速度范围；拖尾宽度／颜色使用长度，Noise Remap 使用噪声输入；Start Delay、贴图 Start Frame 等使用参数采样轴。非时间参数不显示全局播放游标，具体映射见设计文档。
- 曲线／渐变不再提供轨道拖点、双击加点、右键增删或内联关键点字段；在这些轨道上按 Delete 不会删除关键点或发射器。Burst 的拖动、增删与内联字段继续保留。
- 原生窗口修改写入当前工作副本，支持 Undo／Redo 并刷新预览；只有主窗口显式保存才写入 Prefab。仅点击打开不会标脏资产。切换资产、外部改参或撤销会使旧编辑绑定失效，重新点击轨道后继续编辑。
- 参数树只在文档或折叠状态变化时重建，离屏参数行不绘制；原生弹窗的回传绑定到具体发射器与属性，不随轨道滚动而转移。其他模块完整字段仍由上方原生 Inspector 编辑。

## 背景与 Volume 后处理

- 预览工具栏的「背景」色块立即更新画面；也可打开「环境设置…」调整背景、后处理开关和混合权重。
- 选择项目中的 **Volume Profile**，工具会复制其中的 Override 到独立预览配置；可以直接改参、启用／禁用、添加和移除 Override。未选择 Profile 时提供 Bloom、Color Adjustments、Tonemapping、Vignette 起始配置；后处理默认关闭。
- 参数区域使用 Unity 原生 Volume Profile Inspector，包含各效果专用布局、Override 搜索添加、重置／移除、复制／粘贴和 Undo／Redo。勾选字段左侧的 override checkbox 后该参数才参与混合。「重新载入 / 重置」丢弃临时调参并重新读取所选 Profile。
- 调背景和 Volume 只刷新渲染，暂停时仍停在当前帧，不重播粒子、不修改 Prefab、共享 Profile 或场景 Volume，Undo／Redo 仅作用于临时 Profile 的编辑。背景、开关、权重和 Profile 选择保存在本机编辑器偏好；临时 Override 修改在关闭主窗口或编译后丢弃，切换 Prefab 时保留。
- 后处理接入当前项目 URP，使用独立 Volume Stack；其他管线仍可调背景。支持 URP 自带后处理，依赖场景脚本、场景相机堆叠和特定 Renderer Feature 的完整游戏画面仍需场景验收。后处理的 CPU 提交开销会计入预览渲染统计，Gizmo 不受调色影响。

## 预览 Gizmo

- 在发射器列或时间轴选中一个 Emitter，使用预览下拉工具选择「移动／旋转／缩放」。鼠标位于预览时，Q 返回浏览，W／E／R 切换 Transform 工具；参数写入所选发射器的局部 Transform。移动／旋转可切换局部或世界轴，缩放沿局部轴。
- 「Shape 移动／旋转／缩放」修改 Shape 模块的 Position／Rotation／Scale，不改变 GameObject Transform。Shape 必须已启用。
- 「Shape 尺寸」显示发射形状：Sphere／Hemisphere 编辑半径和厚度；Circle 编辑半径、弧角和厚度；Cone 另提供角度，Cone Volume 提供长度；Box／Rectangle 编辑尺寸；Donut 编辑环半径与管半径；Edge 编辑半径。Mesh／Sprite 类型显示资源包围盒参考，使用 Shape Transform 工具调整位置、旋转和缩放，不编辑共享资源。
- 拖动只更新线框提案，松手一次提交并从零重建粒子预览，保留播放／暂停状态；一次拖动对应一次 Undo／Redo。Esc、失焦、选择或参数变化、切换资产和编译会取消未提交拖动。只有显式保存才写回 Prefab。
- 手柄优先接收左键；空白处或 Alt+左键旋转视角，中键或 Shift+拖动平移，滚轮缩放。工具只作用于当前活动发射器，不对 checkbox 多选列表批量变换。
- 嵌套 Prefab 只读；包含嵌套子树的发射器禁止 Transform Gizmo，避免间接移动受保护内容。Gizmo 使用作者配置，不跟随某个存活粒子的世界位置；Mesh／Sprite 包围盒不是实际发射表面的精确可视化。

## 模块复制

- 在源发射器列头选择模块类型并点击「复制模块」，再在目标列点击「粘贴模块」。支持全部模块，包括 Main、Transform 和 Renderer；粘贴保留目标发射器身份和层级。
- 复制保存当时的快照，包括模块启用状态、隐藏参数、曲线、渐变和数组。之后修改或删除源发射器，不影响不含失效引用的快照。粘贴作为一次操作支持 Undo／Redo，并刷新预览；不会直接保存 Prefab。
- 嵌套 Prefab 可作为复制来源，但不能作为粘贴目标。材质等资产引用可以保留；对源发射器自身的引用会对应到目标，其他临时对象引用只能来自当前 Prefab。失效引用或跨 Prefab 临时引用会阻止粘贴。
- 剪贴板属于当前窗口，关闭窗口或编译重载时清空。

## 编辑范围

支持 Transform、Main、Emission、Shape、Velocity over Lifetime、Limit Velocity over Lifetime、Color over Lifetime、Size over Lifetime、Rotation over Lifetime、Noise、Texture Sheet Animation、Trails 和 Renderer。曲线支持 Constant / Curve / Two Curves / Two Constants，渐变支持全部五种模式；旋转常量与曲线乘数以角度显示。

原只读的 12 个模块现已开放编辑、启用／禁用、复制／粘贴及 Undo／Redo：Inherit Velocity、Lifetime by Emitter Speed、Force over Lifetime、Color by Speed、Size by Speed、Rotation by Speed、External Forces、Collision、Triggers、Sub Emitters、Lights、Custom Data。

各模块字段及条件显示由 Unity 原生 Inspector 提供，展开方式与 Unity Inspector 一致。组件引用通过原生对象字段指定；平面、碰撞体、力场和灯光节点的创建及组件本身的参数仍在 Unity Prefab 编辑模式中完成。

字段来自 Unity 2022.3 的序列化数据；不会通过反射调用 Unity 内部 ParticleSystem Inspector。模块顺序遵循 Shuriken 本身的执行语义。隐藏字段完整保留，材质槽替换引用，不修改共享材质。

发射器可新增、重命名、复制子树和删除子树。Prefab 根节点允许编辑参数，但不提供整棵根节点的复制／删除。嵌套 Prefab 子树只读；包含嵌套实例的祖先不能被复制／删除，引用了待删除发射器的嵌套实例也会阻止删除。Variant 与 Model Prefab 不开放编辑。

## 保存与恢复

- 作者数据通过 `LoadPrefabContents` 独立加载，预览使用另一份克隆。未保存时不写入源资产。
- 关闭或换资产时可保存、放弃或取消。保存失败仍保留编辑；源文件哈希变化时阻止覆盖，需先还原并重新加载。
- 编译前将未保存数据写入 `Library/ShurikenCascade/*.snapshot`。域重载保留作者对象；对象丢失时从快照恢复。此机制用于编译重载，不是跨进程崩溃恢复或项目重开恢复。
- 窗口关闭会释放预览场景、对象和渲染资源。保存不会包含临时预览状态。

## 预览边界

预览走当前项目 SRP 与粒子材质，提供基础灯光及纯色背景。预览克隆不运行游戏脚本；不提供场景脚本驱动、完整场景后处理、自定义 Renderer Feature 效果链或碰撞环境搭建。涉及项目特殊渲染依赖时仍需在实际场景中核对最终效果。

预览保留 Prefab 原始 Layer 及力场、2D 碰撞体启用状态，供模块筛选与模拟使用。只有启用的 Sub Emitters 模块会将被引用发射器交给父系统驱动。Collision／Trigger 回调和 Manual／Trigger 子发射器事件配置可保存，但需要游戏脚本参与的行为须在实际场景验证。

## Distance LOD

在 Cascade 顶部点击 **Distance LOD**，给当前工作副本添加配置；修改后点击主窗口「保存」写回 Prefab。也可以直接给场景特效根节点添加 `Effects/Particle Distance LOD` 组件。新配置默认提供 0 / 20 / 50 世界单位三档，原有 Prefab 不会自动添加组件。

- `Automatic` 按 `Distance Check Time` 秒检查距离（0 为每帧）；`ActivateAutomatic` 在启用时确定一次；`DirectSet` 使用 `Direct LOD`，也可调用 `SetLOD(index)`。这三种选择方式参考 [UE Cascade LOD](https://dev.epicgames.com/documentation/zh-cn/unreal-engine/particle-system-level-of-detail-lod?application_version=4.27)。
- 距离是组件根节点到指定 `Distance Camera` 的世界距离，留空使用 `Camera.main`。没有相机时保留当前参数并重试；激活模式在相机首次可用时确定档位。Unity 世界单位不自动转换为 UE 厘米。
- 每档设置起始距离、连续发射与 Burst 的数量乘数、粒子上限乘数，以及是否关闭 Emission、Noise、Collision、Trails、Lights。乘数基于启用时的原始值；只会关闭原本开启的模块。曲线键、Burst 时间、循环数和概率保持原样。
- 距离达到阈值时降低质量；返回高质量时需额外靠近 `Hysteresis` 距离（最多该阈值的一半，避免无法返回近处档位）。LOD 0 的距离字段不参与判定；距离应递增，重复或乱序阈值会按非递减边界处理。空档位表恢复原始参数。
- 顶部 `LOD 0 / 1 / 2` 下拉框在独立模拟副本中预览指定档位，切换会从零重建预览；不改作者参数，不进入资产 Undo。预览采用手动档位，不随预览相机缩放自动切换。
- 普通切档不重播、不清空整个系统。降低粒子上限可能截断存量；关闭 Emission 只停止新粒子，存量自然结束，不提供停止整个模拟的硬裁剪。此实现提供数量和模块开关 LOD，不是 UE 的完整逐模块参数继承编辑器。
- 组件控制其层级内所有粒子系统；嵌套 `ParticleDistanceLOD` 管理自己的子树。运行时禁用组件会恢复基线。对象池若不切换 GameObject 激活状态，须在 `ParticleSystem.Play` 前调用 `ActivateLOD()`；该方法会重新采集原始设置并按模式选档。控制期间应由本组件独占相关数量与模块开关参数。

配置保存到 Prefab 的运行时组件；`ShurikenCascade.Runtime` 随 Player 编译，编辑器程序集引用它。嵌套 Prefab 本身仍通过原有只读规则保护，外层 LOD 的运行时质量控制可作用于其中没有独立 LOD 控制器的粒子。

## Emitter 画质分级

在 **Distance LOD → 画质分级 · Emitter 启用** 矩阵中，按项目画质逐项勾选各发射器；列头「全开／全关」作用于当前可编辑的发射器。仍需主窗口「保存」写回 Prefab。嵌套 Prefab 和其他 LOD 控制器管理的行只读。

- 画质名称对应 `QualitySettings.names`，不依赖画质序号。规则只保存被关闭的画质名称；未配置的 emitter、新增画质和旧 Prefab 均默认启用。重命名画质会视作新增画质，需重新检查矩阵。
- 画质允许的 emitter 继续使用距离 LOD；画质禁止的 emitter 会立即停止并清空、关闭发射和渲染。停止不递归、不停用 GameObject，因此层级下其他允许的 emitter 仍可运行。
- 默认每 0.25 秒检查项目画质变化。设置菜单可在 `QualitySettings.SetQualityLevel(...)` 之后对特效控制器调用 `RefreshQuality()` 立即应用。三种距离判断模式均支持；空距离档位表也能使用画质开关。
- 被关闭 emitter 的入站 Sub Emitter 连接以概率 0 临时屏蔽，连接顺序、类型和继承设置不变；恢复时还原概率。连接管理范围是当前 Transform 根层级，跨独立根节点的引用需由游戏逻辑管理。质量控制期间不要由其他脚本修改相关发射参数或重排这些连接。
- 主动停止期间将 Stop Action 设为 None，避免触发 Destroy／Disable／Callback。升画质时，恢复原先播放的循环 emitter；一次性 emitter 不重播，子 emitter 等待后续事件。禁用控制器恢复参数和连接，不自动播放。
- 对象池重新启用时自动应用当前画质；不切换激活状态时，在播放前调用 `ActivateLOD()`。`Play(true)` 可能再次启动已关闭的子 emitter，控制器在 LateUpdate 将其停止，发射和渲染限制仍然有效。
- Cascade 顶部画质下拉框与 `LOD n` 组合预览；「画质：跟随项目」会跟随项目变化，指定画质只作用于预览副本。切换从零重建预览，既不改项目实际画质，也不写入 Prefab。
- 画质矩阵支持 Undo／Redo；复制 emitter 会复制其画质规则，删除时清理规则引用。参数编辑和预览使用现有工作副本隔离流程。

## 验证

`Tests/Editor` 提供 EditMode 测试，可在 Unity Test Runner 中运行 `ShurikenCascade.Editor.Tests`。覆盖无修改关闭、曲线／渐变／Burst 往返、同名节点与重命名的本地 ID 保留、子发射器引用、结构撤销、复制重映射、外部冲突、磁盘恢复、嵌套和 Variant 边界、预览隔离与资源释放、模块绘制和实际像素渲染。渲染测试需要图形设备。

最近一次代码验证在同一 Unity 2022.3.37f1 的独立工程中通过 **160 项 EditMode 测试**，报告见 [analysis-results.xml](../../../Artifacts/ShurikenCascade/analysis-results.xml)。保留原有 141 项回归，新增 19 项资源、显存、真实 DXBC、队列／缓存失效和 Tab 用例。已将项目现有 `Assets/Content/Particle System.prefab` 复制到隔离工程，使用当前定制 URP 包验证材质索引、VS／PS 指令分析及三个 Tab，Prefab 文件未改变。完整游戏 Renderer Feature 效果链仍需场景验收。测试职责和历史记录见 [ARCHITECTURE.md](ARCHITECTURE.md)。

项目 Unity 已通过 6401 stdio MCP 确认完成编译，未发现 Cascade 相关错误／警告。当前项目定制 URP 与现有特效 Prefab 的完整人工视觉验收尚未执行。

参数可见性参考 Unity 2022.3 的 [Shape Inspector 源码](https://github.com/Unity-Technologies/UnityCsReference/blob/2022.3/Modules/ParticleSystemEditor/ParticleSystemModules/ShapeModuleUI.cs) 与 [Emission Inspector 源码](https://github.com/Unity-Technologies/UnityCsReference/blob/2022.3/Modules/ParticleSystemEditor/ParticleSystemModules/EmissionModuleUI.cs)，实现不调用这些内部 Inspector 类。

新增模块映射与条件显示参考同版本的 [Collision](https://github.com/Unity-Technologies/UnityCsReference/blob/2022.3/Modules/ParticleSystemEditor/ParticleSystemModules/CollisionModuleUI.cs)、[Sub Emitters](https://github.com/Unity-Technologies/UnityCsReference/blob/2022.3/Modules/ParticleSystemEditor/ParticleSystemModules/SubModuleUI.cs) 和 [Custom Data](https://github.com/Unity-Technologies/UnityCsReference/blob/2022.3/Modules/ParticleSystemEditor/ParticleSystemModules/CustomDataModuleUI.cs) Inspector 定义。
