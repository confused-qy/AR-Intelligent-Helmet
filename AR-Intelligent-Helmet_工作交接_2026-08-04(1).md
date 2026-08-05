# AR-Intelligent-Helmet 工作交接

更新时间：2026-08-05  
目标平台：Android 12 / RK3588  
Unity：6000.4.1f1，URP，IL2CPP，ARM64

## 1. 必须先确认的项目路径

实际由 Unity Hub 打开的项目已经移动到：

```text
C:\dada_docs\2026\450\AR-Intelligent-Helmet
```

原 Codex 工作区仍指向以下旧路径，该目录此前已经不存在/为空，因此上一任务无法写入实际项目：

```text
D:\桌面\文档\2026\450\AR-Intelligent-Helmet
```

本交接文档现在位于实际项目根目录：

```text
C:\dada_docs\2026\450\AR-Intelligent-Helmet\AR-Intelligent-Helmet_工作交接_2026-08-04.md
```

文件名保留 `2026-08-04`，但正文已更新到 2026-08-05。新任务应直接以 C 盘实际项目作为 workspace，开始前执行：

```powershell
git status --short
```

现有改动属于用户，不要 reset、checkout 或覆盖。当前交接文档仍是未跟踪文件；换电脑前必须把它复制过去，或者先 `git add`、提交并推送，否则另一台电脑执行 `git pull` 不会得到它。

## 2. 当前已经达到的状态

- APK 已经能在 RK3588 上启动并正确显示页面。
- 键盘 `M` 可以控制互动地图显示/隐藏。
- Windows Unity Editor 中，鼠标点击地图可以正常选终点、生成规划路径。
- Pointer 统一输入、隐藏地图时双击打开、显示地图时双击关闭和地图范围越界拦截已经写入 `VRFullMapGoalSelector.cs`，对应提交 `22d8ef4`。
- Pointer 改动仍需在 RK3588 上用外接鼠标、触摸屏或触控板做真机验证。
- 当前玩家仍由 `PlannedPathAutoMover` 按规划路线以固定速度自动移动；WASD 手动移动尚未实现，本文件后半部分给出完整接续方案。

已完成的重要构建配置：

- `ProjectSettings/EditorBuildSettings.asset` 当前只启用了真正的 Demo Scene，位于 Build Index 0：

  ```text
  Assets/SimplePoly City - Low Poly Assets/Demo/SimplePoly City - Low Poly Assets_Demo Scene.unity
  ```

- 当前 HEAD：`b4db7f7 test1`。
- `Assets/XR/Settings/OpenXR Editor Settings.asset` 的本地改动已把：

  ```yaml
  m_vulkanOffscreenSwapchainNoMainDisplay: 0
  ```

  即关闭了 Vulkan 的 Offscreen Rendering Only。

- `ProjectSettings/ProjectSettings.asset` 的本地改动已把 Android Graphics API 改为仅 OpenGLES3：

  ```yaml
  m_APIs: 0b000000
  ```

这些配置使此前的黑屏问题消失。它们目前可能仍是未提交改动，必须保留。

## 3. 当前 Git 状态中观察到的用户改动

2026-08-05 最近一次只读检查显示：

```text
?? AR-Intelligent-Helmet_工作交接_2026-08-04.md
?? Assets.zip
```

重新操作前要再次检查，因为 Unity 可能继续更新这些文件。`Assets.zip` 的来源和用途尚未确认，不要删除、覆盖或提交。

## 4. RK3588 点击终点问题的根因与当前修复状态

主要脚本：

```text
Assets/Scripts/VRFullMapGoalSelector.cs
```

旧实现的 New Input System 分支只处理：

```csharp
Keyboard.current.mKey
Keyboard.current.spaceKey
Mouse.current.leftButton
```

关键代码约在 137–149 行：

```csharp
if (Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame)
{
    if (mouseUsesPointerPosition)
        TrySelectGoalFromMap(gazeCamera.ScreenPointToRay(Mouse.current.position.ReadValue()));
    else
        TrySelectGoalFromMap();
}
```

问题是 RK3588 的定制 Android、触控板或外接指针设备可能被 Unity 上报为 `Touchscreen`/通用 `Pointer`，而不是严格的 `Mouse`。因此旧实现中：

- `M` 键能工作，说明 Keyboard 输入正常。
- 点击不触发，因为代码只检查 `Mouse.current`。
- 项目 `ProjectSettings/ProjectSettings.asset` 中 `activeInputHandler: 1`，表示仅使用 New Input System；Legacy Input 分支不会补救。

场景中的 `VRFullMapGoalSelector` 仍存在：

```yaml
confirmAction: {fileID: 0}
toggleAction: {fileID: 0}
```

因此，虽然 `Assets/InputSystem_Actions.inputactions` 已有包含 Mouse 和 Touch 的 `UI/Click`、`Player/Attack` 绑定，实际场景没有把这些 Action 接入组件。当前修复没有依赖这些场景引用，而是直接读取 `Pointer.current`。

场景也没有 EventSystem、InputSystemUIInputModule、GraphicRaycaster 或地图 Collider。不过当前实现是直接读取设备，然后用射线和平面求交，并不依赖这些 UI/物理组件；不要为了本次修改额外引入一套 EventSystem。

当前代码已改为 `Pointer.current`，可统一接收 Mouse、Touchscreen 和 Pen。双击打开/关闭需要区分单击选点，所以地图显示时的单击会延迟约 `0.35` 秒执行；若同设备在相近位置完成第二次点击，则消费这次双击并关闭地图，不再设置终点。

## 5. 已有运行证据

2026-08-04 的 Windows Editor.log 中，当前 Mouse 分支确实成功执行过：

```text
[VRFullMapGoalSelector] Goal selected. clicked=(752,552) ... hasPose=True
[VRFullMapGoalSelector] Planning succeeded: points=19 length=301.4m turns=17 expanded=156075 Phase=Navigating
```

调用栈来自：

```text
VRFullMapGoalSelector.LateUpdate()
Assets/Scripts/VRFullMapGoalSelector.cs:146
```

所以地图坐标、终点设置和规划器在 Editor 中是可工作的。当前优先修复 Android 指针类型兼容，不要先重写规划器。

## 6. 用户明确要求的新功能

已经实现、待真机验证：

1. 鼠标、触屏和触控板统一支持。
2. 地图显示时，单击/单触地图选择终点。
3. 地图隐藏时，同一指针在约 0.35 秒内、相近位置双击/双触，打开地图。
4. 地图显示时，同一指针双击/双触，关闭地图。
5. 双击的第二次输入会被消费，不能同时触发终点选择。
6. 保留 `M` 键显示/隐藏地图。
7. 地图范围外的点击由 `Rect.Contains` 拦截，不再被夹到地图边缘。

新增但尚未实现的需求：

1. 用键盘 WASD 手动移动玩家。
2. 停止按规划路线匀速强制移动，导航线只负责显示和指引。
3. 评估玩家偏离旧路线后是否需要自动重新规划；详细方案见第 13 节以后。

当前场景的 `showOnStart: 1`。如果希望应用启动时地图隐藏、完全依靠 M 或双击打开，应在 Inspector 取消 `Show On Start`，或把场景序列化值改为 `0`。如果要保持当前启动即显示，则不要改它；双击打开仍可在用 M 隐藏地图后使用。

## 7. 已应用的 Pointer、双击和地图越界修复

实现位于：

```text
Assets/Scripts/VRFullMapGoalSelector.cs
```

代码已包含：

- 用 `Pointer.current` 统一 Mouse、Touchscreen 和 Pen。
- `doublePointerPressOpensMap`：隐藏时双击打开。
- `doublePointerPressClosesMap`：显示时双击关闭。
- `doublePointerPressIntervalSeconds = 0.35`。
- `doublePointerPressMaxDistancePixels = 100`。
- 地图显示时延迟执行单击选点，以便区分单击与双击。
- 双击打开/关闭会清空待处理单击，避免同一操作同时设置终点。
- `SetVisible()` 在显隐变化时清空双击状态。

`UnityEngine.InputSystem.Pointer` 是 Mouse、Touchscreen、Pen 等指针设备的共同基类；`Pointer.press` 对鼠标代表左键，对触屏代表至少一个手指按下。

## 8. 已应用的地图命中范围修复

旧实现先调用 `Mathf.InverseLerp`，再判断结果是否超出 0..1，但 `Mathf.InverseLerp` 本身会夹紧结果，导致越界判断永远无法失败。当前 `TryGetPointOnMap()` 已在求归一化坐标前加入：

```csharp
if (!rect.Contains(new Vector2(localHit.x, localHit.y)))
    return false;
```

因此点击地图矩形之外会直接失败，不再错误选择地图边缘终点。

## 9. 建议验证顺序

### Unity Editor

1. 等待脚本重新编译，确认 Console 无 C# 错误。
2. 地图显示时单击地图道路：应显示黄色终点标记和蓝色规划线。
3. 按 M 隐藏地图。
4. 单击一次：地图保持隐藏。
5. 0.35 秒内在相近位置第二次点击：地图打开。
6. 打开地图的第二次点击不能同时设置终点。
7. 地图显示时双击：地图关闭，不能设置终点。
8. 地图显示时单击：等待约 0.35 秒后正常设置终点。
9. 点击地图范围之外：不能选择地图边缘终点。
10. M 键仍能正常显示/隐藏。

### RK3588 / Android 12

1. 导出 Development Build。
2. 测试 USB/Bluetooth 外接鼠标单击地图。
3. 如果设备有触摸屏，测试单触选点、双触打开和双触关闭。
4. 观察日志：

   ```text
   [VRFullMapGoalSelector] Map opened by a double pointer press ...
   [VRFullMapGoalSelector] Map closed by a double pointer press ...
   [VRFullMapGoalSelector] Goal selected ...
   [VRFullMapGoalSelector] Planning succeeded ...
   ```

5. 如果统一 Pointer 后仍完全收不到输入，下一步 A/B 测试：
   - `Project Settings > Player > Android > Application Entry Point`
   - 从 `GameActivity` 临时改为 `Activity`
   - 重新导出 APK 验证 RK3588 定制系统的输入桥接兼容性

GameActivity 不是当前首要根因。Pointer 支持已经实施，先完成真机验证；仅在仍完全收不到输入时再做 Activity A/B 测试。

## 10. ADB 状态

上一任务尝试启动本机 ADB daemon 时失败：

```text
could not read ok from ADB Server
failed to start daemon
```

因此尚未取得 RK3588 的 logcat。新任务若设备已连接并授权，建议执行：

```powershell
adb devices -l
adb logcat -c
adb logcat -v time | Select-String -Pattern 'VRFullMapGoalSelector|Unity|InputSystem|AndroidRuntime'
```

## 11. 其他已知信息

- Demo Scene 的主要引用完整：navigationManager、gazeCamera、playerView、currentPositionSource 均已绑定。
- `mapPanelLayer` 当前是 Layer 6 `NavGround`，但现有平面求交代码没有使用这个 LayerMask，因此不是本次点击失败的原因。
- Windows Editor 的 OpenXR 日志出现 `XR_ERROR_RUNTIME_UNAVAILABLE`，表示电脑没有 OpenXR Runtime；这与 RK3588 当前 Pointer 输入问题分开处理。
- 不要提交或删除 `.utmp`、IDE 文件等用户改动，除非用户明确要求。

## 12. 可直接交给新 Codex 项目的任务描述

```text
请阅读 AR-Intelligent-Helmet_工作交接_2026-08-04.md，并在
C:\dada_docs\2026\450\AR-Intelligent-Helmet
中继续工作。先检查 git status，保留所有现有用户改动。Pointer 统一输入、双击打开/关闭地图和 Rect.Contains 越界拦截已经完成，不要重复实现。按第 13～20 节先完成“阶段一：只显示导航、WASD 手动移动”：新增 ManualNavigationMover，复用 Player/Move，移动 XR Origin，禁用 PlannedPathAutoMover，地图打开时暂停移动，并用 costmap 拦截不可通行位置。阶段一不要修改 A* 或开启高频重规划。编译验证后汇报修改文件、场景引用和测试结果。不要 reset，不要覆盖 OpenXR/OpenGLES3 配置，也不要处理来源未确认的 Assets.zip。
```

## 13. WASD 需求的评估结论

可以实现，而且“导航路线显示”和“沿路线自动移动”在当前项目里本来就是两套逻辑。

- `PlannedPathAutoMover` 负责把玩家沿 `CurrentPlan` 以固定速度移动。
- `VRFullMapGoalSelector` 负责在全图上绘制当前位置和 `CurrentPlan`。
- `GroundNavigationArrow` 会根据玩家实际位置计算旧路线上的进度。
- 因此只要禁用 `PlannedPathAutoMover`，路线、当前位置标记和地面导航箭头仍可保留。
- 新增 WASD 控制器后，玩家只在按键时移动，不再被规划器强制带着走。

“只显示导航、不强制移动”不需要改 A*，也不要求实时重规划。只有希望玩家偏离旧路线后蓝色路线自动更新，才需要增加持续位姿同步并调整现有重规划判断；这仍是小范围改动，不需要重写规划器。

推荐按两个阶段做：

1. 阶段一先实现 WASD 手动移动，导航显示固定路线，不自动改道。
2. 阶段一在 Editor 和 RK3588 稳定后，再决定是否实施阶段二的阈值式偏航重规划。

## 14. 当前输入、移动和场景事实

### 输入

项目启用的是 New Input System。现有动作资产：

```text
Assets/InputSystem_Actions.inputactions
```

已经存在 `Player/Move` 的 `Vector2` 动作，并绑定：

- W / S / A / D。
- 方向键。
- Gamepad 左摇杆。
- XR Controller 摇杆。
- Joystick。

因此阶段一通常不需要修改 `.inputactions` 文件。推荐让新组件提供可选的 `InputActionReference moveActionReference`；Inspector 未赋值时回退到：

```csharp
InputSystem.actions.FindAction("Player/Move")
```

该动作资产已注册为 project-wide actions。不要在组件禁用时无条件关闭共享 Action，否则可能影响其他输入。暂时也不建议为了 WASD 增加 `PlayerInput`，因为现有 `Keyboard&Mouse` control scheme 同时要求键盘和鼠标；Android 只连接键盘时可能造成方案匹配问题。直接读取 project-wide `Player/Move` 更稳。

### 当前自动移动

自动移动脚本：

```text
Assets/Scripts/PlannedPathAutoMover.cs
```

它会取得 `navigationManager.CurrentPlan`，每帧直接修改 `playerRoot.position`，并可旋转玩家根节点。Demo Scene 中该组件位于 `XR Origin (VR)`，当前配置：

```text
speedMetersPerSecond: 8
updateNavigationPose: false
rotateRootTowardMovement: true
```

WASD 上线时必须禁用这个组件，否则自动移动器和手动移动器会同时写 XR Origin，出现抢位置、抖动或按键无效。

### 玩家对象与碰撞

- 应移动 `XR Origin (VR)` 根节点，不能直接移动 `Main Camera`。
- Main Camera 的姿态由 XR 跟踪驱动，直接改相机 Transform 可能被覆盖或破坏头显跟踪。
- 当前玩家没有完整的 `CharacterController` / Rigidbody 运动体系。
- 城市场景也没有完整可靠的地面与建筑碰撞体。
- 阶段一应固定玩家 Y 高度，并调用 `navigationManager.IsPoseNavigable(position, yaw)` 用 costmap 拦截不可通行位置。
- costmap 拦截能限制离开可通行区，但不等同于完整物理碰撞。以后若要求贴墙滑动、台阶和真实碰撞，再单独引入 `CharacterController.Move` 和场景 Collider。

### 其他移动脚本

`ScriptedTrajectoryFollower` 控制的是后方模拟车辆，不是玩家。它可以继续跟随玩家记录轨迹，不应当因为改 WASD 就直接删除。只读检查发现后车对象可能重复挂了两个启用状态的 `ScriptedTrajectoryFollower`；实施时应先在 Inspector 确认，确认重复后再处理，避免误删用户配置。

## 15. 阶段一：只显示导航、WASD 手动移动

这是当前推荐实施范围。

### 新增组件

建议新增：

```text
Assets/Scripts/ManualNavigationMover.cs
```

建议字段：

```csharp
MotorcycleNavigationManager navigationManager;
Transform playerRoot;
Transform viewTransform;
VRFullMapGoalSelector mapSelector;
InputActionReference moveActionReference;
float speedMetersPerSecond = 5f;
bool cameraRelativeMovement = true;
bool rotateRootTowardMovement = false;
bool blockMovementWhileFullMapVisible = true;
bool restrictToNavigableCostmap = true;
bool updateNavigationPose = false;
```

说明：

- `speedMetersPerSecond` 建议先用 5，便于键盘测试；如需保持原自动移动速度可改回 8。
- `updateNavigationPose` 在阶段一默认 `false`，确保导航只显示选点时生成的固定路线，不因偏航触发自动改道。
- `rotateRootTowardMovement` 默认 `false`，避免键盘平移时主动旋转整个 XR Origin。

### 每帧移动逻辑

1. 从 `Player/Move` 读取 `Vector2`。
2. 使用 `Vector2.ClampMagnitude(input, 1)`，避免同时按 W+D 时斜向速度变成 1.414 倍。
3. 将 Main Camera 的 forward/right 投影到水平面，得到视角相对方向。
4. 只在存在有效输入时计算候选位置。
5. 固定候选位置的 Y 为移动前高度。
6. 若开启 costmap 限制，先调用 `IsPoseNavigable`；不可通行则不移动。
7. 通过后再写 `playerRoot.position`。
8. 地图显示时暂停 WASD，避免用户选点时角色同时移动。

核心逻辑可按以下结构实现：

```csharp
Vector2 input = Vector2.ClampMagnitude(moveAction.ReadValue<Vector2>(), 1f);
Vector3 forward = Vector3.ProjectOnPlane(viewTransform.forward, Vector3.up).normalized;
Vector3 right = Vector3.ProjectOnPlane(viewTransform.right, Vector3.up).normalized;
Vector3 direction = Vector3.ClampMagnitude(forward * input.y + right * input.x, 1f);
Vector3 candidate = playerRoot.position + direction * speedMetersPerSecond * Time.deltaTime;
candidate.y = playerRoot.position.y;
```

实际实现还要处理相机几乎垂直朝上/朝下时投影向量过小的情况，此时回退到 `playerRoot.forward/right`。

### 地图显隐状态

`VRFullMapGoalSelector` 当前没有公开只读显隐属性。建议增加：

```csharp
public bool IsVisible => visible;
```

新移动器读取这个属性；当 `blockMovementWhileFullMapVisible` 为 true 且地图显示时，直接跳过移动。

### Demo Scene 配置

场景：

```text
Assets/SimplePoly City - Low Poly Assets/Demo/SimplePoly City - Low Poly Assets_Demo Scene.unity
```

在 `XR Origin (VR)` 上：

1. 取消勾选 `PlannedPathAutoMover` 的 Enabled，不要直接删除组件，方便回退对比。
2. 添加 `ManualNavigationMover`。
3. `navigationManager` 指向 NavigationSystem 上的 `MotorcycleNavigationManager`。
4. `playerRoot` 指向 `XR Origin (VR)` 自身 Transform。
5. `viewTransform` 指向 Main Camera。
6. `mapSelector` 指向同对象上的 `VRFullMapGoalSelector`。
7. `moveActionReference` 可暂时留空，让代码查找 `Player/Move`。
8. 初始速度建议 5 m/s，确认体验后再调整。

## 16. 阶段一的导航显示为什么仍能工作

禁用自动移动器后不需要补绘图代码：

- 全图当前位置标记直接读取 XR Origin 的 `currentPositionSource`。
- 全图蓝色路线直接绘制 `navigationManager.CurrentPlan`。
- 地面箭头已经按玩家实际位置在旧路线中计算进度。
- 选择新终点时，`VRFullMapGoalSelector` 会把当时的玩家位置同步成新起点并重新规划。

所以阶段一的行为是：

1. 选择终点时生成一次路线。
2. 路线保持显示。
3. 玩家用 WASD 自由移动。
4. 玩家偏离路线时，旧路线仍显示，不强制拉回，也不自动重算。
5. 用户重新选终点时，从当时的玩家位置重新生成路线。

这最符合“只是把导航显示出来，不强制移动”的原始需求，也最容易先验证稳定性。

## 17. 阶段二：偏航后实时重新规划

如果要求玩家偏离旧路线后自动刷新蓝线，需要改少量代码，但不需要重写 A*。

### 必要改动

1. 将 `ManualNavigationMover.updateNavigationPose` 打开。
2. 手动移动时持续调用：

   ```csharp
   navigationManager.UpdatePosition(playerRoot.position);
   navigationManager.UpdateRotationQuaternion(currentHeading);
   ```

3. 保留 Navigation Manager 每 0.1 秒的导航 Tick，不要每帧执行 A*。
4. 修改 `MotorcycleNavigationManager.ShouldReplan()`，让普通偏航阈值真正生效。

当前场景参数约为：

```text
replanDistanceFromPathMeters: 1.2
severeReplanDistanceFromPathMeters: 2.5
minimumReplanIntervalSeconds: 0.75
```

但当前 `ShouldReplan()` 实际只使用严重偏航阈值 2.5 m，`replanDistanceFromPathMeters = 1.2` 没有生效。建议：

- 偏离超过 1.2～1.5 m，并持续一小段时间且满足 0.75 秒限频后重规划。
- 偏离超过 2.5 m 时按严重偏航快速触发。
- 重新回到阈值内后清空普通偏航计时，形成滞回，避免路线边缘反复重算。
- 偏航距离最好计算到“路径线段”的最近距离，而不是只计算到最近路径点。
- 规划失败时增加 1～2 秒退避，不能按当前约 0.2 秒频率连续失败重试。
- 新规划失败时尽量保留旧的有效路线，不要让显示立即消失。

### 性能与能力边界

- 当前全局规划在主线程同步运行，地图约 120 万格；不要做逐帧重规划。
- 合理做法是每 0.1 秒检查一次偏航，但真正 A* 受阈值和 0.75 秒以上冷却限制。
- 这能处理基于静态 costmap 的“走错路后重新找路”。
- 动态车辆目前没有写入 costmap，因此阶段二不会自动绕开实时移动车辆。
- 如果以后要绕开动态障碍，需要单独增加动态障碍 costmap 层；这不是本次 WASD 必需范围。

## 18. 推荐修改文件清单

阶段一：

```text
新增  Assets/Scripts/ManualNavigationMover.cs
修改  Assets/Scripts/VRFullMapGoalSelector.cs
修改  Assets/SimplePoly City - Low Poly Assets/Demo/SimplePoly City - Low Poly Assets_Demo Scene.unity
```

其中 `VRFullMapGoalSelector.cs` 只需增加 `IsVisible` 只读属性；场景修改用于禁用自动移动器并绑定手动移动器。

阶段一通常无需修改：

```text
Assets/InputSystem_Actions.inputactions
Assets/Scripts/PlannedPathAutoMover.cs
Assets/Scripts/MotorcycleNavigationManager.cs
```

阶段二才需要修改 `MotorcycleNavigationManager.cs` 的偏航判断、限频和失败退避。

## 19. WASD 验证清单

### Unity Editor

1. Console 无脚本编译错误和 NullReferenceException。
2. 不选择终点时，按 WASD 只手动移动，不发生自动位移。
3. 选择终点后显示路线，但松开 WASD 时玩家保持静止。
4. W/S 前后、A/D 左右方向符合 Main Camera 的水平朝向。
5. 同时按 W+D 时速度不比只按 W 更快。
6. 玩家 Y 高度不漂移。
7. costmap 不可通行位置能阻止移动，不会穿出道路。
8. 地图显示时 WASD 不移动；关闭地图后恢复。
9. M、单击选点、双击打开和双击关闭地图仍正常。
10. 地面箭头、全图当前位置和蓝色路线仍显示。
11. 确认 `PlannedPathAutoMover` 已禁用，没有两个组件争抢 XR Origin。

如果实施阶段二，再验证：

1. 小幅偏离阈值内不频繁重算。
2. 持续偏离超过普通阈值后路线更新。
3. 严重偏离超过 2.5 m 时能较快重算。
4. 连续规划失败不会每 0.2 秒卡顿重试。
5. 重算期间旧路线仍可显示，成功后再替换。

### RK3588 / Android 12

1. 连接 USB 或 Bluetooth 实体键盘。
2. 验证 W/A/S/D 与方向键均可持续移动。
3. 验证键盘热插拔后仍能读取输入。
4. 验证 M 键与 Pointer 点击/双击不受影响。
5. 观察长按与斜向移动时的帧率。
6. Android 软键盘不适合持续 WASD，不作为本次目标输入设备。

## 20. 换电脑继续工作的注意事项

1. 复制整个项目时，确认本交接文档也在项目根目录。
2. 如果通过 Git 换电脑，必须先把本交接文档提交并推送；它当前是未跟踪文件。
3. 另一台电脑打开项目后先执行 `git status --short` 和 `git log -3 --oneline`。
4. 当前参考 HEAD 是 `b4db7f7`；Pointer/双击/Rect.Contains 改动来自 `22d8ef4`。
5. 不要 reset 或覆盖 OpenXR、OpenGLES3 和 Unity 版本相关配置。
6. 不要处理来源未确认的 `Assets.zip`。
7. 先做阶段一，完成 Editor 验证后再打 Android Development Build。
8. 只有用户确认需要自动改道时，才进入阶段二；不要把“实时规划”与基础 WASD 一次性混在同一补丁里。
